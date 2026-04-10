using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace BingeCode
{
    public partial class ToolWindow1Control : UserControl
    {
        private bool             _isBorderless;
        private IntPtr           _thumbnailHandle  = IntPtr.Zero;
        private IntPtr           _sourceHwnd       = IntPtr.Zero;
        // Original top-level window the user picked, used for UIA & keep-alive.
        // May differ from _sourceHwnd when we switch to the render-widget child.
        private IntPtr           _browserTopLevelHwnd = IntPtr.Zero;
        // Best crop rect found via UIA (document or video element). Screen pixels.
        // Empty = nothing found; fall back to HWND-based or full client area.
        private System.Windows.Rect _videoSourceRect = System.Windows.Rect.Empty;

        // Fullscreen overlay window and its own DWM thumbnail handle
        private Window           _fullscreenOverlay  = null;
        private IntPtr           _overlayThumbHandle = IntPtr.Zero;
        private Border           _overlayExitBar     = null;
        // VS root HWND and its saved rect, restored when exiting fullscreen
        private IntPtr           _overlayRootHwnd    = IntPtr.Zero;
        private NativeMethods.RECT _savedVsRect;

        // VS title bar HWNDs hidden during fullscreen
        private readonly List<IntPtr> _hiddenTitleBarHwnds = new List<IntPtr>();

        // Timer that auto-hides the exit button after 10 seconds
        private readonly DispatcherTimer _miniBarTimer;

        // Keeps the source window "awake" so the browser doesn't throttle/pause
        // the video when VS has focus instead of the browser.
        private readonly DispatcherTimer _keepAliveTimer;



        // ── Security allowlists ──────────────────────────────────────────────

        private static readonly string[] AllowedSchemes = { "https://", "http://" };

        private static readonly HashSet<string> AllowedVideoExtensions =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".mp4", ".avi", ".mkv", ".mov", ".wmv", ".webm", ".flv" };

        // Windows system / shell processes that are never video sources.
        // Everything in this list is silently excluded from the picker.
        private static readonly HashSet<string> SystemProcessBlacklist =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "explorer",                  // Program Manager / desktop
            "ShellExperienceHost",       // notification centre, quick settings
            "StartMenuExperienceHost",   // Start menu
            "SearchHost", "SearchApp",   // Windows Search
            "LockApp",                   // lock screen
            "ApplicationFrameHost",      // UWP host frame
            "TextInputHost",             // touch keyboard
            "SystemSettings",            // Settings app
            "msedgewebview2",            // WebView2 runtime (used by shell, not a browser)
            "RuntimeBroker",
            "sihost", "taskhostw", "ctfmon",
            "devenv",                    // Visual Studio (belt-and-suspenders with OwnPid)
            // GPU / overlay processes
            "NVIDIA Overlay",            // GeForce Experience overlay
            "nvcontainer",
            "NVDisplay.Container",
        };

        // Own process ID, never include VS itself in the picker.
        private static readonly int OwnPid = Process.GetCurrentProcess().Id;


        // ── Constructor ──────────────────────────────────────────────────────

        public ToolWindow1Control()
        {
            InitializeComponent();
            // Initialise WebView2 (Edge Chromium) asynchronously once the control is loaded.
            Loaded += OnLoaded;

            _miniBarTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _miniBarTimer.Tick += (s, e) =>
            {
                MiniBar.Visibility = Visibility.Collapsed;
                if (_overlayExitBar != null)
                    _overlayExitBar.Visibility = Visibility.Collapsed;
                _miniBarTimer.Stop();
            };

            // Fire every 800 ms while a source window is mirrored.
            // WM_ACTIVATE + WM_SETFOCUS sent to both the DWM source (render widget)
            // and the browser top-level window keeps the Page Visibility API from
            // firing and fights Chrome's OcclusionTracker throttling.
            _keepAliveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            _keepAliveTimer.Tick += (s, e) =>
            {
                void Ping(IntPtr hwnd)
                {
                    if (hwnd == IntPtr.Zero) return;
                    NativeMethods.PostMessage(hwnd, NativeMethods.WM_ACTIVATE,
                        new IntPtr(NativeMethods.WA_ACTIVE), IntPtr.Zero);
                    NativeMethods.PostMessage(hwnd, NativeMethods.WM_SETFOCUS,
                        IntPtr.Zero, IntPtr.Zero);
                }
                Ping(_sourceHwnd);
                if (_browserTopLevelHwnd != _sourceHwnd)
                    Ping(_browserTopLevelHwnd);
            };

            RefreshWindowList();
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            Loaded -= OnLoaded;
            try
            {
                // Store WebView2 user data alongside other VS extension data.
                string dataDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "BingeCode", "WebView2");
                Directory.CreateDirectory(dataDir);
                var env = await CoreWebView2Environment.CreateAsync(null, dataDir);
                await Browser.EnsureCoreWebView2Async(env);
                // Suppress the default right-click context menu and dev-tools shortcut
                // so it feels like a seamless media player, not a browser.
                Browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled  = false;
                Browser.CoreWebView2.Settings.AreDevToolsEnabled             = false;
            }
            catch { /* WebView2 runtime unavailable, URL navigation will silently fail */ }
        }

        // ── Window picker ────────────────────────────────────────────────────

        private void RefreshWindowList()
        {
            var windows = new List<WindowInfo>();

            NativeMethods.EnumWindows((hWnd, _) =>
            {
                if (!NativeMethods.IsWindowVisible(hWnd)) return true;
                int len = NativeMethods.GetWindowTextLength(hWnd);
                if (len == 0) return true;

                var sb = new StringBuilder(len + 1);
                NativeMethods.GetWindowText(hWnd, sb, sb.Capacity);
                string title = sb.ToString().Trim();
                if (string.IsNullOrEmpty(title)) return true;

                string procName = GetProcessName(hWnd);
                if (procName == null) return true;   // skip un-queryable system windows

                windows.Add(new WindowInfo
                {
                    Handle      = hWnd,
                    ProcessName = procName,
                    Title       = title
                });

                return true;
            }, IntPtr.Zero);

            WindowPicker.ItemsSource = windows;
        }

        // Returns the process name for the window's owning process, or null if it
        // should be excluded (own process, system shell, un-queryable process).
        private static string GetProcessName(IntPtr hWnd)
        {
            try
            {
                uint pid;
                NativeMethods.GetWindowThreadProcessId(hWnd, out pid);
                if ((int)pid == OwnPid) return null;
                string name = Process.GetProcessById((int)pid).ProcessName;
                return SystemProcessBlacklist.Contains(name) ? null : name;
            }
            catch { return null; }
        }

        private void RefreshWindows_Click(object sender, RoutedEventArgs e)
        {
            RefreshWindowList();
        }

        private void WindowPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (WindowPicker.SelectedItem is WindowInfo info && info.Handle != IntPtr.Zero)
            {
                var result = MessageBox.Show(
                    $"Mirror \"{info.Title}\" inside BingeCode?\n\nThe original window stays where it is.",
                    "BingeCode: Confirm",
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.OK)
                    StartThumbnailMirror(info.Handle);
                else
                    WindowPicker.SelectedItem = null;
            }
        }

        // ── DWM Thumbnail mirror ─────────────────────────────────────────────

        // Returns the root top-level HWND that hosts this control.
        // DwmRegisterThumbnail only accepts top-level windows as destination.
        private IntPtr GetRootHwnd()
        {
            var hwndSource = PresentationSource.FromVisual(this) as HwndSource;
            if (hwndSource == null) return IntPtr.Zero;
            return NativeMethods.GetAncestor(hwndSource.Handle, NativeMethods.GA_ROOT);
        }

        private void StartThumbnailMirror(IntPtr sourceHwnd)
        {
            ClearContent();

            IntPtr rootHwnd = GetRootHwnd();
            if (rootHwnd == IntPtr.Zero) return;

            // DwmRegisterThumbnail requires a top-level window as source.
            int hr = NativeMethods.DwmRegisterThumbnail(
                rootHwnd, sourceHwnd, out _thumbnailHandle);

            if (hr != 0)
            {
                MessageBox.Show(
                    "Could not mirror that window. Try a different one.",
                    "BingeCode", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _sourceHwnd          = sourceHwnd;
            _browserTopLevelHwnd = sourceHwnd;
            _videoSourceRect     = System.Windows.Rect.Empty;

            Browser.Visibility       = Visibility.Collapsed;
            Placeholder.Visibility   = Visibility.Collapsed;
            ThumbnailArea.Visibility = Visibility.Visible;

            _keepAliveTimer.Start();

            // Defer until the layout has rendered so ActualWidth/Height are non-zero.
            Dispatcher.BeginInvoke(new Action(UpdateThumbnailPosition), DispatcherPriority.Render);

            Task.Run(() =>
            {
                // Phase 0 (Win32, instant): use the render-widget HWND rect as rcSource.
                // DWM requires a top-level source, so we keep sourceHwnd as the DWM source
                // and crop via rcSource to just the content area, stripping the browser
                // chrome (address bar, tab strip, bookmarks bar) immediately.
                var contentRect = FindContentHwndRect(sourceHwnd);
                if (!contentRect.IsEmpty)
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        _videoSourceRect = contentRect;
                        UpdateThumbnailPosition();
                    }));

                // Prime Chrome's accessibility tree so it populates even without focus
                ForceAccessibility(sourceHwnd);
                System.Threading.Thread.Sleep(600);

                // Phase 1: UIA document rect
                var docRect = FindDocumentRect(sourceHwnd);
                if (!docRect.IsEmpty)
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        _videoSourceRect = docRect;
                        UpdateThumbnailPosition();
                    }));

                // Phase 2: motion detection, finds the animated region (video)
                // regardless of browser, streaming service, or UIA state.
                // rootHwnd is passed so the method can cloak VS during capture,
                // preventing Chrome's OcclusionTracker from freezing the video.
                var videoRect = DetectVideoRectByMotion(sourceHwnd, rootHwnd);
                if (!videoRect.IsEmpty)
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        _videoSourceRect = videoRect;
                        UpdateThumbnailPosition();
                    }));
            });
        }

        private void UpdateThumbnailPosition()
        {
            if (_thumbnailHandle == IntPtr.Zero) return;

            IntPtr rootHwnd = GetRootHwnd();
            if (rootHwnd == IntPtr.Zero) return;

            // Get the screen rect of our thumbnail area
            Point tl = ThumbnailArea.PointToScreen(new Point(0, 0));
            Point br = ThumbnailArea.PointToScreen(
                new Point(ThumbnailArea.ActualWidth, ThumbnailArea.ActualHeight));

            // Convert screen coords to root HWND client coords
            var origin = new NativeMethods.POINT { x = 0, y = 0 };
            NativeMethods.ClientToScreen(rootHwnd, ref origin);

            var dest = new NativeMethods.RECT
            {
                left   = (int)tl.X - origin.x,
                top    = (int)tl.Y - origin.y,
                right  = (int)br.X - origin.x,
                bottom = (int)br.Y - origin.y
            };

            // When UIA found a <video> element, crop the source to that element's
            // exact bounds so only the video is shown. Otherwise let DWM stretch
            // the full client area to fill the destination (no black bars).
            bool useSourceCrop = !_videoSourceRect.IsEmpty && _videoSourceRect.Width > 0;
            NativeMethods.RECT src = default(NativeMethods.RECT);
            if (useSourceCrop)
            {
                var clientOrigin = new NativeMethods.POINT { x = 0, y = 0 };
                NativeMethods.ClientToScreen(_sourceHwnd, ref clientOrigin);
                src = new NativeMethods.RECT
                {
                    left   = (int)_videoSourceRect.Left   - clientOrigin.x,
                    top    = (int)_videoSourceRect.Top    - clientOrigin.y,
                    right  = (int)_videoSourceRect.Right  - clientOrigin.x,
                    bottom = (int)_videoSourceRect.Bottom - clientOrigin.y
                };

                // DWM thumbnails preserve aspect ratio and align top-left, so when
                // source AR ≠ dest AR the unfilled region shows as a black border.
                // Center-crop rcSource to exactly match the destination AR ("cover"
                // mode) so DWM fills the destination rect with no transparent gaps.
                int srcW = src.right  - src.left;
                int srcH = src.bottom - src.top;
                int dstW = dest.right  - dest.left;
                int dstH = dest.bottom - dest.top;
                if (srcW > 0 && srcH > 0 && dstW > 0 && dstH > 0)
                {
                    long crossA = (long)srcW * dstH;
                    long crossB = (long)srcH * dstW;
                    if (crossA > crossB)
                    {
                        // Source wider than dest → crop source width to match dest AR.
                        int targetW = (int)Math.Round((double)srcH * dstW / dstH);
                        int trim    = (srcW - targetW) / 2;
                        src.left  += trim;
                        src.right  = src.left + targetW;
                    }
                    else if (crossA < crossB)
                    {
                        // Source taller than dest → crop source height to match dest AR.
                        int targetH = (int)Math.Round((double)srcW * dstH / dstW);
                        int trim    = (srcH - targetH) / 2;
                        src.top    += trim;
                        src.bottom  = src.top + targetH;
                    }
                }
            }

            int sourceFlags = NativeMethods.DWM_TNP_VISIBLE
                            | NativeMethods.DWM_TNP_RECTDESTINATION
                            | NativeMethods.DWM_TNP_OPACITY
                            | NativeMethods.DWM_TNP_SOURCECLIENTAREAONLY;
            if (useSourceCrop) sourceFlags |= NativeMethods.DWM_TNP_RECTSOURCE;

            var props = new NativeMethods.DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags               = sourceFlags,
                rcDestination         = dest,
                rcSource              = src,
                opacity               = 255,
                fVisible              = true,
                fSourceClientAreaOnly = true
            };

            NativeMethods.DwmUpdateThumbnailProperties(_thumbnailHandle, ref props);
        }

        private void ClearThumbnail()
        {
            _keepAliveTimer.Stop();
            if (_thumbnailHandle != IntPtr.Zero)
            {
                NativeMethods.DwmUnregisterThumbnail(_thumbnailHandle);
                _thumbnailHandle = IntPtr.Zero;
            }
            _sourceHwnd          = IntPtr.Zero;
            _browserTopLevelHwnd = IntPtr.Zero;
            _videoSourceRect     = System.Windows.Rect.Empty;
        }

        // Phase 1: returns the bounding rect of the browser's Document element
        // (the page content area, which excludes the tab bar and address bar).
        // Works for both Chromium and Firefox/Zen. Fast, no retry needed.
        private static System.Windows.Rect FindDocumentRect(IntPtr sourceHwnd)
        {
            var cond = new System.Windows.Automation.PropertyCondition(
                System.Windows.Automation.AutomationElement.ControlTypeProperty,
                System.Windows.Automation.ControlType.Document);
            try
            {
                // Try top-level first
                var best = SearchUiaForLargest(sourceHwnd, cond);
                if (!best.IsEmpty) return best;

                // Fallback: Chrome render widget child
                IntPtr renderHwnd = FindLargestChildByClass(
                    sourceHwnd, "Chrome_RenderWidgetHostHWND");
                if (renderHwnd != IntPtr.Zero)
                    return SearchUiaForLargest(renderHwnd, cond);
            }
            catch { }
            return System.Windows.Rect.Empty;
        }

        private static System.Windows.Rect SearchUiaForLargest(
            IntPtr hwnd, System.Windows.Automation.Condition cond)
        {
            try
            {
                var root = System.Windows.Automation.AutomationElement.FromHandle(hwnd);
                if (root == null) return System.Windows.Rect.Empty;
                var all  = root.FindAll(
                    System.Windows.Automation.TreeScope.Descendants, cond);
                System.Windows.Rect best = System.Windows.Rect.Empty;
                foreach (System.Windows.Automation.AutomationElement el in all)
                {
                    try
                    {
                        var r = el.Current.BoundingRectangle;
                        if (r.Width * r.Height > best.Width * best.Height) best = r;
                    }
                    catch { }
                }
                return best;
            }
            catch { return System.Windows.Rect.Empty; }
        }

        // Phase 2: searches the accessibility tree for a <video> element.
        // Browsers initialise their accessibility trees lazily, so we retry
        // up to ~7 s to give Chrome/Firefox time to wire up the tree.
        private static System.Windows.Rect FindVideoElementRect(IntPtr sourceHwnd)
        {
            // IgnoreCase handles locale differences ("video" vs "Video" etc.)
            var videoCond = new System.Windows.Automation.PropertyCondition(
                System.Windows.Automation.AutomationElement.LocalizedControlTypeProperty,
                "video",
                System.Windows.Automation.PropertyConditionFlags.IgnoreCase);

            int[] delays = { 0, 500, 1000, 1500, 2000, 2000 };
            foreach (int delay in delays)
            {
                if (delay > 0) System.Threading.Thread.Sleep(delay);
                try
                {
                    // Try from the top-level source window first
                    var best = SearchUiaForVideo(sourceHwnd, videoCond);
                    if (!best.IsEmpty) return best;

                    // Chrome's render widget may surface elements the top-level
                    // UIA tree misses, try the largest Chrome_RenderWidgetHostHWND child
                    IntPtr renderHwnd = FindLargestChildByClass(
                        sourceHwnd, "Chrome_RenderWidgetHostHWND");
                    if (renderHwnd != IntPtr.Zero)
                    {
                        best = SearchUiaForVideo(renderHwnd, videoCond);
                        if (!best.IsEmpty) return best;
                    }
                }
                catch { }
            }
            return System.Windows.Rect.Empty;
        }

        private static System.Windows.Rect SearchUiaForVideo(
            IntPtr hwnd, System.Windows.Automation.Condition cond)
        {
            try
            {
                var root = System.Windows.Automation.AutomationElement.FromHandle(hwnd);
                if (root == null) return System.Windows.Rect.Empty;
                var all  = root.FindAll(
                    System.Windows.Automation.TreeScope.Descendants, cond);
                System.Windows.Rect best = System.Windows.Rect.Empty;
                foreach (System.Windows.Automation.AutomationElement el in all)
                {
                    try
                    {
                        var r = el.Current.BoundingRectangle;
                        if (r.Width * r.Height > best.Width * best.Height) best = r;
                    }
                    catch { }
                }
                return best;
            }
            catch { return System.Windows.Rect.Empty; }
        }

        // ── Motion-based video detection ─────────────────────────────────────

        // Phase 2: captures two frames 350 ms apart via PrintWindow, diffs them
        // on a coarse grid, and returns the bounding rect of all cells that
        // changed (i.e. the video). Works for any browser / any streaming service.
        //
        // vsHwndToCloak: pass the VS root HWND when VS is NOT already cloaked.
        //   This temporarily cloaks VS so Chrome's OcclusionTracker stops seeing
        //   it as covering Chrome, otherwise Chrome freezes rendering and both
        //   frames are identical (no diff, no result).
        //   Pass IntPtr.Zero when VS is already cloaked (e.g. fullscreen mode).
        private static System.Windows.Rect DetectVideoRectByMotion(
            IntPtr sourceHwnd, IntPtr vsHwndToCloak)
        {
            const int SAMPLE_W  = 320;
            const int SAMPLE_H  = 180;
            const int GRID_COLS = 20;
            const int GRID_ROWS = 12;
            const int THRESHOLD = 15;   // avg channel diff to count as motion
            const int DELAY_MS  = 350;

            bool didCloak = false;
            try
            {
                // Prefer render widget, excludes browser chrome from capture
                IntPtr captureHwnd = FindLargestChildByClass(
                    sourceHwnd, "Chrome_RenderWidgetHostHWND");
                if (captureHwnd == IntPtr.Zero)
                    captureHwnd = FindLargestChildByClass(
                        sourceHwnd, "MozillaCompositorWindowClass");
                if (captureHwnd == IntPtr.Zero)
                    captureHwnd = sourceHwnd;

                // Cloak VS so Chrome's OcclusionTracker no longer sees it as an
                // opaque window covering Chrome. Chrome then resumes rendering so
                // the two captured frames contain actual video motion.
                if (vsHwndToCloak != IntPtr.Zero)
                {
                    NativeMethods.DwmSetWindowAttribute(
                        vsHwndToCloak, NativeMethods.DWMWA_CLOAK, 1, 4);
                    didCloak = true;
                    // Poke Chrome to re-check its active/visible state immediately
                    NativeMethods.PostMessage(sourceHwnd, NativeMethods.WM_ACTIVATE,
                        new IntPtr(NativeMethods.WA_ACTIVE), IntPtr.Zero);
                    NativeMethods.PostMessage(captureHwnd, NativeMethods.WM_ACTIVATE,
                        new IntPtr(NativeMethods.WA_ACTIVE), IntPtr.Zero);
                    // Wait for OcclusionTracker to re-evaluate (~400 ms period) and
                    // for Chrome to render at least one new frame.
                    System.Threading.Thread.Sleep(450);
                }

                byte[] frame1 = CaptureWindowScaled(captureHwnd, SAMPLE_W, SAMPLE_H);
                if (frame1 == null) return System.Windows.Rect.Empty;

                System.Threading.Thread.Sleep(DELAY_MS);

                byte[] frame2 = CaptureWindowScaled(captureHwnd, SAMPLE_W, SAMPLE_H);
                if (frame2 == null) return System.Windows.Rect.Empty;

                int cellW  = SAMPLE_W / GRID_COLS;
                int cellH  = SAMPLE_H / GRID_ROWS;
                int minCol = GRID_COLS, maxCol = -1;
                int minRow = GRID_ROWS, maxRow = -1;

                for (int row = 0; row < GRID_ROWS; row++)
                {
                    for (int col = 0; col < GRID_COLS; col++)
                    {
                        long totalDiff = 0;
                        int px = col * cellW;
                        int py = row * cellH;
                        for (int y = py; y < py + cellH; y++)
                        {
                            for (int x = px; x < px + cellW; x++)
                            {
                                int idx = (y * SAMPLE_W + x) * 4;
                                if (idx + 2 < frame1.Length)
                                {
                                    totalDiff += Math.Abs(frame1[idx]     - frame2[idx]);
                                    totalDiff += Math.Abs(frame1[idx + 1] - frame2[idx + 1]);
                                    totalDiff += Math.Abs(frame1[idx + 2] - frame2[idx + 2]);
                                }
                            }
                        }
                        double avgDiff = (double)totalDiff / (cellW * cellH * 3);
                        if (avgDiff > THRESHOLD)
                        {
                            if (col < minCol) minCol = col;
                            if (col > maxCol) maxCol = col;
                            if (row < minRow) minRow = row;
                            if (row > maxRow) maxRow = row;
                        }
                    }
                }

                if (maxCol < 0) return System.Windows.Rect.Empty;

                NativeMethods.GetClientRect(captureHwnd, out var cr);
                double scaleX = (double)cr.right  / SAMPLE_W;
                double scaleY = (double)cr.bottom / SAMPLE_H;

                int clientLeft   = Math.Max(0,        (int)(minCol           * cellW * scaleX));
                int clientTop    = Math.Max(0,        (int)(minRow           * cellH * scaleY));
                int clientRight  = Math.Min(cr.right, (int)((maxCol + 1) * cellW * scaleX));
                int clientBottom = Math.Min(cr.bottom,(int)((maxRow + 1) * cellH * scaleY));

                if (clientRight <= clientLeft || clientBottom <= clientTop)
                    return System.Windows.Rect.Empty;

                var tl = new NativeMethods.POINT { x = clientLeft,  y = clientTop    };
                var br = new NativeMethods.POINT { x = clientRight, y = clientBottom };
                NativeMethods.ClientToScreen(captureHwnd, ref tl);
                NativeMethods.ClientToScreen(captureHwnd, ref br);

                return new System.Windows.Rect(tl.x, tl.y, br.x - tl.x, br.y - tl.y);
            }
            catch { return System.Windows.Rect.Empty; }
            finally
            {
                // Always uncloak VS so it reappears immediately after the capture
                if (didCloak)
                    NativeMethods.DwmSetWindowAttribute(
                        vsHwndToCloak, NativeMethods.DWMWA_CLOAK, 0, 4);
            }
        }

        // Captures the client area of hwnd via PrintWindow (GPU-aware), then
        // nearest-neighbour downsamples to sampleW × sampleH.
        // Returns a byte array of BGRA pixels (4 bytes per pixel).
        private static byte[] CaptureWindowScaled(IntPtr hwnd, int sampleW, int sampleH)
        {
            NativeMethods.GetClientRect(hwnd, out var cr);
            int srcW = cr.right;
            int srcH = cr.bottom;
            if (srcW <= 0 || srcH <= 0) return null;

            IntPtr screenDC = NativeMethods.GetDC(hwnd);
            if (screenDC == IntPtr.Zero) return null;
            try
            {
                IntPtr captureDC  = NativeMethods.CreateCompatibleDC(screenDC);
                IntPtr captureBmp = NativeMethods.CreateCompatibleBitmap(screenDC, srcW, srcH);
                IntPtr oldBmp     = NativeMethods.SelectObject(captureDC, captureBmp);

                // PW_RENDERFULLCONTENT captures GPU / D3D content (Chrome, Edge, etc.)
                NativeMethods.PrintWindow(hwnd, captureDC, NativeMethods.PW_RENDERFULLCONTENT);

                var bmi = new NativeMethods.BITMAPINFO { bmiColors = new uint[4] };
                bmi.bmiHeader.biSize        = (uint)System.Runtime.InteropServices.Marshal
                                                   .SizeOf(typeof(NativeMethods.BITMAPINFOHEADER));
                bmi.bmiHeader.biWidth       = (uint)srcW;
                bmi.bmiHeader.biHeight      = srcH;  // positive = bottom-up scanlines
                bmi.bmiHeader.biPlanes      = 1;
                bmi.bmiHeader.biBitCount    = 32;
                bmi.bmiHeader.biCompression = 0;     // BI_RGB

                byte[] fullPixels = new byte[srcW * srcH * 4];
                NativeMethods.GetDIBits(captureDC, captureBmp, 0, (uint)srcH,
                                        fullPixels, ref bmi, 0);

                NativeMethods.SelectObject(captureDC, oldBmp);
                NativeMethods.DeleteObject(captureBmp);
                NativeMethods.DeleteDC(captureDC);

                // Downsample, orientation is consistent across both frames so
                // bottom-up vs top-down doesn't affect the diff result.
                byte[] result = new byte[sampleW * sampleH * 4];
                for (int dy = 0; dy < sampleH; dy++)
                {
                    int sy = dy * srcH / sampleH;
                    for (int dx = 0; dx < sampleW; dx++)
                    {
                        int sx     = dx * srcW / sampleW;
                        int srcIdx = (sy * srcW + sx) * 4;
                        int dstIdx = (dy * sampleW + dx) * 4;
                        result[dstIdx]     = fullPixels[srcIdx];
                        result[dstIdx + 1] = fullPixels[srcIdx + 1];
                        result[dstIdx + 2] = fullPixels[srcIdx + 2];
                    }
                }
                return result;
            }
            finally
            {
                NativeMethods.ReleaseDC(hwnd, screenDC);
            }
        }

        private static IntPtr FindLargestChildByClass(IntPtr parent, string className)
        {
            IntPtr best    = IntPtr.Zero;
            int    bestArea = 0;
            NativeMethods.EnumChildWindows(parent, (child, _) =>
            {
                var sb = new StringBuilder(256);
                NativeMethods.GetClassName(child, sb, 256);
                if (string.Equals(sb.ToString(), className,
                        StringComparison.OrdinalIgnoreCase))
                {
                    NativeMethods.GetWindowRect(child, out var wr);
                    int area = (wr.right - wr.left) * (wr.bottom - wr.top);
                    if (area > bestArea) { bestArea = area; best = child; }
                }
                return true;
            }, IntPtr.Zero);
            return best;
        }

        // Finds the main content-area rect of the source window.
        // Strategy:
        //   1. Known renderer class names (Chrome, Firefox/Gecko).
        //   2. Largest visible child HWND by area, works for NW.js (Stremio),
        //      CEF, Electron variants, and any future framework.
        //   3. Full client area fallback.
        // Phase 0: get the render-widget screen rect via Win32, no UIA, no delays.
        // Strips Chrome/Edge/Firefox browser chrome instantly.
        private static System.Windows.Rect FindContentHwndRect(IntPtr sourceHwnd)
        {
            // Preferred renderer class names for each browser family
            string[] preferred = {
                "Chrome_RenderWidgetHostHWND",   // Chrome, Edge, Electron, NW.js, CEF
                "MozillaCompositorWindowClass",  // Firefox, Zen, Librewolf
                "MozillaWindowClass",
            };
            IntPtr best    = IntPtr.Zero;
            int    bestArea = 0;

            NativeMethods.EnumChildWindows(sourceHwnd, (child, _) =>
            {
                if (!NativeMethods.IsWindowVisible(child)) return true;
                var sb = new StringBuilder(256);
                NativeMethods.GetClassName(child, sb, 256);
                string cls = sb.ToString();
                foreach (string p in preferred)
                {
                    if (string.Equals(cls, p, StringComparison.OrdinalIgnoreCase))
                    {
                        NativeMethods.GetWindowRect(child, out var wr);
                        int area = (wr.right - wr.left) * (wr.bottom - wr.top);
                        if (area > bestArea) { bestArea = area; best = child; }
                        break;
                    }
                }
                return true;
            }, IntPtr.Zero);

            if (best == IntPtr.Zero) return System.Windows.Rect.Empty;
            NativeMethods.GetWindowRect(best, out var r);
            return new System.Windows.Rect(r.left, r.top, r.right - r.left, r.bottom - r.top);
        }

        // Calling AccessibleObjectFromWindow on the render HWND signals Chrome
        // to fully initialise its UIA/MSAA accessibility tree even when it does
        // not have focus. Must be called before any UIA FindAll queries.
        private static void ForceAccessibility(IntPtr sourceHwnd)
        {
            try
            {
                IntPtr renderHwnd = FindLargestChildByClass(
                    sourceHwnd, "Chrome_RenderWidgetHostHWND");
                IntPtr target = renderHwnd != IntPtr.Zero ? renderHwnd : sourceHwnd;
                var iid = new Guid("618736e0-3c3d-11cf-810c-00aa00389b71"); // IID_IAccessible
                NativeMethods.AccessibleObjectFromWindow(
                    target, NativeMethods.OBJID_CLIENT, ref iid, out _);
            }
            catch { }
        }

        private static NativeMethods.RECT FindContentAreaRect(IntPtr sourceHwnd)
        {
            NativeMethods.RECT sourceClient;
            NativeMethods.GetClientRect(sourceHwnd, out sourceClient);
            int sourceArea = sourceClient.right * sourceClient.bottom;

            var preferredClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Chrome_RenderWidgetHostHWND",
                "MozillaCompositorWindowClass",
                "MozillaWindowClass",
                "GeckoPluginWindow",
            };

            IntPtr bestPreferred = IntPtr.Zero;
            IntPtr bestFallback  = IntPtr.Zero;
            int    bestPrefArea  = 0;
            int    bestFallArea  = 0;

            NativeMethods.EnumChildWindows(sourceHwnd, (child, _) =>
            {
                if (!NativeMethods.IsWindowVisible(child)) return true;

                NativeMethods.RECT wr;
                if (!NativeMethods.GetWindowRect(child, out wr)) return true;
                int area = (wr.right - wr.left) * (wr.bottom - wr.top);
                if (area <= 0) return true;

                var sb = new StringBuilder(256);
                NativeMethods.GetClassName(child, sb, 256);
                string cls = sb.ToString();

                if (preferredClasses.Contains(cls))
                {
                    if (area > bestPrefArea) { bestPrefArea = area; bestPreferred = child; }
                }
                else
                {
                    // Only consider children that are nearly as large as the source
                    // (≥ 70 % of the source client area) to avoid picking toolbars.
                    if (area >= sourceArea * 0.70 && area > bestFallArea)
                    { bestFallArea = area; bestFallback = child; }
                }
                return true;
            }, IntPtr.Zero);

            IntPtr contentHwnd = bestPreferred != IntPtr.Zero ? bestPreferred : bestFallback;
            if (contentHwnd == IntPtr.Zero) return sourceClient;

            NativeMethods.RECT screenRect;
            NativeMethods.GetWindowRect(contentHwnd, out screenRect);

            var tl = new NativeMethods.POINT { x = screenRect.left,  y = screenRect.top    };
            var br = new NativeMethods.POINT { x = screenRect.right, y = screenRect.bottom  };
            NativeMethods.ScreenToClient(sourceHwnd, ref tl);
            NativeMethods.ScreenToClient(sourceHwnd, ref br);

            return new NativeMethods.RECT { left = tl.x, top = tl.y, right = br.x, bottom = br.y };
        }

        // ── URL navigation ───────────────────────────────────────────────────

        private void LoadUrl_Click(object sender, RoutedEventArgs e) =>
            NavigateTo(UrlInput.Text);

        private void UrlInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) NavigateTo(UrlInput.Text);
        }

        private void NavigateTo(string rawUrl)
        {
            if (string.IsNullOrWhiteSpace(rawUrl)) return;

            string trimmed = rawUrl.Trim();
            bool hasScheme = AllowedSchemes.Any(s =>
                trimmed.StartsWith(s, StringComparison.OrdinalIgnoreCase));

            if (!hasScheme)
            {
                if (!trimmed.Contains("://"))
                    trimmed = "https://" + trimmed;
                else
                {
                    MessageBox.Show("Only http:// and https:// URLs are allowed.",
                        "BingeCode: Blocked URL", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out Uri uri))
            {
                MessageBox.Show("The URL is not valid.",
                    "BingeCode: Invalid URL", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Navigate inside the embedded WebView2 (Edge Chromium).
            ClearContent();
            ThumbnailArea.Visibility = Visibility.Collapsed;
            Placeholder.Visibility   = Visibility.Collapsed;
            Browser.Visibility       = Visibility.Visible;
            Browser.Source           = uri;
        }

        // ── Local file ───────────────────────────────────────────────────────

        private void OpenFile_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title  = "Open video file: BingeCode",
                Filter = "Video files|*.mp4;*.avi;*.mkv;*.mov;*.wmv;*.webm;*.flv|All files|*.*"
            };

            if (dialog.ShowDialog() != true) return;

            string ext = Path.GetExtension(dialog.FileName);
            if (!AllowedVideoExtensions.Contains(ext))
            {
                MessageBox.Show($"File type \"{ext}\" is not allowed.",
                    "BingeCode: Blocked File", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!Path.IsPathRooted(dialog.FileName) || !File.Exists(dialog.FileName))
            {
                MessageBox.Show("The selected file could not be verified.",
                    "BingeCode: Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            ClearContent();
            ThumbnailArea.Visibility = Visibility.Collapsed;
            Placeholder.Visibility   = Visibility.Collapsed;
            Browser.Visibility       = Visibility.Visible;
            Browser.Source           = new Uri(dialog.FileName);
        }

        // ── Fullscreen toggle ────────────────────────────────────────────────

        private void ToggleFullscreen_Click(object sender, RoutedEventArgs e) =>
            SetFullscreen(!_isBorderless);

        private void ContentArea_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            ContentGrid.Focus(); // grab focus so F11 works immediately
            if (e.ClickCount == 2)
                SetFullscreen(!_isBorderless);
        }

        private void Control_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F11)
            {
                SetFullscreen(!_isBorderless);
                e.Handled = true;
            }
        }

        private void SetFullscreen(bool fullscreen)
        {
            _isBorderless = fullscreen;

            Toolbar.Visibility            = fullscreen ? Visibility.Collapsed : Visibility.Visible;
            ContentBorder.BorderThickness = fullscreen ? new Thickness(0)     : new Thickness(1);
            ContentBorder.BorderBrush     = fullscreen
                ? System.Windows.Media.Brushes.Transparent
                : new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter
                        .ConvertFromString("#E50914"));

            if (fullscreen)
            {
                if (_sourceHwnd != IntPtr.Zero)
                {
                    // DWM mirror mode: open the topmost overlay window.
                    ShowFullscreenOverlay();

                    IntPtr hwnd = _sourceHwnd;
                    Task.Run(() =>
                    {
                        // VS is already cloaked by ShowFullscreenOverlay, so pass Zero
                        var rect = DetectVideoRectByMotion(hwnd, IntPtr.Zero);
                        if (!rect.IsEmpty)
                            Dispatcher.BeginInvoke(new Action(() =>
                            {
                                _videoSourceRect = rect;
                                UpdateOverlayThumbnailProps();
                            }));
                    });
                }
                else
                {
                    // Browser / placeholder mode: no overlay window.
                    // Show the in-pane MiniBar exit strip. Don't auto-hide it,
                    // WebView2 swallows mouse events so we can't detect movement
                    // inside the browser to re-show it once hidden.
                    MiniBar.Visibility = Visibility.Visible;
                }
            }
            else
            {
                CloseFullscreenOverlay();
                MiniBar.Visibility = Visibility.Collapsed;
                _miniBarTimer.Stop();
                // Restore the in-pane thumbnail
                Dispatcher.BeginInvoke(new Action(UpdateThumbnailPosition), DispatcherPriority.Render);
            }
        }

        // ── Fullscreen overlay window ────────────────────────────────────────

        private void ShowFullscreenOverlay()
        {
            CloseFullscreenOverlay();
            if (_sourceHwnd == IntPtr.Zero) return;

            IntPtr rootHwnd = GetRootHwnd();
            if (rootHwnd == IntPtr.Zero) return;

            NativeMethods.GetWindowRect(rootHwnd, out var rootRect);
            _overlayRootHwnd = rootHwnd;
            _savedVsRect     = rootRect;   // restored when exiting fullscreen

            // Exit bar, shown for 3 s on entry, auto-hides, reappears on mouse move.
            // Transparent background so only the button text floats over the video.
            _overlayExitBar = new Border
            {
                Background          = System.Windows.Media.Brushes.Transparent,
                Height              = 32,
                VerticalAlignment   = VerticalAlignment.Top,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            var exitBtn = new Button
            {
                Content             = "▲ Exit fullscreen",
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment   = VerticalAlignment.Center,
                Margin              = new Thickness(0, 0, 8, 0),
                Padding             = new Thickness(8, 0, 8, 0),
                FontSize            = 10,
                Height              = 18,
                Foreground          = System.Windows.Media.Brushes.White,
                Background          = System.Windows.Media.Brushes.Transparent,
                BorderThickness     = new Thickness(0)
            };
            exitBtn.Click += (s, e) => SetFullscreen(false);
            _overlayExitBar.Child = exitBtn;

            var grid = new Grid { Background = System.Windows.Media.Brushes.Black };
            grid.Children.Add(_overlayExitBar);

            _fullscreenOverlay = new Window
            {
                WindowStyle        = WindowStyle.None,
                ResizeMode         = ResizeMode.CanResize,
                AllowsTransparency = false,
                Topmost            = true,
                ShowInTaskbar      = false,
                Background         = System.Windows.Media.Brushes.Black,
                Left               = rootRect.left,
                Top                = rootRect.top,
                Width              = rootRect.right  - rootRect.left,
                Height             = rootRect.bottom - rootRect.top,
                Content            = grid
            };

            // WindowChrome removes the native caption area (white bar) that Windows
            // adds to CanResize windows even when WindowStyle=None, while keeping
            // the invisible resize grip around the edges.
            System.Windows.Shell.WindowChrome.SetWindowChrome(_fullscreenOverlay,
                new System.Windows.Shell.WindowChrome
                {
                    CaptionHeight          = 0,
                    ResizeBorderThickness  = new Thickness(6),
                    UseAeroCaptionButtons  = false,
                    GlassFrameThickness    = new Thickness(0)
                });

            // No Owner, owner relationship causes VS to surface above the overlay
            // during the native resize modal loop. Topmost=true is sufficient.

            // Drag anywhere on the video with a small movement threshold so
            // double-click still exits fullscreen.
            System.Windows.Point _dragOrigin = default;
            bool _dragStarted = false;

            grid.PreviewMouseLeftButtonDown += (s, e) =>
            {
                _dragOrigin  = e.GetPosition(_fullscreenOverlay);
                _dragStarted = false;
            };

            grid.PreviewMouseMove += (s, e) =>
            {
                if (e.LeftButton != MouseButtonState.Pressed || _dragStarted) return;
                var p = e.GetPosition(_fullscreenOverlay);
                if (Math.Abs(p.X - _dragOrigin.X) > 6 || Math.Abs(p.Y - _dragOrigin.Y) > 6)
                {
                    _dragStarted = true;
                    _fullscreenOverlay?.DragMove();  // blocks until mouse release
                    _dragStarted = false;
                }
            };

            // Show exit bar on mouse move; restart auto-hide timer
            _fullscreenOverlay.MouseMove += (s, e) =>
            {
                _overlayExitBar.Visibility = Visibility.Visible;
                _miniBarTimer.Stop();
                _miniBarTimer.Start();
            };

            // Double-click exits fullscreen (fires only when not dragging)
            grid.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ClickCount == 2) SetFullscreen(false);
            };

            _fullscreenOverlay.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.F11) { SetFullscreen(false); e.Handled = true; }
            };

            _fullscreenOverlay.Show();
            _fullscreenOverlay.Activate();

            // Cloak VS's main window so DWM stops compositing it. Chrome's
            // OcclusionTracker detects coverage by opaque windows, with VS cloaked,
            // Chrome is no longer "occluded" and keeps rendering the video.
            NativeMethods.DwmSetWindowAttribute(
                rootHwnd, NativeMethods.DWMWA_CLOAK, 1, 4);

            // Show exit bar for 3 s immediately on entry
            _overlayExitBar.Visibility = Visibility.Visible;
            _miniBarTimer.Stop();
            _miniBarTimer.Start();

            // Register DWM thumbnail on the overlay window
            var overlaySource = PresentationSource.FromVisual(_fullscreenOverlay) as HwndSource;
            if (overlaySource == null) return;

            NativeMethods.GetClientRect(overlaySource.Handle, out var clientRect);

            int hr = NativeMethods.DwmRegisterThumbnail(
                overlaySource.Handle, _sourceHwnd, out _overlayThumbHandle);
            if (hr == 0)
                ApplyOverlayThumbnailProps(clientRect);

            // Hook WM_SIZE so the thumbnail fills the window as the user resizes it.
            // SizeChanged fires too early (before the HWND client rect updates).
            overlaySource.AddHook(OverlayWndProc);
        }

        private IntPtr OverlayWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_MOVE = 0x0003;
            const int WM_SIZE = 0x0005;

            if (msg == WM_SIZE || msg == WM_MOVE)
            {
                // Keep the DWM thumbnail filling the (possibly new) client area
                if (msg == WM_SIZE)
                {
                    NativeMethods.GetClientRect(hwnd, out var cr);
                    ApplyOverlayThumbnailProps(cr);
                }

                // Resize/move VS to exactly match the overlay so nothing shows behind
                if (_overlayRootHwnd != IntPtr.Zero)
                {
                    NativeMethods.GetWindowRect(hwnd, out var wr);
                    NativeMethods.SetWindowPos(
                        _overlayRootHwnd, IntPtr.Zero,
                        wr.left, wr.top,
                        wr.right - wr.left, wr.bottom - wr.top,
                        NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
                }
            }
            return IntPtr.Zero;
        }

        // Recompute thumbnail source crop and push updated DWM properties.
        private void UpdateOverlayThumbnailProps()
        {
            if (_overlayThumbHandle == IntPtr.Zero || _fullscreenOverlay == null) return;
            var overlaySource = PresentationSource.FromVisual(_fullscreenOverlay) as HwndSource;
            if (overlaySource == null) return;
            NativeMethods.GetClientRect(overlaySource.Handle, out var clientRect);
            ApplyOverlayThumbnailProps(clientRect);
        }

        private void ApplyOverlayThumbnailProps(NativeMethods.RECT clientRect)
        {
            bool useSourceCrop = !_videoSourceRect.IsEmpty && _videoSourceRect.Width > 0;
            NativeMethods.RECT src = default(NativeMethods.RECT);
            if (useSourceCrop)
            {
                var clientOrigin = new NativeMethods.POINT { x = 0, y = 0 };
                NativeMethods.ClientToScreen(_sourceHwnd, ref clientOrigin);
                src = new NativeMethods.RECT
                {
                    left   = (int)_videoSourceRect.Left   - clientOrigin.x,
                    top    = (int)_videoSourceRect.Top    - clientOrigin.y,
                    right  = (int)_videoSourceRect.Right  - clientOrigin.x,
                    bottom = (int)_videoSourceRect.Bottom - clientOrigin.y
                };
            }

            int flags = NativeMethods.DWM_TNP_VISIBLE
                      | NativeMethods.DWM_TNP_RECTDESTINATION
                      | NativeMethods.DWM_TNP_OPACITY
                      | NativeMethods.DWM_TNP_SOURCECLIENTAREAONLY;
            if (useSourceCrop) flags |= NativeMethods.DWM_TNP_RECTSOURCE;

            var props = new NativeMethods.DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags               = flags,
                rcDestination         = clientRect,
                rcSource              = src,
                opacity               = 255,
                fVisible              = true,
                fSourceClientAreaOnly = true
            };
            NativeMethods.DwmUpdateThumbnailProperties(_overlayThumbHandle, ref props);
        }

        private void CloseFullscreenOverlay()
        {
            // Uncloak VS before closing overlay so it reappears immediately
            if (_overlayRootHwnd != IntPtr.Zero)
                NativeMethods.DwmSetWindowAttribute(
                    _overlayRootHwnd, NativeMethods.DWMWA_CLOAK, 0, 4);

            if (_overlayThumbHandle != IntPtr.Zero)
            {
                NativeMethods.DwmUnregisterThumbnail(_overlayThumbHandle);
                _overlayThumbHandle = IntPtr.Zero;
            }
            _overlayExitBar = null;
            _fullscreenOverlay?.Close();
            _fullscreenOverlay = null;

            // Restore VS to its pre-fullscreen size and position
            if (_overlayRootHwnd != IntPtr.Zero)
            {
                var r = _savedVsRect;
                NativeMethods.SetWindowPos(
                    _overlayRootHwnd, IntPtr.Zero,
                    r.left, r.top, r.right - r.left, r.bottom - r.top,
                    NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
                _overlayRootHwnd = IntPtr.Zero;
            }
        }

        // ── VS title bar hide / restore ──────────────────────────────────────

        private void HideVSTitleBar()
        {
            _hiddenTitleBarHwnds.Clear();

            var hwndSource = PresentationSource.FromVisual(this) as HwndSource;
            if (hwndSource == null) return;

            IntPtr rootHwnd  = GetRootHwnd();
            IntPtr current   = hwndSource.Handle;

            // Walk up the HWND parent chain, stopping before the VS root window.
            // At each level hide sibling child windows, these are the title bar,
            // grip, and button controls drawn by VS around our content.
            while (true)
            {
                IntPtr parent = NativeMethods.GetParent(current);
                if (parent == IntPtr.Zero) break;

                IntPtr captured = current;
                NativeMethods.EnumChildWindows(parent, (child, lParam) =>
                {
                    if (child != captured && NativeMethods.IsWindowVisible(child))
                    {
                        NativeMethods.ShowWindow(child, NativeMethods.SW_HIDE);
                        _hiddenTitleBarHwnds.Add(child);
                    }
                    return true;
                }, IntPtr.Zero);

                if (parent == rootHwnd) break;
                current = parent;
            }
        }

        private void ShowVSTitleBar()
        {
            foreach (IntPtr hwnd in _hiddenTitleBarHwnds)
                NativeMethods.ShowWindow(hwnd, NativeMethods.SW_SHOW);
            _hiddenTitleBarHwnds.Clear();
        }

        private void ContentArea_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isBorderless) return;

            // Re-show the exit button and reset the auto-hide timer on mouse move
            MiniBar.Visibility = Visibility.Visible;
            _miniBarTimer.Stop();
            _miniBarTimer.Start();
        }

        // ── Resize ───────────────────────────────────────────────────────────

        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateThumbnailPosition();
        }

        // ── Cleanup ──────────────────────────────────────────────────────────

        private void StopContent_Click(object sender, RoutedEventArgs e)
        {
            ClearContent();
            WindowPicker.SelectedItem = null;
        }

        private void ClearContent()
        {
            CloseFullscreenOverlay();
            ClearThumbnail();

            // Stop WebView2 playback so audio doesn't continue in the background
            // when the user switches to a mirror source or hits the stop button.
            if (Browser.CoreWebView2 != null)
                Browser.CoreWebView2.Navigate("about:blank");

            ThumbnailArea.Visibility = Visibility.Collapsed;
            Browser.Visibility       = Visibility.Collapsed;
            Placeholder.Visibility   = Visibility.Visible;
            PlaceholderTitle.Text    = "</BingeCode>";
            PlaceholderHint.Text     = "Pick a video window above, paste a URL, or open a local file.";
        }
    }
}

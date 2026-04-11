# `</BingeCode>`

**Watch any video directly inside Visual Studio.**

BingeCode is a Visual Studio 2022 tool window extension with two modes: a **window mirror** that composites a live copy of any running media player or browser window into VS using the Windows DWM Thumbnail API, and an **embedded browser** (WebView2) for navigating directly to any URL or local video file.

---

## Features

- **Window mirror**: pick any visible window from the dropdown; a live, hardware-accelerated DWM thumbnail appears inside the tool window. The original window is not moved or modified.
- **Auto-crop**: on selection, BingeCode automatically detects and crops to just the video area, stripping browser chrome, sidebars, and address bars. Detection runs in three progressively refined passes in the background, finishing on the actual moving region of the video.
- **Keep-alive**: keeps the source window active in the background so video does not pause or throttle when VS has focus
- **Embedded browser**: paste any URL into the URL bar and navigate inside the extension using a full Edge-based browser. Supports login, in-page navigation, and fullscreen video playback. Also opens local video files (mp4, mkv, avi, mov, wmv, webm, flv)
- **Fullscreen (mirror mode)**: F11 or double-click opens a separate always-on-top overlay window with the mirrored video filling the screen. Resizable and draggable.
- **Fullscreen (browser mode)**: F11 or double-click expands the browser to fill the tool window with a small exit bar at the top.
- **Aspect-ratio correct**: the thumbnail crop is center-fitted to match the destination aspect ratio so no black bars appear.
- **Window filter**: system shell processes (Explorer, Start Menu, Search, VS itself, GPU overlays, etc.) are excluded from the picker automatically.

---

## Known limitations

- **Chromium windows in mirror mode**: Chrome and Edge stop rendering when their window is covered by another opaque window. The keep-alive timer fights this, but it is not always reliable. If the mirrored video goes blank when VS has focus, use the URL bar instead.
- **Fullscreen in browser mode**: going fullscreen inside the WebView2 will not hide the BingeCode toolbar. This is a limitation of how VS hosts tool windows and is being worked on.

---

## Installation

**From the Marketplace (recommended)**

Search for **BingeCode** in the [Visual Studio Marketplace](https://marketplace.visualstudio.com/) or install from **Extensions > Manage Extensions** inside VS.

After installing, open the tool window via **View > Other Windows > BingeCode**.

**From source**

1. Clone this repository
2. Open `BingeCode.slnx` in Visual Studio 2022
3. Build (`Ctrl+Shift+B`)
4. Double-click the `.vsix` from `BingeCode/bin/Debug/` to install, or press **F5** to launch the experimental VS instance

---

## Usage

| Action | Result |
|---|---|
| Pick a window from the dropdown | Mirrors that window; auto-crop runs in the background |
| Click **⟳** | Refreshes the window list |
| Click **⊡** or press **F11** | Toggles fullscreen |
| Double-click content area | Toggles fullscreen |
| Mouse move in fullscreen | Shows the exit bar for 3 seconds |
| Paste URL + **Enter** or **Go** | Navigates the embedded WebView2 browser |
| Click **...** | Opens a local video file in the embedded browser |
| Click **✕** | Stops and clears all content |

---

## How it works

### Window mirror

BingeCode uses the Windows DWM Thumbnail API to composite a live, GPU-accelerated copy of any window directly onto the VS tool window. The original window is not moved, modified, or captured — DWM handles the rendering entirely. The crop region is refined progressively in the background, starting with an instant browser chrome strip and finishing with motion-based detection to find the exact video area.

To keep video playing while VS has focus, BingeCode periodically signals the source window to stay active in the background. In fullscreen mode, VS is additionally hidden from the OS compositor so it does not interfere with the source window's rendering.

### Embedded browser

The URL bar and file picker use a full Edge-based browser (WebView2) embedded directly in the tool window, with its own isolated browsing session stored in `%LocalAppData%\BingeCode\WebView2`. Only `http://` and `https://` URLs are accepted. Local files must be a recognised video format.

---

## Requirements

- Visual Studio 2022 (17.0+), Community, Professional, or Enterprise
- Windows 10 / 11
- .NET Framework 4.7.2
- Microsoft Edge WebView2 Runtime (included with Windows 11 and recent Windows 10 updates)

---

## License

MIT

using System;

namespace DevFlix
{
    public class WindowInfo
    {
        public IntPtr Handle      { get; set; }
        public string Title       { get; set; }  // display label (includes [procName])
        public string ProcessName { get; set; }

        public override string ToString() => Title;
    }
}

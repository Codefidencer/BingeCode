using Microsoft.VisualStudio.Shell;
using System;
using System.Runtime.InteropServices;

namespace DevFlix
{
    [Guid("ef25525d-eec1-47b6-a659-c4df56ff2721")]
    public class ToolWindow1 : ToolWindowPane
    {
        public ToolWindow1() : base(null)
        {
            this.Caption = "</DevFlix>";
            this.Content = new ToolWindow1Control();
        }
    }
}

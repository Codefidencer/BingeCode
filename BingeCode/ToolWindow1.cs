using Microsoft.VisualStudio.Shell;
using System;
using System.Runtime.InteropServices;

namespace BingeCode
{
    [Guid("cc072ec8-e6cb-49c9-a058-a62e552f5f85")]
    public class ToolWindow1 : ToolWindowPane
    {
        public ToolWindow1() : base(null)
        {
            this.Caption = "BingeCode";
            this.Content = new ToolWindow1Control();
        }
    }
}

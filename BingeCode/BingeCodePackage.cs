using Microsoft.VisualStudio.Shell;
using System;
using System.Runtime.InteropServices;
using System.Threading;
using Task = System.Threading.Tasks.Task;

namespace BingeCode
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [Guid(BingeCodePackage.PackageGuidString)]
    [ProvideToolWindow(typeof(ToolWindow1))]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    public sealed class BingeCodePackage : AsyncPackage
    {
        public const string PackageGuidString = "e9b5e54c-c718-45a7-b7d7-743a86edc3a8";

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await this.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            await ToolWindow1Command.InitializeAsync(this);
        }
    }
}

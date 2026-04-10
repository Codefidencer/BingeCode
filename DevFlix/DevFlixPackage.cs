using Microsoft.VisualStudio.Shell;
using System;
using System.Runtime.InteropServices;
using System.Threading;
using Task = System.Threading.Tasks.Task;

namespace DevFlix
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [Guid(DevFlixPackage.PackageGuidString)]
    [ProvideToolWindow(typeof(ToolWindow1))]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    public sealed class DevFlixPackage : AsyncPackage
    {
        public const string PackageGuidString = "645fc830-4b16-42f1-8ca4-859227eb8939";

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await this.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            await ToolWindow1Command.InitializeAsync(this);
        }
    }
}

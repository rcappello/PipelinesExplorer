using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using Microsoft.VisualStudio.Extensibility.Shell;

namespace PipelinesExplorer.VisualStudio.Commands;

/// <summary>Signs out of Azure DevOps and clears the cached session.</summary>
[VisualStudioContribution]
internal sealed class SignOutCommand : Command
{
    public override CommandConfiguration CommandConfiguration => new("%PipelinesExplorer.Command.SignOut.DisplayName%")
    {
        Placements = [CommandPlacement.KnownPlacements.ToolsMenu],
    };

    public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken cancellationToken)
    {
        ExtensionServices.Initialize(this.Extensibility);
        try
        {
            await ExtensionServices.Auth.SignOutAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ExtensionServices.Logger.Error("Sign out failed", ex);
        }
    }
}

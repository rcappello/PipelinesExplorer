using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using Microsoft.VisualStudio.Extensibility.Shell;

namespace PipelinesExplorer.VisualStudio.Commands;

/// <summary>Sign in to Azure DevOps with a personal access token.</summary>
[VisualStudioContribution]
internal sealed class SignInWithPatCommand : Command
{
    public override CommandConfiguration CommandConfiguration => new("%PipelinesExplorer.Command.SignInPat.DisplayName%")
    {
        Placements = [CommandPlacement.KnownPlacements.ToolsMenu],
        Icon = new(ImageMoniker.KnownValues.Lock, IconSettings.IconAndText),
    };

    public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken cancellationToken)
    {
        ExtensionServices.Initialize(this.Extensibility);
        // Free-form text input is not yet exposed by the Extensibility shell;
        // route the user to the welcome panel inside the Pipelines Explorer
        // tool window where the PAT input field lives.
        await this.Extensibility.Shell().ShowToolWindowAsync<ToolWindows.PipelinesToolWindow>(activate: true, cancellationToken).ConfigureAwait(false);
    }
}

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using Microsoft.VisualStudio.Extensibility.Shell;

namespace PipelinesExplorer.VisualStudio.Commands;

/// <summary>
/// Placeholder command that, in a future iteration, will trigger Microsoft Entra ID
/// sign-in via MSAL + WAM broker (mirroring the VS Code client's behaviour).
/// </summary>
[VisualStudioContribution]
internal sealed class SignInWithMicrosoftCommand : Command
{
    /// <inheritdoc />
    public override CommandConfiguration CommandConfiguration => new("%PipelinesExplorer.SignInWithMicrosoft.DisplayName%")
    {
        Placements = [CommandPlacement.KnownPlacements.ToolsMenu],
        Icon = new(ImageMoniker.KnownValues.User, IconSettings.IconAndText),
    };

    /// <inheritdoc />
    public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken cancellationToken)
    {
        await this.Extensibility.Shell().ShowPromptAsync(
            "Pipelines Explorer: Microsoft sign-in is not implemented yet in the VS 2026 client.",
            PromptOptions.OK,
            cancellationToken);
    }
}

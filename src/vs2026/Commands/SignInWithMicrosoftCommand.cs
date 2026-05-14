using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using Microsoft.VisualStudio.Extensibility.Shell;

namespace PipelinesExplorer.VisualStudio.Commands;

/// <summary>Triggers Microsoft Entra ID sign-in via MSAL + WAM broker.</summary>
[VisualStudioContribution]
internal sealed class SignInWithMicrosoftCommand : Command
{
    public override CommandConfiguration CommandConfiguration => new("Pipelines Explorer: Sign in with Microsoft")
    {
        Placements = [CommandPlacement.KnownPlacements.ToolsMenu],
        Icon = new(ImageMoniker.KnownValues.User, IconSettings.IconAndText),
    };

    public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken cancellationToken)
    {
        ExtensionServices.Initialize(this.Extensibility);
        try
        {
            await ExtensionServices.Auth.SignInWithMicrosoftAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ExtensionServices.Logger.Error("Microsoft sign in failed", ex);
            await this.Extensibility.Shell().ShowPromptAsync(
                $"Microsoft sign in failed: {ex.Message}",
                PromptOptions.OK,
                cancellationToken).ConfigureAwait(false);
        }
    }
}

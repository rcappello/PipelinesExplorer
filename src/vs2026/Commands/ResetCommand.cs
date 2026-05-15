using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using Microsoft.VisualStudio.Extensibility.Shell;

using PipelinesExplorer.VisualStudio.Resources;

namespace PipelinesExplorer.VisualStudio.Commands;

/// <summary>Wipes credentials, links and branch overrides. Mirrors VS Code's <c>pipelinesexplorer.reset</c>.</summary>
[VisualStudioContribution]
internal sealed class ResetCommand : Command
{
    public override CommandConfiguration CommandConfiguration => new("%PipelinesExplorer.Command.Reset.DisplayName%")
    {
        Placements = [CommandPlacement.KnownPlacements.ToolsMenu],
    };

    public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken cancellationToken)
    {
        ExtensionServices.Initialize(this.Extensibility);

        var confirm = await this.Extensibility.Shell().ShowPromptAsync(
            Strings.Reset_Confirm,
            PromptOptions.OKCancel,
            cancellationToken).ConfigureAwait(false);
        if (!confirm) { return; }

        try
        {
            await ExtensionServices.Auth.ResetAsync(cancellationToken).ConfigureAwait(false);
            // No public Reset on link/branch services yet; clear by enumeration is a TODO for Phase 5.
        }
        catch (Exception ex)
        {
            ExtensionServices.Logger.Error("Reset failed", ex);
            await this.Extensibility.Shell().ShowPromptAsync(
                string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.Reset_Failed_Format, ex.Message),
                PromptOptions.OK,
                cancellationToken).ConfigureAwait(false);
        }
    }
}

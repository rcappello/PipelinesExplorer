using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using Microsoft.VisualStudio.Extensibility.Shell;
using PipelinesExplorer.VisualStudio.Resources;

namespace PipelinesExplorer.VisualStudio.Commands;

/// <summary>
/// Lists the tenants of the signed-in Microsoft account and lets the user
/// switch to one of them. Mirrors <c>pipelinesexplorer.selectTenant</c>.
/// </summary>
[VisualStudioContribution]
internal sealed class SelectTenantCommand : Command
{
    public override CommandConfiguration CommandConfiguration => new("%PipelinesExplorer.Command.SelectTenant.DisplayName%")
    {
        Placements = [CommandPlacement.KnownPlacements.ToolsMenu],
    };

    public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken cancellationToken)
    {
        ExtensionServices.Initialize(this.Extensibility);
        try
        {
            var tenants = await ExtensionServices.Auth.ListAvailableTenantsAsync(cancellationToken).ConfigureAwait(false);
            if (tenants.Count == 0)
            {
                await this.Extensibility.Shell().ShowPromptAsync(
                    Strings.TenantPicker_NoTenants,
                    PromptOptions.OK,
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            // Build a dynamic prompt listing each tenant by display name.
            var options = new PromptOptions<int> { DismissedReturns = -1, DefaultChoiceIndex = 0 };
            for (var i = 0; i < tenants.Count; i++)
            {
                var t = tenants[i];
                options.Choices.Add($"{t.DisplayName}  ({t.TenantId})", i);
            }

            var picked = await this.Extensibility.Shell().ShowPromptAsync(
                Strings.SelectTenant_Prompt,
                options,
                cancellationToken).ConfigureAwait(false);
            if (picked < 0) { return; }

            var chosen = tenants[picked];
            await ExtensionServices.Auth.SwitchTenantAsync(chosen.TenantId, chosen.DisplayName, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ExtensionServices.Logger.Error("Select tenant failed", ex);
            await this.Extensibility.Shell().ShowPromptAsync(
                string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.SelectTenant_Failed_Format, ex.Message),
                PromptOptions.OK,
                cancellationToken).ConfigureAwait(false);
        }
    }
}

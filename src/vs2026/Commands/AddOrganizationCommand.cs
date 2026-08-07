using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using Microsoft.VisualStudio.Extensibility.Shell;
using PipelinesExplorer.VisualStudio.Auth;

namespace PipelinesExplorer.VisualStudio.Commands;

/// <summary>
/// Opens the Pipelines Explorer tool window and toggles the inline
/// "Add Azure DevOps organization" panel so the user can add another
/// per-organization PAT on top of an existing PAT sign-in (plan 002 §2.3).
/// No-op when the user is not signed in via PAT.
/// </summary>
[VisualStudioContribution]
internal sealed class AddOrganizationCommand : Command
{
    public override CommandConfiguration CommandConfiguration => new("%PipelinesExplorer.Command.AddOrganization.DisplayName%")
    {
        Placements = [CommandPlacement.KnownPlacements.ToolsMenu],
        Icon = new(ImageMoniker.KnownValues.Add, IconSettings.IconAndText),
    };

    public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken cancellationToken)
    {
        ExtensionServices.Initialize(this.Extensibility);

        var session = ExtensionServices.Auth.Session;
        if (session is null || session.Kind != SignInKind.Pat)
        {
            await this.Extensibility.Shell().ShowPromptAsync(
                Resources.Strings.AddOrg_RequiresPatSignIn,
                PromptOptions.OK,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        await this.Extensibility.Shell().ShowToolWindowAsync<ToolWindows.PipelinesToolWindow>(activate: true, cancellationToken).ConfigureAwait(false);
        // Fire the VM command so state is set on the same thread the tool window
        // control marshals updates through.
        ExtensionServices.ViewModel.OpenAddOrgPanel();
    }
}

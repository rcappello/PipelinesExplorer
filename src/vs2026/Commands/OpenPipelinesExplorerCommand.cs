using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using Microsoft.VisualStudio.Extensibility.Shell;
using PipelinesExplorer.VisualStudio.ToolWindows;

namespace PipelinesExplorer.VisualStudio.Commands;

/// <summary>Opens the "Pipelines Explorer" tool window.</summary>
[VisualStudioContribution]
internal sealed class OpenPipelinesExplorerCommand : Command
{
    public override CommandConfiguration CommandConfiguration => new("Pipelines Explorer")
    {
        Placements = [CommandPlacement.KnownPlacements.ViewOtherWindowsMenu],
        Icon = new(ImageMoniker.KnownValues.User, IconSettings.IconAndText),
    };

    public override Task ExecuteCommandAsync(IClientContext context, CancellationToken cancellationToken) =>
        this.Extensibility.Shell().ShowToolWindowAsync<PipelinesToolWindow>(activate: true, cancellationToken);
}

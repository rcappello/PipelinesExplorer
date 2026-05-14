using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using Microsoft.VisualStudio.Extensibility.Shell;

namespace PipelinesExplorer.VisualStudio.Commands;

/// <summary>Opens today's log file in the default editor (or shows the folder if it doesn't exist yet).</summary>
[VisualStudioContribution]
internal sealed class ShowLogsCommand : Command
{
    public override CommandConfiguration CommandConfiguration => new("Pipelines Explorer: Show logs")
    {
        Placements = [CommandPlacement.KnownPlacements.ToolsMenu],
    };

    public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken cancellationToken)
    {
        ExtensionServices.Initialize(this.Extensibility);
        try
        {
            var path = ExtensionServices.Logger.LogFilePath;
            var target = (path is not null && File.Exists(path)) ? path :
                (path is not null ? Path.GetDirectoryName(path)! :
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PipelinesExplorer", "logs"));
            Process.Start(new ProcessStartInfo
            {
                FileName = target,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            ExtensionServices.Logger.Error("Show logs failed", ex);
            await this.Extensibility.Shell().ShowPromptAsync(
                $"Show logs failed: {ex.Message}",
                PromptOptions.OK,
                cancellationToken).ConfigureAwait(false);
        }
    }
}

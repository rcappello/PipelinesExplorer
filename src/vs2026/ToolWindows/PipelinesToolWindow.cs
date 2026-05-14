using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.ToolWindows;
using Microsoft.VisualStudio.RpcContracts.RemoteUI;

namespace PipelinesExplorer.VisualStudio.ToolWindows;

/// <summary>
/// "Pipelines Explorer" tool window — VS 2026 counterpart of the VS Code
/// custom view declared in the <c>pipelinesexplorer-pipelines</c> view
/// container. Hosts a <see cref="PipelinesToolWindowControl"/> bound to the
/// shared <see cref="ViewModels.PipelinesViewModel"/>.
/// </summary>
[VisualStudioContribution]
internal sealed class PipelinesToolWindow : ToolWindow
{
    private PipelinesToolWindowControl? _content;

    public override ToolWindowConfiguration ToolWindowConfiguration => new()
    {
        Placement = ToolWindowPlacement.DocumentWell,
    };

    public override async Task InitializeAsync(CancellationToken cancellationToken)
    {
        ExtensionServices.Initialize(this.Extensibility);
        Title = "Pipelines Explorer";

        // Eagerly trigger silent restore so the UI binds to the right state on first show.
        try
        {
            await ExtensionServices.Auth.InitializeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ExtensionServices.Logger.Error("AdoAuthService initialization failed", ex);
        }
    }

    public override Task<IRemoteUserControl> GetContentAsync(CancellationToken cancellationToken)
    {
        _content ??= new PipelinesToolWindowControl(ExtensionServices.ViewModel);
        return Task.FromResult<IRemoteUserControl>(_content);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _content?.Dispose();
            _content = null;
        }
        base.Dispose(disposing);
    }
}

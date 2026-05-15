using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Documents;
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

    // VisualStudio.Extensibility Output Window APIs (Microsoft.VisualStudio.Extensibility.Documents)
    // are still in preview, so the SDK ships an analyzer (VSEXTPREVIEW_OUTPUTWINDOW) that flags
    // every reference. The official sample (microsoft/VSExtensibility,
    // New_Extensibility_Model/Samples/OutputWindowSample/TestOutputWindowCommand.cs) suppresses it
    // with a #pragma — we follow the same pattern.
#pragma warning disable VSEXTPREVIEW_OUTPUTWINDOW
    private OutputChannel? _outputChannel;
#pragma warning restore VSEXTPREVIEW_OUTPUTWINDOW

    public override ToolWindowConfiguration ToolWindowConfiguration => new()
    {
        Placement = ToolWindowPlacement.DocumentWell,
    };

#pragma warning disable VSEXTPREVIEW_OUTPUTWINDOW
    public override async Task InitializeAsync(CancellationToken cancellationToken)
#pragma warning restore VSEXTPREVIEW_OUTPUTWINDOW
    {
        ExtensionServices.Initialize(this.Extensibility);
        Title = Branding.ProductName;

        // Wire the Output Window channel as a sink on the shared logger so every
        // log line surfaces in View > Output > "Pipelines Explorer". Documented at
        // https://learn.microsoft.com/visualstudio/extensibility/visualstudio.extensibility/output-window/output-window
        // CreateOutputChannelAsync can only be called once per displayName per
        // extension instance; the tool window is created at most once so this is safe.
        try
        {
#pragma warning disable VSEXTPREVIEW_OUTPUTWINDOW
            _outputChannel = await this.Extensibility.Views().Output
                .CreateOutputChannelAsync(Branding.ProductName, cancellationToken)
                .ConfigureAwait(false);

            ExtensionServices.Logger.AttachSink(line =>
            {
                // OutputChannel.Writer is a TextWriter; fire-and-forget so logging never blocks.
                _ = _outputChannel?.Writer.WriteLineAsync(line);
            });
#pragma warning restore VSEXTPREVIEW_OUTPUTWINDOW

            ExtensionServices.Logger.Info("Output channel attached.");
        }
        catch (Exception ex)
        {
            // File log + Trace remain available even if the Output channel fails.
            ExtensionServices.Logger.Warn($"Could not create Output channel: {ex.Message}");
        }

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

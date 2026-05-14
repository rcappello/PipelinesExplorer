using System;
using Microsoft.VisualStudio.Extensibility;
using PipelinesExplorer.VisualStudio.Auth;
using PipelinesExplorer.VisualStudio.AzureDevOps;
using PipelinesExplorer.VisualStudio.Services;
using PipelinesExplorer.VisualStudio.ViewModels;

namespace PipelinesExplorer.VisualStudio;

/// <summary>
/// Tiny composition root for the extension. Out-of-process VS extensions don't
/// have a per-instance container we can inject through, so we expose lazily-
/// initialised singletons. <see cref="Initialize"/> is called by the first
/// component that has access to a <see cref="VisualStudioExtensibility"/>
/// instance (commands, tool windows) so the view-model can prompt and show
/// dialogs through the shell.
/// </summary>
internal static class ExtensionServices
{
    private static readonly System.Threading.Lock _gate = new();
    private static LoggingService? _logger;
    private static AdoAuthService? _auth;
    private static AdoClient? _ado;
    private static WorkspaceLinkService? _links;
    private static RepoBranchService? _branches;
    private static PipelineYamlAnalyzer? _analyzer;
    private static OpenItemService? _openItem;
    private static PipelinesViewModel? _viewModel;
    private static VisualStudioExtensibility? _extensibility;

    public static void Initialize(VisualStudioExtensibility extensibility)
    {
        ArgumentNullException.ThrowIfNull(extensibility);
        lock (_gate)
        {
            _extensibility ??= extensibility;
        }
    }

    public static VisualStudioExtensibility? Extensibility => _extensibility;

    public static LoggingService Logger
    {
        get
        {
            if (_logger is null) lock (_gate) { _logger ??= new LoggingService(); }
            return _logger;
        }
    }

    public static AdoAuthService Auth
    {
        get
        {
            if (_auth is null) lock (_gate) { _auth ??= new AdoAuthService(Logger); }
            return _auth;
        }
    }

    public static AdoClient Ado
    {
        get
        {
            if (_ado is null) lock (_gate) { _ado ??= new AdoClient(Logger, Auth); }
            return _ado;
        }
    }

    public static WorkspaceLinkService Links
    {
        get
        {
            if (_links is null) lock (_gate) { _links ??= new WorkspaceLinkService(Logger); }
            return _links;
        }
    }

    public static RepoBranchService Branches
    {
        get
        {
            if (_branches is null) lock (_gate) { _branches ??= new RepoBranchService(Logger); }
            return _branches;
        }
    }

    public static PipelineYamlAnalyzer Analyzer
    {
        get
        {
            if (_analyzer is null) lock (_gate) { _analyzer ??= new PipelineYamlAnalyzer(Ado, Logger); }
            return _analyzer;
        }
    }

    public static OpenItemService OpenItem
    {
        get
        {
            if (_openItem is null) lock (_gate) { _openItem ??= new OpenItemService(Links, Logger, () => _extensibility); }
            return _openItem;
        }
    }

    public static PipelinesViewModel ViewModel
    {
        get
        {
            if (_viewModel is null)
            {
                lock (_gate)
                {
                    _viewModel ??= new PipelinesViewModel(Logger, Auth, Ado, Links, Branches, Analyzer, OpenItem, () => _extensibility);
                }
            }
            return _viewModel;
        }
    }
}

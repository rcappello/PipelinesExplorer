using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Shell;
using Microsoft.VisualStudio.Extensibility.UI;
using PipelinesExplorer.VisualStudio.Auth;
using PipelinesExplorer.VisualStudio.AzureDevOps;
using PipelinesExplorer.VisualStudio.Resources;
using PipelinesExplorer.VisualStudio.Services;

namespace PipelinesExplorer.VisualStudio.ViewModels;

/// <summary>
/// Root view-model for the Pipelines tool window. Owns the connection state
/// (signed-in/out, account label, busy/error indicators) and the lazily-loaded
/// tree of organisations → projects → repositories → pipelines.
/// </summary>
[DataContract]
public sealed class PipelinesViewModel : NotifyPropertyChangedObject
{
    private readonly LoggingService _logger;
    private readonly AdoAuthService _auth;
    private readonly AdoClient _ado;
    private readonly WorkspaceLinkService _links;
    private readonly RepoBranchService _branches;
    private readonly PipelineYamlAnalyzer _analyzer;
    private readonly OpenItemService _openItem;
    private readonly Func<VisualStudioExtensibility?> _extensibilityProvider;

    private bool _isSignedIn;
    private bool _isMicrosoftSignIn;
    private bool _isBusy;
    private string? _connectionLabel;
    private string? _connectionTooltip;
    private string? _errorMessage;
    private string _patInputText = string.Empty;
    private CancellationTokenSource? _loadCts;
    // One-shot gate: when an Ado 401/403 triggers the recovery prompt we set
    // this flag so subsequent failures in the same broken session don't keep
    // re-showing the same modal. Cleared on session change / sign-in success.
    private bool _unauthorizedHandled;

    // Filter state (Plan 001). Kept together for clarity.
    private const int FilterPipelineCap = 500;
    // Maximum recursion depth followed by the filter scan when descending
    // into same-repo nested templates. Guards against pathological template
    // graphs on top of the per-file cycle check.
    private const int FilterMaxTemplateDepth = 10;
    private string _filterText = string.Empty;
    private string? _activeFilterTerm; // already trimmed + lowercased
    private string _filterStatusText = string.Empty;
    private bool _isFilterActive;
    private CancellationTokenSource? _filterDebounceCts;
    private CancellationTokenSource? _filterScanCts;
    private readonly HashSet<TreeNodeViewModel> _matchedNodes = new();
    private readonly HashSet<TreeNodeViewModel> _ancestorNodes = new();

    /// <summary>
    /// Cache of TfsGit repository ids -> display name. Mirrors the
    /// <c>repoNameCache</c> in the VS Code provider: the pipelines list API
    /// only returns the repository GUID for <c>azureReposGit</c> sources, so
    /// we resolve the name once via <see cref="AdoClient.GetRepositoryAsync"/>
    /// and reuse it for every pipeline pointing at the same repo.
    /// </summary>
    private readonly Dictionary<string, string> _repoNameCache = new(StringComparer.OrdinalIgnoreCase);

    public PipelinesViewModel(
        LoggingService logger,
        AdoAuthService auth,
        AdoClient ado,
        WorkspaceLinkService links,
        RepoBranchService branches,
        PipelineYamlAnalyzer analyzer,
        OpenItemService openItem,
        Func<VisualStudioExtensibility?> extensibilityProvider)
    {
        _logger = logger;
        _auth = auth;
        _ado = ado;
        _links = links;
        _branches = branches;
        _analyzer = analyzer;
        _openItem = openItem;
        _extensibilityProvider = extensibilityProvider;
        Roots = new ObservableList<TreeNodeViewModel>();

        RefreshCommand = new AsyncCommand((parameter, clientContext, cancellationToken) => RefreshAsync(cancellationToken));
        SignOutCommand = new AsyncCommand(async (parameter, clientContext, cancellationToken) =>
        {
            try { await _auth.SignOutAsync(cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) { _logger.Error("Sign out failed", ex); SetError(ex.Message); }
        });
        SignInWithMicrosoftCommand = new AsyncCommand(async (parameter, clientContext, cancellationToken) =>
        {
            try { await _auth.SignInWithMicrosoftAsync(cancellationToken: cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) { _logger.Error("Microsoft sign in failed", ex); SetError(ex.Message); }
        });
        SelectTenantCommand = new AsyncCommand((parameter, clientContext, cancellationToken) => SelectTenantAsync(cancellationToken));
        SignInWithPatCommand = new AsyncCommand(async (parameter, clientContext, cancellationToken) =>
        {
            try
            {
                var pat = _patInputText;
                if (string.IsNullOrWhiteSpace(pat))
                {
                    SetError("Paste a PAT into the field first.");
                    return;
                }
                await _auth.SignInWithPatAsync(pat, cancellationToken).ConfigureAwait(false);
                PatInputText = string.Empty;
            }
            catch (Exception ex) { _logger.Error("PAT sign in failed", ex); SetError(ex.Message); }
        });
        ClearFilterCommand = new AsyncCommand((_, _, _) =>
        {
            FilterText = string.Empty;
            return Task.CompletedTask;
        });

        _auth.SessionChanged += (_, s) => OnSessionChanged(s);
        _links.Changed += (_, _) => RefreshFireAndForget();
        _branches.Changed += (_, _) => RefreshFireAndForget();

        OnSessionChanged(_auth.Session);
    }

    [DataMember]
    public ObservableList<TreeNodeViewModel> Roots { get; }

    /// <summary>
    /// Localized strings consumed by the Remote UI XAML. See
    /// <see cref="LocalizedStrings"/> for why XAML can't reference
    /// <see cref="Resources.Strings"/> directly.
    /// </summary>
    [DataMember]
    public LocalizedStrings Loc { get; } = new LocalizedStrings();

    [DataMember]
    public bool IsSignedIn
    {
        get => _isSignedIn;
        private set
        {
            if (SetProperty(ref _isSignedIn, value))
            {
                RaiseNotifyPropertyChangedEvent(nameof(IsSignedOut));
            }
        }
    }

    /// <summary>True only when the active session was created via Microsoft Entra (drives tenant-switch button).</summary>
    [DataMember]
    public bool IsMicrosoftSignIn
    {
        get => _isMicrosoftSignIn;
        private set => SetProperty(ref _isMicrosoftSignIn, value);
    }

    /// <summary>True when the user has not signed in yet — drives welcome-panel visibility.</summary>
    [DataMember]
    public bool IsSignedOut => !_isSignedIn;

    [DataMember]
    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    [DataMember]
    public string? ConnectionLabel
    {
        get => _connectionLabel;
        private set => SetProperty(ref _connectionLabel, value);
    }

    /// <summary>Detailed multi-line tooltip shown when hovering the connection label.</summary>
    [DataMember]
    public string? ConnectionTooltip
    {
        get => _connectionTooltip;
        private set => SetProperty(ref _connectionTooltip, value);
    }

    [DataMember]
    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                RaiseNotifyPropertyChangedEvent(nameof(HasError));
            }
        }
    }

    [DataMember]
    public bool HasError => !string.IsNullOrEmpty(_errorMessage);

    /// <summary>Two-way bound textbox for the PAT entered in the welcome panel.</summary>
    [DataMember]
    public string PatInputText
    {
        get => _patInputText;
        set => SetProperty(ref _patInputText, value ?? string.Empty);
    }

    [DataMember]
    public AsyncCommand RefreshCommand { get; }

    [DataMember]
    public AsyncCommand SignOutCommand { get; }

    [DataMember]
    public AsyncCommand SignInWithMicrosoftCommand { get; }

    /// <summary>Open a popup to switch the Microsoft Entra tenant.</summary>
    [DataMember]
    public AsyncCommand SelectTenantCommand { get; }

    [DataMember]
    public AsyncCommand SignInWithPatCommand { get; }

    /// <summary>
    /// Two-way bound text of the filter box. Changes are debounced (~200ms)
    /// before triggering a scan; typing quickly does not spawn one scan per
    /// keystroke.
    /// </summary>
    [DataMember]
    public string FilterText
    {
        get => _filterText;
        set
        {
            var v = value ?? string.Empty;
            if (SetProperty(ref _filterText, v))
            {
                ScheduleFilterScan(v);
            }
        }
    }

    /// <summary>Status text rendered next to the filter box (scanning / result count / capped notice).</summary>
    [DataMember]
    public string FilterStatusText
    {
        get => _filterStatusText;
        private set => SetProperty(ref _filterStatusText, value);
    }

    /// <summary>True when a non-empty filter is currently applied; drives visibility of the status text and the clear button.</summary>
    [DataMember]
    public bool IsFilterActive
    {
        get => _isFilterActive;
        private set => SetProperty(ref _isFilterActive, value);
    }

    /// <summary>Clears the current filter without touching the loaded tree.</summary>
    [DataMember]
    public AsyncCommand ClearFilterCommand { get; }

    /// <summary>Reload the top-level tree from Azure DevOps.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (!_isSignedIn)
        {
            Roots.Clear();
            return;
        }

        _loadCts?.Cancel();
        _loadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ct = _loadCts.Token;

        IsBusy = true;
        SetError(null);
        try
        {
            var profile = await _ado.GetProfileAsync(ct).ConfigureAwait(false);
            var orgs = await _ado.ListOrganizationsAsync(profile.Id, ct).ConfigureAwait(false);
            _logger.Info($"Loaded {orgs.Count} organization(s) for {profile.DisplayName ?? profile.Id}");
            if (orgs.Count == 0)
            {
                ReplaceList(Roots, new TreeNodeViewModel[]
                {
                    new InfoNode(Strings.Tree_NoOrganizations, TreeNodeKind.Info),
                });
            }
            else
            {
                var orgNodes = orgs
                    .OrderBy(o => o.AccountName, StringComparer.OrdinalIgnoreCase)
                    .Select(BuildOrganizationNode)
                    .Cast<TreeNodeViewModel>()
                    .ToList();
                ReplaceList(Roots, orgNodes);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (AdoUnauthorizedException ex)
        {
            _logger.Warn($"Refresh failed (unauthorized): {ex.Message}");
            SetError(ex.Message);
            ReplaceList(Roots, new TreeNodeViewModel[] { new InfoNode(ex.Message, TreeNodeKind.Error) });
            await HandleUnauthorizedAsync(ex, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Error("Refresh failed", ex);
            SetError(ex.Message);
            ReplaceList(Roots, new TreeNodeViewModel[] { new InfoNode(ex.Message, TreeNodeKind.Error) });
        }
        finally
        {
            IsBusy = false;
        }

        // A refresh rebuilds every node in Roots, so the object references
        // stored in _matchedNodes / _ancestorNodes are stale. Re-run the scan
        // on the newly-materialised tree when a filter is active.
        if (IsFilterActive && !string.IsNullOrEmpty(_filterText))
        {
            ScheduleFilterScan(_filterText);
        }
    }

    private OrganizationNode BuildOrganizationNode(AdoOrganization org)
    {
        var node = new OrganizationNode(org);
        node.Children.Add(new InfoNode(Strings.Tree_Loading, TreeNodeKind.Loading));
        SubscribeLazyLoad(node, () => LoadProjectsAsync(node));
        return node;
    }

    private async Task LoadProjectsAsync(OrganizationNode node)
    {
        try
        {
            var projects = await _ado.ListProjectsAsync(node.Organization.AccountName).ConfigureAwait(false);
            var children = projects
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .Select(p => BuildProjectNode(node.Organization, p))
                .Cast<TreeNodeViewModel>()
                .ToList();
            ReplaceList(node.Children, children.Count == 0
                ? new TreeNodeViewModel[] { new InfoNode(Strings.Tree_NoProjects, TreeNodeKind.Info) }
                : children);
        }
        catch (Exception ex)
        {
            _logger.Error($"Loading projects of {node.Organization.AccountName} failed", ex);
            ReplaceList(node.Children, new TreeNodeViewModel[] { new InfoNode(ex.Message, TreeNodeKind.Error) });
        }
    }

    private ProjectNode BuildProjectNode(AdoOrganization org, AdoProject project)
    {
        var node = new ProjectNode(org, project);
        node.Children.Add(new InfoNode(Strings.Tree_Loading, TreeNodeKind.Loading));
        SubscribeLazyLoad(node, () => LoadPipelinesAsync(node));
        return node;
    }

    private async Task LoadPipelinesAsync(ProjectNode node)
    {
        try
        {
            var pipelines = await _ado.ListPipelinesAsync(node.Organization.AccountName, node.Project.Name).ConfigureAwait(false);

            var details = new List<(AdoPipeline pipe, AdoPipelineDetail? detail)>(pipelines.Count);
            foreach (var batch in Chunk(pipelines, 8))
            {
                var tasks = batch.Select(async p =>
                {
                    try
                    {
                        var d = await _ado.GetPipelineAsync(node.Organization.AccountName, node.Project.Name, p.Id).ConfigureAwait(false);
                        return (p, (AdoPipelineDetail?)d);
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn($"GetPipeline {p.Name} failed: {ex.Message}");
                        return (p, (AdoPipelineDetail?)null);
                    }
                });
                details.AddRange(await Task.WhenAll(tasks).ConfigureAwait(false));
            }

            var byRepo = new Dictionary<string, List<(AdoPipeline pipe, AdoPipelineDetail? detail)>>(StringComparer.OrdinalIgnoreCase);
            string KeyOf(AdoPipelineDetail? d) =>
                d?.Configuration?.Repository?.Id ?? d?.Configuration?.Repository?.FullName ?? "(unknown)";
            string? TypeOf(AdoPipelineDetail? d) => d?.Configuration?.Repository?.Type;

            // Resolve display names for TfsGit repositories that the pipelines API
            // didn't include (those come back with only an Id + type=azureReposGit).
            // Mirrors the equivalent block in the VS Code provider.
            var missingIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in details)
            {
                var repo = entry.detail?.Configuration?.Repository;
                if (repo?.Id is { Length: > 0 } id
                    && string.IsNullOrEmpty(repo.Name)
                    && string.IsNullOrEmpty(repo.FullName)
                    && (string.IsNullOrEmpty(repo.Type)
                        || string.Equals(repo.Type, "azureReposGit", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(repo.Type, "TfsGit", StringComparison.OrdinalIgnoreCase))
                    && !_repoNameCache.ContainsKey(id))
                {
                    missingIds.Add(id);
                }
            }
            if (missingIds.Count > 0)
            {
                foreach (var batch in Chunk(missingIds.ToList(), 8))
                {
                    var tasks = batch.Select(async id =>
                    {
                        try
                        {
                            var r = await _ado.GetRepositoryAsync(node.Organization.AccountName, node.Project.Name, id).ConfigureAwait(false);
                            return new KeyValuePair<string, string?>(id, r?.Name);
                        }
                        catch (Exception ex)
                        {
                            _logger.Warn($"Resolve repo {id} failed: {ex.Message}");
                            return new KeyValuePair<string, string?>(id, null);
                        }
                    });
                    foreach (var kv in await Task.WhenAll(tasks).ConfigureAwait(false))
                    {
                        _repoNameCache[kv.Key] = kv.Value ?? Strings.Tree_UnknownRepository;
                    }
                }
            }

            string LabelOf(AdoPipelineDetail? d)
            {
                var repo = d?.Configuration?.Repository;
                if (repo is null) { return Strings.Tree_UnknownRepository; }
                if (!string.IsNullOrEmpty(repo.FullName)) { return repo.FullName!; }
                if (!string.IsNullOrEmpty(repo.Name)) { return repo.Name!; }
                if (!string.IsNullOrEmpty(repo.Id) && _repoNameCache.TryGetValue(repo.Id!, out var cached))
                {
                    return cached;
                }
                return Strings.Tree_UnknownRepository;
            }

            foreach (var entry in details)
            {
                var key = KeyOf(entry.detail);
                if (!byRepo.TryGetValue(key, out var list))
                {
                    list = new List<(AdoPipeline, AdoPipelineDetail?)>();
                    byRepo[key] = list;
                }
                list.Add(entry);
            }

            var repoNodes = new List<TreeNodeViewModel>();
            foreach (var kv in byRepo.OrderBy(k => LabelOf(k.Value[0].detail), StringComparer.OrdinalIgnoreCase))
            {
                var first = kv.Value[0].detail;
                var repoKey = kv.Key;
                var label = LabelOf(first);
                var type = TypeOf(first);
                var linkKey = new RepoLinkKey(node.Organization.AccountId, node.Project.Id, repoKey);
                var linked = _links.Get(linkKey);
                var branch = _branches.Get(linkKey);
                var repoNode = new RepositoryNode(node.Organization, node.Project, repoKey, label, type, linked, branch, kv.Value.Count);
                WireRepositoryCommands(repoNode);

                foreach (var (pipe, detail) in kv.Value.OrderBy(t => t.pipe.Name, StringComparer.OrdinalIgnoreCase))
                {
                    var pipeNode = BuildPipelineNode(node.Organization, node.Project, pipe, detail, repoKey);
                    repoNode.Children.Add(pipeNode);
                }
                repoNodes.Add(repoNode);
            }

            ReplaceList(node.Children, repoNodes.Count == 0
                ? new TreeNodeViewModel[] { new InfoNode(Strings.Tree_NoPipelines, TreeNodeKind.Info) }
                : repoNodes);
        }
        catch (Exception ex)
        {
            _logger.Error($"Loading pipelines of {node.Project.Name} failed", ex);
            ReplaceList(node.Children, new TreeNodeViewModel[] { new InfoNode(ex.Message, TreeNodeKind.Error) });
        }
    }

    private void OnSessionChanged(AdoSession? session)
    {
        IsSignedIn = session is not null;
        IsMicrosoftSignIn = session is not null && session.Kind == SignInKind.Microsoft;
        // A new session — successful or cleared — resets the one-shot
        // unauthorized prompt gate so a subsequent 401 surfaces the recovery
        // dialog again.
        _unauthorizedHandled = false;

        // Session boundary invalidates any active filter — clear it before
        // Roots are rebuilt so we don't leak stale visibility state.
        FilterText = string.Empty;

        if (session is null)
        {
            ConnectionLabel = null;
            ConnectionTooltip = null;
        }
        else if (session.Kind == SignInKind.Microsoft)
        {
            // Mirrors the VS Code tree header: "Microsoft Entra · <tenant>" with a
            // multi-line tooltip showing the account UPN and the tenant id.
            var storedTenantId = _auth.GetStoredTenant();
            var tenantName = _auth.GetStoredTenantName();
            string tenantDisplay;
            if (string.IsNullOrEmpty(storedTenantId))
            {
                // No explicit override -> the home tenant. Show "Default tenant"
                // (matches VS Code's behaviour) and put the actual id in the tooltip.
                tenantDisplay = Strings.Connection_DefaultTenant;
            }
            else
            {
                tenantDisplay = !string.IsNullOrEmpty(tenantName) ? tenantName! : storedTenantId!;
            }
            ConnectionLabel = string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.Connection_MicrosoftEntra_Format, tenantDisplay);
            var tenantLine = string.IsNullOrEmpty(session.TenantId) ? Strings.Connection_DefaultTenant : session.TenantId!;
            ConnectionTooltip = string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.Connection_Microsoft_Tooltip_Format, session.AccountLabel, tenantLine);
        }
        else
        {
            ConnectionLabel = Strings.Connection_Pat_Label;
            ConnectionTooltip = string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.Connection_Pat_Tooltip_Format, session.AccountLabel);
        }

        if (session is null)
        {
            Roots.Clear();
        }
        else
        {
            RefreshFireAndForget();

            // Pre-warm the tenant list in the background so the switch-tenant
            // dialog opens instantly and does not have to launch a browser
            // sign-in flow on first click.
            if (session.Kind == SignInKind.Microsoft)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _auth.ListAvailableTenantsAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn($"Tenant prefetch failed: {ex.Message}");
                    }
                });
            }
        }
    }

    /// <summary>
    /// Lists the Microsoft Entra tenants the signed-in account has access to,
    /// shows a Visual Studio modal dialog with a vertical ComboBox and switches
    /// to the chosen one. Mirrors the <c>pipelinesexplorer.selectTenant</c>
    /// command in the VS Code client.
    /// </summary>
    private async Task SelectTenantAsync(CancellationToken cancellationToken)
    {
        var ext = _extensibilityProvider();
        if (ext is null)
        {
            _logger.Warn("SelectTenantCommand: extensibility unavailable");
            return;
        }

        try
        {
            IsBusy = true;
            // Cached on first fetch -> instantaneous on subsequent invocations.
            var tenants = await _auth.ListAvailableTenantsAsync(cancellationToken).ConfigureAwait(false);
            if (tenants.Count == 0)
            {
                await ext.Shell().ShowPromptAsync(
                    Strings.TenantPicker_NoTenants,
                    PromptOptions.OK,
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            var sorted = tenants
                .OrderBy(t => t.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var current = _auth.GetStoredTenant();
            var dialogVm = new TenantPickerDialogViewModel(sorted, current);
            using var dialog = new ToolWindows.TenantPickerDialog(dialogVm);

            var result = await ext.Shell().ShowDialogAsync(
                dialog,
                Strings.TenantPicker_Title,
                Microsoft.VisualStudio.RpcContracts.Notifications.DialogOption.OKCancel,
                cancellationToken).ConfigureAwait(false);
            if (result != Microsoft.VisualStudio.RpcContracts.Notifications.DialogResult.OK)
            {
                return;
            }

            var picked = dialogVm.SelectedChoice;
            if (picked is null)
            {
                return;
            }

            if (string.IsNullOrEmpty(picked.TenantId))
            {
                await _auth.SwitchTenantAsync(null, null, cancellationToken).ConfigureAwait(false);
            }
            else if (!string.Equals(picked.TenantId, current, StringComparison.OrdinalIgnoreCase))
            {
                await _auth.SwitchTenantAsync(picked.TenantId, picked.Title, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Select tenant failed", ex);
            SetError(ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RefreshFireAndForget()
    {
        _ = Task.Run(async () =>
        {
            try { await RefreshAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger.Error("Background refresh failed", ex); }
        });
    }

    private static void ReplaceList(ObservableList<TreeNodeViewModel> target, IList<TreeNodeViewModel> values)
    {
        target.Clear();
        if (values.Count > 0)
        {
            target.AddRange(values);
        }
    }

    private void SetError(string? error)
    {
        ErrorMessage = error;
    }

    private static IEnumerable<List<T>> Chunk<T>(IEnumerable<T> source, int size)
    {
        var bucket = new List<T>(size);
        foreach (var item in source)
        {
            bucket.Add(item);
            if (bucket.Count == size)
            {
                yield return bucket;
                bucket = new List<T>(size);
            }
        }
        if (bucket.Count > 0)
        {
            yield return bucket;
        }
    }

    // -------- Pipeline / template / script wiring (Phase 4) --------

    private PipelineNode BuildPipelineNode(
        AdoOrganization org, AdoProject project, AdoPipeline pipe, AdoPipelineDetail? detail, string repoKey)
    {
        var node = new PipelineNode(org, project, pipe, detail, repoKey);
        var rootPath = detail?.Configuration?.Path;
        if (!string.IsNullOrEmpty(rootPath))
        {
            node.OpenCommand = new AsyncCommand((_, _, ct) => OpenPipelineYamlAsync(node, ct));
        }
        node.Children.Add(new InfoNode(Strings.Tree_Loading, TreeNodeKind.Loading));
        SubscribeLazyLoad(node, () => LoadPipelineAnalysisAsync(node));
        return node;
    }

    private async Task LoadPipelineAnalysisAsync(PipelineNode node)
    {
        try
        {
            var branch = _branches.Get(new RepoLinkKey(node.Organization.AccountId, node.Project.Id, node.RepoKey));
            var analysis = await _analyzer.AnalyzeAsync(
                node.Organization.AccountName,
                node.Project.Name,
                node.Pipeline.Id,
                node.Detail,
                branch).ConfigureAwait(false);
            BuildAnalysisChildren(node, analysis, node.RepoKey, node.RepoId, node.YamlDir, node.Detail?.Configuration?.Path);
        }
        catch (Exception ex)
        {
            _logger.Error($"Loading analysis of pipeline {node.Pipeline.Name} failed", ex);
            ReplaceList(node.Children, new TreeNodeViewModel[] { new InfoNode(ex.Message, TreeNodeKind.Error) });
        }
    }

    private async Task LoadTemplateAnalysisAsync(TemplateNode node)
    {
        if (!node.IsSameRepoExpandable) { ReplaceList(node.Children, System.Array.Empty<TreeNodeViewModel>()); return; }
        try
        {
            var branch = _branches.Get(new RepoLinkKey(node.Organization.AccountId, node.Project.Id, node.PipelineRepoKey));
            var analysis = await _analyzer.AnalyzeFileAsync(
                node.Organization.AccountName,
                node.Project.Name,
                node.ContainingRepoId!,
                node.ResolvedPath,
                branch).ConfigureAwait(false);
            BuildAnalysisChildren(node, analysis, node.PipelineRepoKey, node.ContainingRepoId, node.ResolvedDir, node.ResolvedPath);
        }
        catch (Exception ex)
        {
            _logger.Error($"Loading analysis of template {node.Reference.Raw} failed", ex);
            ReplaceList(node.Children, new TreeNodeViewModel[] { new InfoNode(ex.Message, TreeNodeKind.Error) });
        }
    }

    private void BuildAnalysisChildren(
        TreeNodeViewModel parent,
        PipelineAnalysis analysis,
        string pipelineRepoKey,
        string? containingRepoId,
        string baseDir,
        string? containingYamlPath)
    {
        var children = new List<TreeNodeViewModel>();
        if (!string.IsNullOrEmpty(analysis.Warning))
        {
            children.Add(new InfoNode(analysis.Warning!, TreeNodeKind.Info));
        }

        if (analysis.Templates.Count == 0 && analysis.Scripts.Count == 0 && string.IsNullOrEmpty(analysis.Warning))
        {
            children.Add(new InfoNode(Strings.Tree_NoTemplatesOrScripts, TreeNodeKind.Info));
        }

        var org = parent switch
        {
            PipelineNode pn => pn.Organization,
            TemplateNode tn => tn.Organization,
            _ => null,
        };
        var project = parent switch
        {
            PipelineNode pn => pn.Project,
            TemplateNode tn => tn.Project,
            _ => null,
        };

        if (analysis.Templates.Count > 0 && org is not null && project is not null)
        {
            var group = new GroupNode(GroupKind.Templates, analysis.Templates.Count);
            foreach (var t in analysis.Templates)
            {
                group.Children.Add(BuildTemplateNode(t, org, project, pipelineRepoKey, containingRepoId, baseDir));
            }
            children.Add(group);
        }
        if (analysis.Scripts.Count > 0 && org is not null && project is not null)
        {
            var group = new GroupNode(GroupKind.Scripts, analysis.Scripts.Count);
            foreach (var s in analysis.Scripts)
            {
                group.Children.Add(BuildScriptNode(s, org, project, pipelineRepoKey, baseDir, containingYamlPath));
            }
            children.Add(group);
        }

        ReplaceList(parent.Children, children);
    }

    private TemplateNode BuildTemplateNode(
        TemplateRef reference, AdoOrganization org, AdoProject project,
        string pipelineRepoKey, string? containingRepoId, string containingDir)
    {
        var node = new TemplateNode(reference, org, project, pipelineRepoKey, containingRepoId, containingDir);
        node.OpenCommand = new AsyncCommand((_, _, ct) => OpenTemplateAsync(node, ct));

        if (node.IsSameRepoExpandable)
        {
            node.Children.Add(new InfoNode(Strings.Tree_Loading, TreeNodeKind.Loading));
            SubscribeLazyLoad(node, () => LoadTemplateAnalysisAsync(node));
        }
        return node;
    }

    /// <summary>
    /// Hook a one-shot async loader to the first expansion of <paramref name="node"/>.
    /// Avoids <c>async void</c> event-handler lambdas (VSTHRD101).
    /// </summary>
    private void SubscribeLazyLoad(TreeNodeViewModel node, Func<Task> loader)
    {
        node.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(TreeNodeViewModel.IsExpanded) || !node.IsExpanded) { return; }
            if (node.Children.Count != 1 || node.Children[0].Kind != TreeNodeKind.Loading) { return; }
            _ = Task.Run(async () =>
            {
                try { await loader().ConfigureAwait(false); }
                catch (Exception ex) { _logger.Error("Lazy load failed", ex); }
            });
        };
    }

    private ScriptNode BuildScriptNode(
        ScriptRef reference, AdoOrganization org, AdoProject project,
        string pipelineRepoKey, string baseDir, string? containingYamlPath)
    {
        var node = new ScriptNode(reference);
        if (!string.IsNullOrEmpty(reference.FilePath))
        {
            var linkKey = new RepoLinkKey(org.AccountId, project.Id, pipelineRepoKey);
            var branch = _branches.Get(linkKey);
            // Match the VS Code client: pass the script's filePath verbatim. Script
            // task `filePath`/`scriptPath` inputs in Azure Pipelines are repo-root
            // relative (often using $(System.DefaultWorkingDirectory)/...), NOT
            // relative to the YAML file that declares the task. OpenItemService
            // strips the pipeline variables and resolves against the linked clone.
            var target = new OpenTarget
            {
                RepoLinkKey = linkKey,
                RelativePath = reference.FilePath!,
                DisplayName = node.Label,
                Branch = branch,
            };
            node.OpenCommand = new AsyncCommand((_, _, ct) => _openItem.OpenAsync(target, ct));
        }
        else if (reference.Inline && reference.Line is int line && line > 0 && !string.IsNullOrEmpty(containingYamlPath))
        {
            // Inline scripts have no addressable file of their own — "Open" jumps to
            // the line in the YAML that defines the inline `script:` / `inlineScript:`
            // block. Mirrors the VS Code "Open Inline Script Location" command.
            var linkKey = new RepoLinkKey(org.AccountId, project.Id, pipelineRepoKey);
            var branch = _branches.Get(linkKey);
            var target = new OpenTarget
            {
                RepoLinkKey = linkKey,
                RelativePath = containingYamlPath!,
                DisplayName = node.Label,
                Branch = branch,
                SelectionLine = line,
            };
            node.OpenCommand = new AsyncCommand((_, _, ct) => _openItem.OpenAsync(target, ct));
        }
        return node;
    }

    private Task OpenPipelineYamlAsync(PipelineNode node, CancellationToken ct)
    {
        var yamlPath = node.Detail?.Configuration?.Path;
        if (string.IsNullOrEmpty(yamlPath)) { return Task.CompletedTask; }
        var linkKey = new RepoLinkKey(node.Organization.AccountId, node.Project.Id, node.RepoKey);
        var branch = _branches.Get(linkKey);
        var target = new OpenTarget
        {
            RepoLinkKey = linkKey,
            RelativePath = yamlPath!,
            DisplayName = node.Pipeline.Name,
            Branch = branch,
        };
        return _openItem.OpenAsync(target, ct);
    }

    private Task OpenTemplateAsync(TemplateNode node, CancellationToken ct)
    {
        var linkKey = new RepoLinkKey(node.Organization.AccountId, node.Project.Id, node.PipelineRepoKey);
        var branch = _branches.Get(linkKey);
        var target = new OpenTarget
        {
            RepoLinkKey = linkKey,
            RelativePath = node.IsSameRepoExpandable ? node.ResolvedPath : node.Reference.Path,
            RepositoryAlias = node.Reference.Repository,
            DisplayName = node.Label,
            Branch = branch,
        };
        return _openItem.OpenAsync(target, ct);
    }

    private void WireRepositoryCommands(RepositoryNode node)
    {
        node.LinkCommand = new AsyncCommand(async (_, _, ct) =>
        {
            try
            {
                var changed = await _openItem.PickAndLinkAsync(node.LinkKey, node.RepoLabel, ct).ConfigureAwait(false);
                if (changed)
                {
                    node.UpdateState(_links.Get(node.LinkKey), _branches.Get(node.LinkKey));
                    await OfferDetectedBranchAsync(node, ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) { _logger.Error("Link workspace failed", ex); SetError(ex.Message); }
        });
        node.UnlinkCommand = new AsyncCommand((_, _, _) =>
        {
            try
            {
                _links.Remove(node.LinkKey);
                node.UpdateState(null, _branches.Get(node.LinkKey));
            }
            catch (Exception ex) { _logger.Error("Unlink workspace failed", ex); SetError(ex.Message); }
            return Task.CompletedTask;
        });
        node.SelectBranchCommand = new AsyncCommand((_, _, ct) => SelectBranchAsync(node, ct));
    }

    private async Task SelectBranchAsync(RepositoryNode node, CancellationToken ct)
    {
        var ext = _extensibilityProvider();
        if (ext is null) { return; }
        try
        {
            var branches = await _ado.ListBranchesAsync(node.Organization.AccountName, node.Project.Name, node.RepoKey, ct).ConfigureAwait(false);
            if (branches.Count == 0)
            {
                await ext.Shell().ShowPromptAsync(Strings.BranchPicker_NoBranches, PromptOptions.OK, ct).ConfigureAwait(false);
                return;
            }

            var current = _branches.Get(node.LinkKey);
            var dialogVm = new BranchPickerDialogViewModel(node.RepoLabel, branches, current);
            using var dialog = new ToolWindows.BranchPickerDialog(dialogVm);

            var result = await ext.Shell().ShowDialogAsync(
                dialog,
                Strings.BranchPicker_Title,
                Microsoft.VisualStudio.RpcContracts.Notifications.DialogOption.OKCancel,
                ct).ConfigureAwait(false);
            if (result != Microsoft.VisualStudio.RpcContracts.Notifications.DialogResult.OK)
            {
                return;
            }

            var picked = dialogVm.SelectedChoice;
            if (picked is null) { return; }

            if (string.IsNullOrEmpty(picked.Name)) { _branches.Clear(node.LinkKey); }
            else { _branches.Set(node.LinkKey, picked.Name); }
            node.UpdateState(_links.Get(node.LinkKey), _branches.Get(node.LinkKey));
        }
        catch (Exception ex)
        {
            _logger.Error("Select branch failed", ex);
            await ext.Shell().ShowPromptAsync(string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.BranchPicker_Failed_Format, ex.Message), PromptOptions.OK, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// React to an Azure DevOps 401/403. Wipe the cached credentials (so the
    /// next ADO call doesn't keep retrying with the same broken token) and
    /// surface a modal prompt that lets the user pick a sign-in method. The
    /// <see cref="_unauthorizedHandled"/> flag ensures we only show the
    /// prompt once per broken session — subsequent failures stay silent
    /// (the error message is still rendered in the tree) until the next
    /// successful sign-in. Mirrors <c>PipelinesTreeProvider.handleUnauthorized</c>
    /// in the VS Code client.
    /// </summary>
    private async Task HandleUnauthorizedAsync(AdoUnauthorizedException ex, CancellationToken ct)
    {
        if (_unauthorizedHandled) { return; }
        _unauthorizedHandled = true;

        try
        {
            await _auth.ResetAsync(ct).ConfigureAwait(false);
        }
        catch (Exception resetEx)
        {
            _logger.Warn($"Auth reset after 401 failed: {resetEx.Message}");
        }

        var extensibility = _extensibilityProvider();
        if (extensibility is null) { return; }

        var msg = string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.Unauthorized_Message_Format, ex.Message);
        var options = new PromptOptions<int> { DismissedReturns = -1, DefaultChoiceIndex = 0 };
        options.Choices.Add(Strings.Unauthorized_SignInMicrosoft, 0);
        options.Choices.Add(Strings.Unauthorized_SignInPat, 1);

        int pick;
        try
        {
            pick = await extensibility.Shell().ShowPromptAsync(msg, options, ct).ConfigureAwait(false);
        }
        catch (Exception promptEx)
        {
            _logger.Warn($"Unauthorized prompt failed: {promptEx.Message}");
            return;
        }

        try
        {
            if (pick == 0)
            {
                await _auth.SignInWithMicrosoftAsync(ct).ConfigureAwait(false);
            }
            else if (pick == 1)
            {
                // The PAT sign-in needs the secret value; mirror the welcome
                // panel by surfacing the PAT input back to the user. Clearing
                // IsSignedIn (already done by ResetAsync) reveals the welcome
                // view automatically so the user can paste a new PAT.
                _logger.Info("User selected Sign in with PAT after unauthorized error.");
            }
        }
        catch (OperationCanceledException) { /* user cancelled */ }
        catch (Exception signInEx)
        {
            _logger.Warn($"Recovery sign-in failed: {signInEx.Message}");
            SetError(signInEx.Message);
        }
    }

    /// <summary>
    /// After a successful workspace link, peek at the local clone's
    /// <c>.git/HEAD</c> and offer to use the checked-out branch as the
    /// branch override for this repo. Mirrors the VS Code client.
    /// </summary>
    private async Task OfferDetectedBranchAsync(RepositoryNode node, CancellationToken ct)
    {
        var fsPath = _links.Get(node.LinkKey);
        if (string.IsNullOrEmpty(fsPath)) { return; }
        var detected = await DetectLocalBranchAsync(fsPath!).ConfigureAwait(false);
        if (string.IsNullOrEmpty(detected)) { return; }

        var current = _branches.Get(node.LinkKey);
        if (string.Equals(detected, current, StringComparison.Ordinal)) { return; }

        var ext = _extensibilityProvider();
        if (ext is null) { return; }

        var msg = string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            Strings.LinkWorkspace_DetectedBranch_Format,
            node.RepoLabel,
            detected);
        var options = new PromptOptions<int> { DismissedReturns = -1, DefaultChoiceIndex = 0 };
        options.Choices.Add(Strings.LinkWorkspace_UseThisBranch, 0);
        options.Choices.Add(Strings.LinkWorkspace_KeepDefaultBranch, 1);
        var pick = await ext.Shell().ShowPromptAsync(msg, options, ct).ConfigureAwait(false);
        if (pick == 0)
        {
            _branches.Set(node.LinkKey, detected!);
            node.UpdateState(_links.Get(node.LinkKey), _branches.Get(node.LinkKey));
        }
    }

    // -------- Filter (Plan 001) --------

    /// <summary>
    /// Restart the debounce timer after a <see cref="FilterText"/> change.
    /// Cancels any in-flight scan and, if the term becomes empty, clears the
    /// filter immediately (no need to wait 200ms for that case).
    /// </summary>
    private void ScheduleFilterScan(string rawTerm)
    {
        _filterDebounceCts?.Cancel();
        _filterScanCts?.Cancel();

        var normalized = NormalizeFilterTerm(rawTerm);
        if (normalized is null)
        {
            ClearFilterInternal();
            return;
        }

        var cts = new CancellationTokenSource();
        _filterDebounceCts = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200), cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            if (cts.IsCancellationRequested) { return; }
            _filterDebounceCts = null;

            var scanCts = new CancellationTokenSource();
            _filterScanCts = scanCts;
            try
            {
                await RunFilterScanAsync(normalized, scanCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.Error("Filter scan failed", ex);
            }
        });
    }

    private void ClearFilterInternal()
    {
        _activeFilterTerm = null;
        _matchedNodes.Clear();
        _ancestorNodes.Clear();
        RestoreFilterExpansionState();
        SetAllVisible();
        IsFilterActive = false;
        FilterStatusText = string.Empty;
    }

    /// <summary>
    /// Case-insensitive substring match. Empty / whitespace-only inputs
    /// collapse to <c>null</c> so callers can treat "no filter" uniformly.
    /// </summary>
    private static string? NormalizeFilterTerm(string? term)
    {
        if (string.IsNullOrWhiteSpace(term)) { return null; }
        return term.Trim().ToLowerInvariant();
    }

    private static bool ContainsCI(string? haystack, string needle) =>
        !string.IsNullOrEmpty(haystack)
        && haystack!.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

    private static string BaseNameOf(string path)
    {
        if (string.IsNullOrEmpty(path)) { return string.Empty; }
        var clean = path.Replace('\\', '/').TrimEnd('/');
        var i = clean.LastIndexOf('/');
        return i >= 0 ? clean.Substring(i + 1) : clean;
    }

    /// <summary>
    /// Forces every organization → project subtree reachable from
    /// <see cref="Roots"/> to materialise (bypassing the lazy-load-on-expand
    /// gate), so <see cref="CollectLoadedPipelines"/> sees every pipeline the
    /// filter is expected to search. Per-node failures are logged but do not
    /// abort the whole preload.
    /// </summary>
    private async Task PreloadAllForFilterAsync(CancellationToken ct)
    {
        var orgs = Roots.OfType<OrganizationNode>().ToList();
        foreach (var batch in Chunk(orgs, 4))
        {
            if (ct.IsCancellationRequested) { return; }
            await Task.WhenAll(batch.Select(o => EnsureLoadedForFilterAsync(o))).ConfigureAwait(false);
        }
        if (ct.IsCancellationRequested) { return; }

        var projects = orgs.SelectMany(o => o.Children.OfType<ProjectNode>()).ToList();
        foreach (var batch in Chunk(projects, 4))
        {
            if (ct.IsCancellationRequested) { return; }
            await Task.WhenAll(batch.Select(p => EnsureLoadedForFilterAsync(p))).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// If <paramref name="node"/> is still a lazy stub (single
    /// <see cref="TreeNodeKind.Loading"/> child), invoke the matching loader
    /// synchronously. Mirrors <see cref="SubscribeLazyLoad"/>, but callable
    /// from the filter preload without waiting for user expansion.
    /// </summary>
    private async Task EnsureLoadedForFilterAsync(TreeNodeViewModel node)
    {
        if (node.Children.Count != 1 || node.Children[0].Kind != TreeNodeKind.Loading) { return; }
        try
        {
            switch (node)
            {
                case OrganizationNode org: await LoadProjectsAsync(org).ConfigureAwait(false); break;
                case ProjectNode proj: await LoadPipelinesAsync(proj).ConfigureAwait(false); break;
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"Filter preload: loading '{node.Label}' failed", ex);
        }
    }

    /// <summary>
    /// Walks every organization / project / repository the signed-in identity
    /// can see (auto-loading lazy subtrees on the fly) and marks every
    /// pipeline / template / script whose name contains
    /// <paramref name="term"/>. Ancestors of matches are marked visible so
    /// they still render as breadcrumbs.
    /// </summary>
    private async Task RunFilterScanAsync(string term, CancellationToken ct)
    {
        _activeFilterTerm = term;
        _matchedNodes.Clear();
        _ancestorNodes.Clear();

        IsFilterActive = true;
        FilterStatusText = string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.Filter_Status_Scanning_Format, term);

        await PreloadAllForFilterAsync(ct).ConfigureAwait(false);
        if (ct.IsCancellationRequested) { return; }

        var pipelines = CollectLoadedPipelines();
        var cap = FilterPipelineCap;
        var capped = pipelines.Count > cap;
        var targets = capped ? pipelines.Take(cap).ToList() : pipelines;

        // Pass 1: cheap, synchronous — pipeline names and root YAML basename.
        foreach (var pipe in targets)
        {
            if (ct.IsCancellationRequested) { return; }
            if (PipelineMatchesFilter(pipe, term))
            {
                _matchedNodes.Add(pipe);
                MarkAncestorsVisible(pipe);
            }
        }

        // Pass 2: analyse YAML for templates + scripts. Cap concurrency at 8
        // (same as the pipeline list loader).
        foreach (var batch in Chunk(targets, 8))
        {
            if (ct.IsCancellationRequested) { return; }
            var tasks = batch.Select(async pipe =>
            {
                try
                {
                    var branch = _branches.Get(new RepoLinkKey(pipe.Organization.AccountId, pipe.Project.Id, pipe.RepoKey));
                    var analysis = await _analyzer.AnalyzeAsync(
                        pipe.Organization.AccountName,
                        pipe.Project.Name,
                        pipe.Pipeline.Id,
                        pipe.Detail,
                        branch).ConfigureAwait(false);
                    return (pipe, analysis);
                }
                catch (Exception ex)
                {
                    _logger.Warn($"Filter scan analyze failed for {pipe.Pipeline.Name}: {ex.Message}");
                    return (pipe, (PipelineAnalysis?)null);
                }
            });
            var results = await Task.WhenAll(tasks).ConfigureAwait(false);
            if (ct.IsCancellationRequested) { return; }

            foreach (var (pipe, analysis) in results)
            {
                if (analysis is null) { continue; }
                var pipelineMatched = false;

                foreach (var t in analysis.Templates)
                {
                    if (ContainsCI(BaseNameOf(t.Path), term))
                    {
                        MarkTemplateOrScriptMatch(pipe, GroupKind.Templates);
                        pipelineMatched = true;
                    }
                }
                foreach (var s in analysis.Scripts)
                {
                    if (s.FilePath is not null && ContainsCI(BaseNameOf(s.FilePath), term))
                    {
                        MarkTemplateOrScriptMatch(pipe, GroupKind.Scripts);
                        pipelineMatched = true;
                    }
                }

                // Recurse into same-repo nested templates. Cross-repo aliases
                // are skipped (the analyzer can't resolve a repository alias).
                // Depth capped by FilterMaxTemplateDepth and cycles guarded by
                // the visited-set. Any match found deep in the tree surfaces
                // by marking the top-level Templates group + the pipeline as
                // ancestor-visible; the leaves themselves become visible on
                // demand because materialised child nodes default to visible.
                var repoId = pipe.Detail?.Configuration?.Repository?.Id;
                if (!string.IsNullOrEmpty(repoId))
                {
                    var branch = _branches.Get(new RepoLinkKey(pipe.Organization.AccountId, pipe.Project.Id, pipe.RepoKey));
                    if (await ScanTemplatesRecursivelyAsync(pipe, analysis, repoId!, branch, term, ct).ConfigureAwait(false))
                    {
                        pipelineMatched = true;
                    }
                }

                if (pipelineMatched && !_matchedNodes.Contains(pipe))
                {
                    // A template/script inside this pipeline matched. Count the
                    // pipeline itself as a match so the status line agrees with
                    // what the tree is showing (Pass 1 only matches pipeline
                    // names, so without this the status stays at "no results"
                    // for pure YAML-content matches).
                    _matchedNodes.Add(pipe);
                    MarkAncestorsVisible(pipe);
                }
            }
        }

        if (ct.IsCancellationRequested) { return; }

        ApplyVisibilityFromMarks();
        AutoExpandVisibleAncestors();

        var matchCount = _matchedNodes.Count;
        if (matchCount == 0)
        {
            FilterStatusText = string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.Filter_Status_NoResults_Format, term);
        }
        else if (capped)
        {
            FilterStatusText = string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.Filter_Status_Capped_Format, term, matchCount, cap);
        }
        else
        {
            FilterStatusText = string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.Filter_Status_Ready_Format, term, matchCount);
        }
    }

    private static bool PipelineMatchesFilter(PipelineNode node, string term)
    {
        if (ContainsCI(node.Pipeline.Name, term)) { return true; }
        var rootPath = node.Detail?.Configuration?.Path;
        if (!string.IsNullOrEmpty(rootPath) && ContainsCI(BaseNameOf(rootPath!), term)) { return true; }
        return false;
    }

    private void MarkTemplateOrScriptMatch(PipelineNode pipe, GroupKind group)
    {
        // We match at leaf granularity in Pass 2, but the leaf nodes might not
        // even be materialised yet (the pipeline may never have been expanded).
        // We record the pipeline as an ancestor-visible node and mark the
        // group container so that if/when the user expands the pipeline they
        // see the right group. Leaves under the group will be surfaced on
        // expansion via the same term applied by ApplyVisibilityFromMarks.
        var groupNode = pipe.Children.OfType<GroupNode>().FirstOrDefault(g => g.Group == group);
        if (groupNode is not null)
        {
            _ancestorNodes.Add(groupNode);
        }
    }

    /// <summary>
    /// Walks the reachable template graph of <paramref name="rootAnalysis"/>
    /// following same-repo <c>template:</c> references, and reports whether
    /// any nested template basename or script filename matched the term.
    /// Depth is capped by <see cref="FilterMaxTemplateDepth"/> and cycles are
    /// guarded by an in-memory visited set. Cross-repo aliases are skipped.
    /// </summary>
    private async Task<bool> ScanTemplatesRecursivelyAsync(
        PipelineNode pipe,
        PipelineAnalysis rootAnalysis,
        string repoId,
        string? branch,
        string term,
        CancellationToken ct)
    {
        var rootDir = pipe.YamlDir;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(IReadOnlyList<TemplateRef> Templates, string ContainingDir, int Depth)>();
        queue.Enqueue((rootAnalysis.Templates, rootDir, 0));
        bool anyNestedMatch = false;

        while (queue.Count > 0)
        {
            if (ct.IsCancellationRequested) { return anyNestedMatch; }
            var (tpls, containingDir, depth) = queue.Dequeue();
            foreach (var t in tpls)
            {
                if (ct.IsCancellationRequested) { return anyNestedMatch; }
                // Skip cross-repo references — the analyzer can't resolve
                // a repository alias to an id.
                if (!string.IsNullOrEmpty(t.Repository)) { continue; }
                if (depth >= FilterMaxTemplateDepth) { continue; }

                var resolved = ResolveRepoPath(containingDir, t.Path);
                var key = $"{resolved}@{branch}";
                if (!visited.Add(key)) { continue; }

                PipelineAnalysis sub;
                try
                {
                    sub = await _analyzer.AnalyzeFileAsync(
                        pipe.Organization.AccountName,
                        pipe.Project.Name,
                        repoId,
                        resolved,
                        branch,
                        ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.Warn($"Filter scan analyze-file failed for {resolved}: {ex.Message}");
                    continue;
                }

                foreach (var st in sub.Templates)
                {
                    if (ContainsCI(BaseNameOf(st.Path), term))
                    {
                        MarkTemplateOrScriptMatch(pipe, GroupKind.Templates);
                        anyNestedMatch = true;
                    }
                }
                foreach (var s in sub.Scripts)
                {
                    if (s.FilePath is not null && ContainsCI(BaseNameOf(s.FilePath), term))
                    {
                        MarkTemplateOrScriptMatch(pipe, GroupKind.Templates);
                        anyNestedMatch = true;
                    }
                }

                if (sub.Templates.Count > 0)
                {
                    queue.Enqueue((sub.Templates, DirOfRepoPath(resolved), depth + 1));
                }
            }
        }

        return anyNestedMatch;
    }

    private static string DirOfRepoPath(string p)
    {
        if (string.IsNullOrEmpty(p)) { return string.Empty; }
        var norm = p.Replace('\\', '/');
        var idx = norm.LastIndexOf('/');
        return idx <= 0 ? string.Empty : norm[..idx];
    }

    /// <summary>
    /// Walk the loaded tree once and set <c>IsVisibleUnderFilter</c> on every
    /// node based on the marked sets. Non-matching leaves collapse away.
    /// </summary>
    private void ApplyVisibilityFromMarks()
    {
        var term = _activeFilterTerm;
        if (term is null) { SetAllVisible(); return; }

        foreach (var root in Roots)
        {
            ApplyVisibilityRecursive(root, term);
        }
    }

    private bool ApplyVisibilityRecursive(TreeNodeViewModel node, string term)
    {
        bool selfMatched = _matchedNodes.Contains(node) || NodeLeafMatches(node, term);
        bool anyChildVisible = false;
        foreach (var child in node.Children)
        {
            if (ApplyVisibilityRecursive(child, term)) { anyChildVisible = true; }
        }
        bool visible = selfMatched || anyChildVisible || _ancestorNodes.Contains(node);
        node.IsVisibleUnderFilter = visible;
        if (selfMatched) { _matchedNodes.Add(node); }
        return visible;
    }

    /// <summary>
    /// True when a template / script leaf's basename contains the filter
    /// term. Pipelines/orgs/etc. are matched separately in Pass 1.
    /// </summary>
    private static bool NodeLeafMatches(TreeNodeViewModel node, string term) => node switch
    {
        TemplateNode t => ContainsCI(BaseNameOf(t.Reference.Path), term),
        ScriptNode s => s.Reference.FilePath is not null && ContainsCI(BaseNameOf(s.Reference.FilePath), term),
        _ => false,
    };

    private void MarkAncestorsVisible(TreeNodeViewModel node)
    {
        var parent = FindParent(node);
        while (parent is not null)
        {
            _ancestorNodes.Add(parent);
            parent = FindParent(parent);
        }
    }

    /// <summary>
    /// Reverse lookup of a node's parent. Not tracked at add time — we simply
    /// walk <see cref="Roots"/> once per lookup. Cost is O(loaded-tree) but
    /// the whole scan already walks the tree, so this stays cheap in practice.
    /// </summary>
    private TreeNodeViewModel? FindParent(TreeNodeViewModel target)
    {
        foreach (var root in Roots)
        {
            var p = FindParentRecursive(root, target);
            if (p is not null) { return p; }
        }
        return null;
    }

    private static TreeNodeViewModel? FindParentRecursive(TreeNodeViewModel node, TreeNodeViewModel target)
    {
        foreach (var c in node.Children)
        {
            if (ReferenceEquals(c, target)) { return node; }
            var deeper = FindParentRecursive(c, target);
            if (deeper is not null) { return deeper; }
        }
        return null;
    }

    /// <summary>
    /// Collect every loaded <see cref="PipelineNode"/> reachable from
    /// <see cref="Roots"/>. Skips branches that are still just a
    /// <see cref="TreeNodeKind.Loading"/> placeholder so the filter never
    /// forces a network fetch on the user's behalf.
    /// </summary>
    private List<PipelineNode> CollectLoadedPipelines()
    {
        var out_ = new List<PipelineNode>();
        foreach (var root in Roots)
        {
            CollectLoadedPipelinesRecursive(root, out_);
        }
        return out_;
    }

    private static void CollectLoadedPipelinesRecursive(TreeNodeViewModel node, List<PipelineNode> acc)
    {
        if (node is PipelineNode pn) { acc.Add(pn); return; }
        if (node.Children.Count == 1 && node.Children[0].Kind == TreeNodeKind.Loading) { return; }
        foreach (var c in node.Children)
        {
            CollectLoadedPipelinesRecursive(c, acc);
        }
    }

    private void SetAllVisible()
    {
        foreach (var root in Roots) { SetAllVisibleRecursive(root); }
    }

    private static void SetAllVisibleRecursive(TreeNodeViewModel node)
    {
        node.IsVisibleUnderFilter = true;
        foreach (var c in node.Children) { SetAllVisibleRecursive(c); }
    }

    /// <summary>
    /// For every node that is currently visible-as-ancestor (i.e. an org /
    /// project / repository above a match) remember its expansion state and
    /// force it open so the match is reachable without hand-expanding.
    /// </summary>
    private void AutoExpandVisibleAncestors()
    {
        foreach (var root in Roots) { AutoExpandRecursive(root); }
    }

    private void AutoExpandRecursive(TreeNodeViewModel node)
    {
        // Only auto-expand containers of matches — never the matched leaves.
        if (_ancestorNodes.Contains(node) && node.Kind != TreeNodeKind.Pipeline && node.Kind != TreeNodeKind.Template && node.Kind != TreeNodeKind.Script)
        {
            if (!node.IsAutoExpandedByFilter)
            {
                node.ExpansionSnapshot = node.IsExpanded;
                node.IsAutoExpandedByFilter = true;
            }
            node.IsExpanded = true;
        }
        foreach (var c in node.Children) { AutoExpandRecursive(c); }
    }

    private void RestoreFilterExpansionState()
    {
        foreach (var root in Roots) { RestoreExpansionRecursive(root); }
    }

    private static void RestoreExpansionRecursive(TreeNodeViewModel node)
    {
        if (node.IsAutoExpandedByFilter)
        {
            node.IsExpanded = node.ExpansionSnapshot;
            node.IsAutoExpandedByFilter = false;
        }
        foreach (var c in node.Children) { RestoreExpansionRecursive(c); }
    }

    /// <summary>
    /// Best-effort detection of the current branch of a local Git working
    /// copy by reading <c>.git/HEAD</c>. Handles the worktree indirection
    /// where <c>.git</c> is a file containing <c>gitdir: &lt;path&gt;</c>.
    /// Returns <c>null</c> if the folder is not a Git working copy or the
    /// HEAD is detached.
    /// </summary>
    private static async Task<string?> DetectLocalBranchAsync(string folderPath)
    {
        try
        {
            var gitEntry = System.IO.Path.Combine(folderPath, ".git");
            string gitDir = gitEntry;
            if (System.IO.File.Exists(gitEntry))
            {
                var content = await System.IO.File.ReadAllTextAsync(gitEntry).ConfigureAwait(false);
                var m = System.Text.RegularExpressions.Regex.Match(
                    content,
                    @"^gitdir:\s*(.+?)\s*$",
                    System.Text.RegularExpressions.RegexOptions.Multiline);
                if (!m.Success) { return null; }
                var target = m.Groups[1].Value.Trim();
                gitDir = System.IO.Path.IsPathRooted(target)
                    ? target
                    : System.IO.Path.GetFullPath(System.IO.Path.Combine(folderPath, target));
            }
            else if (!System.IO.Directory.Exists(gitDir))
            {
                return null;
            }

            var headPath = System.IO.Path.Combine(gitDir, "HEAD");
            if (!System.IO.File.Exists(headPath)) { return null; }
            var head = (await System.IO.File.ReadAllTextAsync(headPath).ConfigureAwait(false)).Trim();
            var refMatch = System.Text.RegularExpressions.Regex.Match(head, @"^ref:\s*refs/heads/(.+)$");
            return refMatch.Success ? refMatch.Groups[1].Value.Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Resolve a YAML-style path reference against the directory containing the
    /// YAML file it appeared in. Recognises Azure Pipelines repo-root variables
    /// (<c>$(System.DefaultWorkingDirectory)</c>, <c>$(Build.SourcesDirectory)</c>,
    /// <c>$(Pipeline.Workspace)</c>, <c>$(Agent.BuildDirectory)</c>): when the
    /// cleaned reference starts with one of those, the path is treated as
    /// repo-absolute and <paramref name="baseDir"/> is NOT prepended.
    /// </summary>
    private static string ResolveRepoPath(string baseDir, string reference)
    {
        var cleaned = reference.Replace('\\', '/').Trim();

        // Strip leading Azure Pipelines variables that always resolve to the repo
        // root. After stripping, treat the remainder as repo-absolute.
        var stripped = System.Text.RegularExpressions.Regex.Replace(
            cleaned,
            @"^\s*\$\(\s*(System\.DefaultWorkingDirectory|Build\.SourcesDirectory|Pipeline\.Workspace|Agent\.BuildDirectory)\s*\)/?",
            string.Empty,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var anchoredAtRoot = stripped.Length != cleaned.Length;

        var combined = (anchoredAtRoot || stripped.StartsWith('/'))
            ? stripped
            : $"{baseDir}/{stripped}";

        var parts = combined.Split('/', System.StringSplitOptions.RemoveEmptyEntries);
        var stack = new List<string>();
        foreach (var seg in parts)
        {
            if (seg == ".") { continue; }
            if (seg == "..") { if (stack.Count > 0) { stack.RemoveAt(stack.Count - 1); } continue; }
            stack.Add(seg);
        }
        return "/" + string.Join('/', stack);
    }
}

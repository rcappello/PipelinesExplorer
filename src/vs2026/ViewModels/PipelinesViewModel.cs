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

        _auth.SessionChanged += (_, s) => OnSessionChanged(s);
        _links.Changed += (_, _) => RefreshFireAndForget();
        _branches.Changed += (_, _) => RefreshFireAndForget();

        OnSessionChanged(_auth.Session);
    }

    [DataMember]
    public ObservableList<TreeNodeViewModel> Roots { get; }

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
            BuildAnalysisChildren(node, analysis, node.RepoKey, node.RepoId, node.YamlDir);
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
            BuildAnalysisChildren(node, analysis, node.PipelineRepoKey, node.ContainingRepoId, node.ResolvedDir);
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
        string baseDir)
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
                group.Children.Add(BuildScriptNode(s, org, project, pipelineRepoKey, baseDir));
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
        string pipelineRepoKey, string baseDir)
    {
        var node = new ScriptNode(reference);
        if (!string.IsNullOrEmpty(reference.FilePath))
        {
            var linkKey = new RepoLinkKey(org.AccountId, project.Id, pipelineRepoKey);
            var branch = _branches.Get(linkKey);
            var resolved = ResolveRepoPath(baseDir, reference.FilePath!);
            var target = new OpenTarget
            {
                RepoLinkKey = linkKey,
                RelativePath = resolved,
                DisplayName = node.Label,
                Branch = branch,
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

    private static string ResolveRepoPath(string baseDir, string reference)
    {
        var cleaned = reference.Replace('\\', '/').Trim();
        var combined = cleaned.StartsWith('/') ? cleaned : $"{baseDir}/{cleaned}";
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

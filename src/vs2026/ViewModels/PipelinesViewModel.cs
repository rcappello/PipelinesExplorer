using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.UI;
using PipelinesExplorer.VisualStudio.Auth;
using PipelinesExplorer.VisualStudio.AzureDevOps;
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
    private readonly Func<VisualStudioExtensibility?> _extensibilityProvider;

    private bool _isSignedIn;
    private bool _isBusy;
    private string? _connectionLabel;
    private string? _errorMessage;
    private string _patInputText = string.Empty;
    private CancellationTokenSource? _loadCts;

    public PipelinesViewModel(
        LoggingService logger,
        AdoAuthService auth,
        AdoClient ado,
        WorkspaceLinkService links,
        RepoBranchService branches,
        Func<VisualStudioExtensibility?> extensibilityProvider)
    {
        _logger = logger;
        _auth = auth;
        _ado = ado;
        _links = links;
        _branches = branches;
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
            var orgNodes = orgs
                .OrderBy(o => o.AccountName, StringComparer.OrdinalIgnoreCase)
                .Select(BuildOrganizationNode)
                .Cast<TreeNodeViewModel>()
                .ToList();
            ReplaceList(Roots, orgNodes);
            _logger.Info($"Loaded {orgNodes.Count} organization(s)");
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
        node.Children.Add(new InfoNode("Loading…", TreeNodeKind.Loading));
        node.PropertyChanged += async (_, e) =>
        {
            if (e.PropertyName == nameof(TreeNodeViewModel.IsExpanded) && node.IsExpanded
                && node.Children.Count == 1 && node.Children[0].Kind == TreeNodeKind.Loading)
            {
                await LoadProjectsAsync(node).ConfigureAwait(false);
            }
        };
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
                ? new TreeNodeViewModel[] { new InfoNode("(no projects)", TreeNodeKind.Info) }
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
        node.Children.Add(new InfoNode("Loading…", TreeNodeKind.Loading));
        node.PropertyChanged += async (_, e) =>
        {
            if (e.PropertyName == nameof(TreeNodeViewModel.IsExpanded) && node.IsExpanded
                && node.Children.Count == 1 && node.Children[0].Kind == TreeNodeKind.Loading)
            {
                await LoadPipelinesAsync(node).ConfigureAwait(false);
            }
        };
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
            string LabelOf(AdoPipelineDetail? d) =>
                d?.Configuration?.Repository?.Name ?? d?.Configuration?.Repository?.FullName ?? "(unknown repository)";
            string? TypeOf(AdoPipelineDetail? d) => d?.Configuration?.Repository?.Type;

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

                foreach (var (pipe, detail) in kv.Value.OrderBy(t => t.pipe.Name, StringComparer.OrdinalIgnoreCase))
                {
                    repoNode.Children.Add(new PipelineNode(node.Organization, node.Project, pipe, detail, repoKey));
                }
                repoNodes.Add(repoNode);
            }

            ReplaceList(node.Children, repoNodes.Count == 0
                ? new TreeNodeViewModel[] { new InfoNode("(no pipelines)", TreeNodeKind.Info) }
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
        ConnectionLabel = session is null
            ? null
            : session.Kind == SignInKind.Microsoft
                ? $"Microsoft \u00b7 {session.AccountLabel}{(string.IsNullOrEmpty(session.TenantId) ? string.Empty : $" \u00b7 {session.TenantId}")}"
                : $"PAT \u00b7 {session.AccountLabel}";

        if (session is null)
        {
            Roots.Clear();
        }
        else
        {
            RefreshFireAndForget();
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
}

using Microsoft.VisualStudio.Extensibility.UI;
using PipelinesExplorer.VisualStudio.AzureDevOps;
using PipelinesExplorer.VisualStudio.Resources;
using PipelinesExplorer.VisualStudio.Services;
using System.Globalization;
using System.Runtime.Serialization;

namespace PipelinesExplorer.VisualStudio.ViewModels;

/// <summary>Kind of an entry in the pipelines tree.</summary>
public enum TreeNodeKind
{
    Organization,
    Project,
    Repository,
    Pipeline,
    Group,
    Template,
    Script,
    Loading,
    Info,
    Error,
}

/// <summary>Subtype for <see cref="GroupNode"/>.</summary>
public enum GroupKind
{
    Templates,
    Scripts,
}

/// <summary>
/// Generic tree node view-model rendered by the WPF <c>TreeView</c> in the
/// <c>PipelinesToolWindowControl</c> XAML. Marked as a Remote UI data contract
/// so that property updates and observable child collections are correctly
/// proxied to the Visual Studio process.
/// </summary>
[DataContract]
public class TreeNodeViewModel : NotifyPropertyChangedObject
{
    private bool _isExpanded;
    private bool _hasLoadedChildren;
    private string _label = string.Empty;
    private string? _description;
    private string? _tooltip;
    private bool _isVisibleUnderFilter = true;

    public TreeNodeViewModel(TreeNodeKind kind)
    {
        Kind = kind;
        Children = new ObservableList<TreeNodeViewModel>();
    }

    /// <summary>
    /// True unless a filter is active and this node is neither a direct match
    /// nor an ancestor of a matched node. Bound to the WPF
    /// <c>TreeViewItem.Visibility</c> via <c>BooleanToVisibilityConverter</c>
    /// so hidden subtrees collapse away entirely.
    /// </summary>
    [DataMember]
    public bool IsVisibleUnderFilter
    {
        get => _isVisibleUnderFilter;
        internal set => SetProperty(ref _isVisibleUnderFilter, value);
    }

    /// <summary>Remembers the expansion state before the last auto-expand triggered by a filter, so that <c>ClearFilter</c> can restore it.</summary>
    internal bool ExpansionSnapshot { get; set; }
    /// <summary>True when the current filter has forced this node open; cleared on filter reset.</summary>
    internal bool IsAutoExpandedByFilter { get; set; }

    [DataMember]
    public TreeNodeKind Kind { get; }

    /// <summary>
    /// Localized strings consumed by the per-node ContextMenu in the Remote UI XAML.
    /// ContextMenu items live outside the visual tree, so they can't reach the root
    /// view-model's <c>Loc</c> via RelativeSource — exposing the same snapshot per
    /// node is the simplest workaround. See <see cref="LocalizedStrings"/>.
    /// </summary>
    [DataMember]
    public LocalizedStrings Loc { get; } = new LocalizedStrings();

    [DataMember]
    public string Label
    {
        get => _label;
        set => SetProperty(ref _label, value);
    }

    [DataMember]
    public string? Description
    {
        get => _description;
        set => SetProperty(ref _description, value);
    }

    [DataMember]
    public string? Tooltip
    {
        get => _tooltip;
        set => SetProperty(ref _tooltip, value);
    }

    [DataMember]
    public ObservableList<TreeNodeViewModel> Children { get; }

    /// <summary>
    /// String form of an <c>ImageMoniker</c> (e.g. <c>"KnownMonikers.GitRepository"</c>)
    /// resolved by <c>vs:Image.Source</c> at render time. Mirrors the
    /// <c>vscode.ThemeIcon</c> assigned to each node by the VS Code provider.
    /// </summary>
    [DataMember]
    public virtual string IconMoniker => Kind switch
    {
        TreeNodeKind.Loading => "KnownMonikers.StatusInformation",
        TreeNodeKind.Info => "KnownMonikers.StatusInformation",
        TreeNodeKind.Error => "KnownMonikers.StatusError",
        _ => "KnownMonikers.None",
    };

    [DataMember]
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetProperty(ref _isExpanded, value) && value && !_hasLoadedChildren)
            {
                _hasLoadedChildren = true;
                OnExpandedFirstTime();
            }
        }
    }

    /// <summary>Override to lazily load children the first time the node is expanded.</summary>
    protected virtual void OnExpandedFirstTime() { }

    // Per-kind context-menu visibility. Bound from XAML to MenuItem.Visibility via
    // BooleanToVisibilityConverter so each tree node only shows the actions that
    // make sense for it (the same ContextMenu template is shared by all nodes).
    [DataMember] public virtual bool CanOpen => false;
    [DataMember] public virtual bool CanLinkWorkspace => false;
    [DataMember] public virtual bool CanUnlinkWorkspace => false;
    [DataMember] public virtual bool CanSelectBranch => false;

    /// <summary>True when any of the workspace-link actions is available (used to hide the separator).</summary>
    [DataMember] public bool HasWorkspaceActions => (CanLinkWorkspace || CanUnlinkWorkspace || CanSelectBranch) && CanOpen;

    /// <summary>
    /// True when at least one context-menu action is available on this node.
    /// Bound to <c>ContextMenuService.IsEnabled</c> in the Remote UI XAML so
    /// nodes with no actions (root, organization, project, the
    /// <c>Templates</c>/<c>Scripts</c> group folders) don't show the small
    /// empty-context-menu rectangle on right-click.
    /// </summary>
    [DataMember] public bool HasContextMenu => CanOpen || CanLinkWorkspace || CanUnlinkWorkspace || CanSelectBranch;
}

[DataContract]
public sealed class OrganizationNode : TreeNodeViewModel
{
    public OrganizationNode(AdoOrganization org) : base(TreeNodeKind.Organization)
    {
        Organization = org;
        Label = org.AccountName;
        Tooltip = org.AccountUri;
    }

    public AdoOrganization Organization { get; }

    [DataMember]
    public override string IconMoniker => "KnownMonikers.Cloud";
}

[DataContract]
public sealed class ProjectNode : TreeNodeViewModel
{
    public ProjectNode(AdoOrganization org, AdoProject project) : base(TreeNodeKind.Project)
    {
        Organization = org;
        Project = project;
        Label = project.Name;
        Description = project.Description;
        Tooltip = project.Description ?? project.Name;
    }

    public AdoOrganization Organization { get; }
    public AdoProject Project { get; }

    [DataMember]
    public override string IconMoniker => "KnownMonikers.TeamProject";
}

[DataContract]
public sealed class RepositoryNode : TreeNodeViewModel
{
    private AsyncCommand? _linkCommand;
    private AsyncCommand? _unlinkCommand;
    private AsyncCommand? _selectBranchCommand;

    public RepositoryNode(
        AdoOrganization org,
        AdoProject project,
        string repoKey,
        string repoLabel,
        string? repoType,
        string? linkedFolder,
        string? branchOverride,
        int pipelineCount)
        : base(TreeNodeKind.Repository)
    {
        Organization = org;
        Project = project;
        RepoKey = repoKey;
        RepoLabel = repoLabel;
        RepoType = repoType;
        LinkedFolder = linkedFolder;
        BranchOverride = branchOverride;
        PipelineCount = pipelineCount;
        Label = repoLabel;
        Description = BuildDescription();
        Tooltip = BuildTooltip();
    }

    public AdoOrganization Organization { get; }
    public AdoProject Project { get; }
    public string RepoKey { get; }
    public string RepoLabel { get; }
    public string? RepoType { get; }
    public string? LinkedFolder { get; private set; }
    public string? BranchOverride { get; private set; }
    public int PipelineCount { get; }

    public RepoLinkKey LinkKey => new(Organization.AccountId, Project.Id, RepoKey);

    [DataMember]
    public AsyncCommand? LinkCommand
    {
        get => _linkCommand;
        internal set => SetProperty(ref _linkCommand, value);
    }

    [DataMember]
    public AsyncCommand? UnlinkCommand
    {
        get => _unlinkCommand;
        internal set => SetProperty(ref _unlinkCommand, value);
    }

    [DataMember]
    public AsyncCommand? SelectBranchCommand
    {
        get => _selectBranchCommand;
        internal set => SetProperty(ref _selectBranchCommand, value);
    }

    [DataMember]
    public bool IsLinked => !string.IsNullOrEmpty(LinkedFolder);

    [DataMember]
    public bool HasBranchOverride => !string.IsNullOrEmpty(BranchOverride);

    [DataMember]
    public override string IconMoniker => "KnownMonikers.GitRepository";

    // Workspace-link actions live on repositories only.
    [DataMember] public override bool CanLinkWorkspace => !IsLinked;
    [DataMember] public override bool CanUnlinkWorkspace => IsLinked;
    [DataMember] public override bool CanSelectBranch => true;

    /// <summary>Refresh the link/branch state from the latest service data.</summary>
    internal void UpdateState(string? linkedFolder, string? branchOverride)
    {
        LinkedFolder = linkedFolder;
        BranchOverride = branchOverride;
        Description = BuildDescription();
        Tooltip = BuildTooltip();
        RaiseNotifyPropertyChangedEvent(nameof(IsLinked));
        RaiseNotifyPropertyChangedEvent(nameof(HasBranchOverride));
        RaiseNotifyPropertyChangedEvent(nameof(IconMoniker));
        RaiseNotifyPropertyChangedEvent(nameof(CanLinkWorkspace));
        RaiseNotifyPropertyChangedEvent(nameof(CanUnlinkWorkspace));
        RaiseNotifyPropertyChangedEvent(nameof(HasContextMenu));
    }

    private string BuildDescription()
    {
        var pieces = new List<string> { PipelineCount.ToString(CultureInfo.InvariantCulture) };
        if (!string.IsNullOrEmpty(RepoType)) { pieces.Add(RepoType!); }
        if (!string.IsNullOrEmpty(LinkedFolder)) { pieces.Add("linked"); }
        if (!string.IsNullOrEmpty(BranchOverride)) { pieces.Add("branch: " + BranchOverride); }
        return string.Join(" \u00b7 ", pieces);
    }

    private string BuildTooltip() =>
        (string.IsNullOrEmpty(LinkedFolder) ? RepoLabel : $"{RepoLabel}\nLinked: {LinkedFolder}")
        + (string.IsNullOrEmpty(BranchOverride) ? "\nReading YAML from default branch" : $"\nReading YAML from branch: {BranchOverride}");
}

[DataContract]
public sealed class PipelineNode : TreeNodeViewModel
{
    private AsyncCommand? _openCommand;

    public PipelineNode(
        AdoOrganization org,
        AdoProject project,
        AdoPipeline pipeline,
        AdoPipelineDetail? detail,
        string repoKey)
        : base(TreeNodeKind.Pipeline)
    {
        Organization = org;
        Project = project;
        Pipeline = pipeline;
        Detail = detail;
        RepoKey = repoKey;
        Label = pipeline.Name;
        var folder = pipeline.Folder ?? string.Empty;
        Description = (folder.Length > 0 && folder != "\\") ? folder : null;
        Tooltip = ($"{folder}\\{pipeline.Name}").TrimStart('\\');
    }

    public AdoOrganization Organization { get; }
    public AdoProject Project { get; }
    public AdoPipeline Pipeline { get; }
    public AdoPipelineDetail? Detail { get; }
    public string RepoKey { get; }

    /// <summary>Repo id of the pipeline source (TfsGit only).</summary>
    public string? RepoId => Detail?.Configuration?.Repository?.Id;

    /// <summary>Directory of the root YAML inside the repo (e.g. <c>/solutions/foo/.ci</c>).</summary>
    public string YamlDir => DirOfRepoPath(Detail?.Configuration?.Path ?? "/");

    [DataMember]
    public override string IconMoniker => "KnownMonikers.Pipeline";

    [DataMember]
    public override bool CanOpen => true;

    [DataMember]
    public AsyncCommand? OpenCommand
    {
        get => _openCommand;
        internal set => SetProperty(ref _openCommand, value);
    }

    private static string DirOfRepoPath(string p)
    {
        var clean = p.Replace('\\', '/');
        var i = clean.LastIndexOf('/');
        return i <= 0 ? string.Empty : clean.Substring(0, i);
    }
}

[DataContract]
public sealed class GroupNode : TreeNodeViewModel
{
    public GroupNode(GroupKind group, int count) : base(TreeNodeKind.Group)
    {
        Group = group;
        TotalCount = count;
        Label = group == GroupKind.Templates ? Strings.Tree_Group_Templates : Strings.Tree_Group_Scripts;
        Description = count.ToString(CultureInfo.InvariantCulture);
    }

    public GroupKind Group { get; }

    /// <summary>
    /// Total number of items in this group before any filter is applied.
    /// Used to render a "visible/total" label when the filter is active.
    /// </summary>
    public int TotalCount { get; }

    /// <summary>
    /// Update <see cref="TreeNodeViewModel.Description"/> to reflect how many
    /// items are currently visible under the active filter. Pass <c>null</c>
    /// (or the total count) to reset to the plain total.
    /// </summary>
    public void UpdateFilteredCount(int? visibleCount)
    {
        Description = (visibleCount is null || visibleCount.Value == TotalCount)
            ? TotalCount.ToString(CultureInfo.InvariantCulture)
            : string.Format(
                CultureInfo.InvariantCulture,
                "{0}/{1}",
                visibleCount.Value,
                TotalCount);
    }

    [DataMember]
    public override string IconMoniker => Group == GroupKind.Templates
        ? "KnownMonikers.FolderClosed"
        : "KnownMonikers.Console";
}

[DataContract]
public sealed class TemplateNode : TreeNodeViewModel
{
    private AsyncCommand? _openCommand;

    public TemplateNode(
        TemplateRef reference,
        AdoOrganization org,
        AdoProject project,
        string pipelineRepoKey,
        string? containingRepoId,
        string containingDir)
        : base(TreeNodeKind.Template)
    {
        Reference = reference;
        Organization = org;
        Project = project;
        PipelineRepoKey = pipelineRepoKey;
        ContainingRepoId = containingRepoId;
        ContainingDir = containingDir;
        Label = BaseName(reference.Path);
        Description = reference.Repository is null ? null : "@" + reference.Repository;
        Tooltip = reference.Repository is null ? reference.Path : $"{reference.Path} @{reference.Repository}";
    }

    public TemplateRef Reference { get; }
    public AdoOrganization Organization { get; }
    public AdoProject Project { get; }
    public string PipelineRepoKey { get; }
    public string? ContainingRepoId { get; }
    public string ContainingDir { get; }

    /// <summary>Repo-absolute resolved path of this template (only meaningful for same-repo).</summary>
    public string ResolvedPath => ResolveRepoPath(ContainingDir, Reference.Path);
    public string ResolvedDir => DirOfRepoPath(ResolvedPath);

    /// <summary>True for same-repo templates whose body can be analysed and expanded.</summary>
    public bool IsSameRepoExpandable => Reference.Repository is null && !string.IsNullOrEmpty(ContainingRepoId);

    [DataMember]
    public override string IconMoniker => "KnownMonikers.YamlFile";

    [DataMember]
    public override bool CanOpen => true;

    [DataMember]
    public AsyncCommand? OpenCommand
    {
        get => _openCommand;
        internal set => SetProperty(ref _openCommand, value);
    }

    private static string BaseName(string p)
    {
        var clean = p.Replace('\\', '/').TrimEnd('/');
        var i = clean.LastIndexOf('/');
        return i >= 0 ? clean.Substring(i + 1) : clean;
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

    private static string DirOfRepoPath(string p)
    {
        var clean = p.Replace('\\', '/');
        var i = clean.LastIndexOf('/');
        return i <= 0 ? string.Empty : clean.Substring(0, i);
    }
}

[DataContract]
public sealed class ScriptNode : TreeNodeViewModel
{
    private AsyncCommand? _openCommand;

    public ScriptNode(ScriptRef reference) : base(TreeNodeKind.Script)
    {
        Reference = reference;
        Label = reference.FilePath is not null
            ? BaseName(reference.FilePath)
            : (reference.Inline ? Strings.Tree_InlineScript : Strings.Tree_UnknownSource);
        Description = reference.Task;
        Tooltip = reference.FilePath is not null
            ? $"{reference.Task} \u2192 {reference.FilePath}"
            : $"{reference.Task} ({(reference.Inline ? $"inline{(reference.Line is int l ? $" @ line {l}" : "")}" : "unknown")})";
    }

    public ScriptRef Reference { get; }

    [DataMember]
    public override string IconMoniker => IconForKind(Reference.Kind, Reference.Inline);

    // File-backed scripts open the script file; inline scripts open the
    // containing YAML at the line where the script is defined (matches the
    // VS Code "Open Inline Script Location" behaviour).
    [DataMember]
    public override bool CanOpen => !string.IsNullOrEmpty(Reference.FilePath)
        || (Reference.Inline && Reference.Line is int l && l > 0);

    private static string IconForKind(ScriptKind kind, bool inline) => kind switch
    {
        ScriptKind.PowerShell => inline ? "KnownMonikers.PowershellInteractiveWindow" : "KnownMonikers.PowershellFile",
        ScriptKind.Bash => "KnownMonikers.BashFile",
        ScriptKind.Cmd => "KnownMonikers.BATFile",
        ScriptKind.Python => "KnownMonikers.PYFileNode",
        ScriptKind.AzureCli => "KnownMonikers.AzureLogo",
        _ => "KnownMonikers.Console",
    };

    [DataMember]
    public AsyncCommand? OpenCommand
    {
        get => _openCommand;
        internal set => SetProperty(ref _openCommand, value);
    }

    private static string BaseName(string p)
    {
        var clean = p.Replace('\\', '/').TrimEnd('/');
        var i = clean.LastIndexOf('/');
        return i >= 0 ? clean.Substring(i + 1) : clean;
    }
}

[DataContract]
public sealed class InfoNode : TreeNodeViewModel
{
    public InfoNode(string label, TreeNodeKind kind = TreeNodeKind.Info) : base(kind)
    {
        Label = label;
    }
}

using System.Collections.Generic;
using System.Globalization;
using System.Runtime.Serialization;
using Microsoft.VisualStudio.Extensibility.UI;
using PipelinesExplorer.VisualStudio.AzureDevOps;
using PipelinesExplorer.VisualStudio.Services;

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

    public TreeNodeViewModel(TreeNodeKind kind)
    {
        Kind = kind;
        Children = new ObservableList<TreeNodeViewModel>();
    }

    [DataMember]
    public TreeNodeKind Kind { get; }

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
    public override string IconMoniker => IsLinked ? "KnownMonikers.GitRepositoryLocal" : "KnownMonikers.GitRepository";

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
        Label = group == GroupKind.Templates ? "Templates" : "PowerShell scripts";
        Description = count.ToString(CultureInfo.InvariantCulture);
    }

    public GroupKind Group { get; }

    [DataMember]
    public override string IconMoniker => Group == GroupKind.Templates
        ? "KnownMonikers.FolderClosed"
        : "KnownMonikers.PowershellFile";
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

    public ScriptNode(PowerShellRef reference) : base(TreeNodeKind.Script)
    {
        Reference = reference;
        Label = reference.FilePath is not null
            ? BaseName(reference.FilePath)
            : (reference.Inline ? "(inline script)" : "(unknown source)");
        Description = reference.Task;
        Tooltip = reference.FilePath is not null
            ? $"{reference.Task} \u2192 {reference.FilePath}"
            : $"{reference.Task} ({(reference.Inline ? $"inline{(reference.Line is int l ? $" @ line {l}" : "")}" : "unknown")})";
    }

    public PowerShellRef Reference { get; }

    [DataMember]
    public override string IconMoniker => Reference.FilePath is not null
        ? "KnownMonikers.PowershellFile"
        : "KnownMonikers.PowershellInteractiveWindow";

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

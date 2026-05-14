using System.Collections.ObjectModel;
using System.Runtime.Serialization;
using Microsoft.VisualStudio.Extensibility.UI;
using PipelinesExplorer.VisualStudio.AzureDevOps;

namespace PipelinesExplorer.VisualStudio.ViewModels;

/// <summary>Kind of an entry in the pipelines tree.</summary>
public enum TreeNodeKind
{
    Organization,
    Project,
    Repository,
    Pipeline,
    Loading,
    Info,
    Error,
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
}

[DataContract]
public sealed class RepositoryNode : TreeNodeViewModel
{
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
        Label = repoLabel;
        var pieces = new System.Collections.Generic.List<string>
        {
            pipelineCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        if (!string.IsNullOrEmpty(repoType)) { pieces.Add(repoType!); }
        if (!string.IsNullOrEmpty(linkedFolder)) { pieces.Add("linked"); }
        if (!string.IsNullOrEmpty(branchOverride)) { pieces.Add("branch: " + branchOverride); }
        Description = string.Join(" \u00b7 ", pieces);
        Tooltip = (string.IsNullOrEmpty(linkedFolder) ? repoLabel : $"{repoLabel}\nLinked: {linkedFolder}")
            + (string.IsNullOrEmpty(branchOverride) ? "\nReading YAML from default branch" : $"\nReading YAML from branch: {branchOverride}");
    }

    public AdoOrganization Organization { get; }
    public AdoProject Project { get; }
    public string RepoKey { get; }
    public string RepoLabel { get; }
    public string? RepoType { get; }
    public string? LinkedFolder { get; }
    public string? BranchOverride { get; }
}

[DataContract]
public sealed class PipelineNode : TreeNodeViewModel
{
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
}

[DataContract]
public sealed class InfoNode : TreeNodeViewModel
{
    public InfoNode(string label, TreeNodeKind kind = TreeNodeKind.Info) : base(kind)
    {
        Label = label;
    }
}

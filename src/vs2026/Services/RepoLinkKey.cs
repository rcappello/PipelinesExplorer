namespace PipelinesExplorer.VisualStudio.Services;

/// <summary>
/// Composite key identifying a repository inside an Azure DevOps tenant.
/// Mirrors the shape used by the VS Code client (<c>RepoLinkKey</c>).
/// </summary>
public readonly record struct RepoLinkKey(string OrgAccountId, string ProjectId, string RepoKey)
{
    /// <summary>Stable string encoding used as the storage key.</summary>
    public string Encode() => $"{OrgAccountId}::{ProjectId}::{RepoKey}";
}

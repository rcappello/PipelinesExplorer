namespace PipelinesExplorer.VisualStudio.AzureDevOps;

/// <summary>
/// Classified outcome of <see cref="AdoClient.ProbeOrganizationAsync"/>.
/// Mirrors the string union used by the VS Code client's probe helper so
/// the fallback UI can react to the same set of cases in both extensions.
/// </summary>
public enum OrgProbeResult
{
    /// <summary>Token is authorized for the organization; safe to persist.</summary>
    Ok,
    /// <summary>Token is not authorized for the organization (HTTP 401/403).</summary>
    Unauthorized,
    /// <summary>Organization does not exist / is not reachable at the expected URL (HTTP 404).</summary>
    NotFound,
    /// <summary>Any other failure (5xx, DNS, network cancellation, non-JSON response).</summary>
    NetworkError,
}

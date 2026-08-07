using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace PipelinesExplorer.VisualStudio.AzureDevOps;

/// <summary>
/// Supplies the <c>Authorization</c> header that <see cref="AdoClient"/> attaches
/// to outbound REST calls. Implemented by the PAT and Microsoft Entra flows.
/// </summary>
public interface IAdoAuthHeaderProvider
{
    /// <summary>
    /// Returns the <c>Authorization</c> header to send with the next request,
    /// or <c>null</c> if no credentials are available.
    /// </summary>
    /// <param name="orgHint">
    /// Canonical Azure DevOps organization name the request is targeting, if
    /// any. Used by the PAT provider to pick a per-organization PAT when one
    /// is stored for that org (plan 002 §2.3). <c>null</c> for calls that
    /// target SPS-level endpoints (<c>app.vssps.visualstudio.com/…</c>) or
    /// otherwise have no org context.
    /// </param>
    Task<AuthenticationHeaderValue?> GetAuthHeaderAsync(string? orgHint, CancellationToken cancellationToken);
}

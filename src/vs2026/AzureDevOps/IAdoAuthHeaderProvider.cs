using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace PipelinesExplorer.VisualStudio.AzureDevOps;

/// <summary>
/// Supplies the <c>Authorization</c> header that <see cref="AdoClient"/> attaches
/// to outbound REST calls. Implemented by Phase 2 (PAT and Microsoft Entra
/// flows). For Phase 1 the client can be constructed without a provider, in
/// which case calls will fail with <see cref="AdoUnauthorizedException"/>.
/// </summary>
public interface IAdoAuthHeaderProvider
{
    /// <summary>
    /// Returns the <c>Authorization</c> header to send with the next request,
    /// or <c>null</c> if no credentials are available.
    /// </summary>
    Task<AuthenticationHeaderValue?> GetAuthHeaderAsync(CancellationToken cancellationToken);
}

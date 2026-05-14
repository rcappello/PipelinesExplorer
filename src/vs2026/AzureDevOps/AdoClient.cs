using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PipelinesExplorer.VisualStudio.Services;

namespace PipelinesExplorer.VisualStudio.AzureDevOps;

/// <summary>
/// Thin REST wrapper around the Azure DevOps APIs we need. Mirrors
/// <c>AdoClient</c> in the VS Code client: same endpoints, same error
/// semantics, same sort order for <see cref="ListBranchesAsync"/>.
/// </summary>
public sealed class AdoClient : IDisposable
{
    private const string ApiVersion = "7.1";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly IAdoAuthHeaderProvider? _authProvider;
    private readonly LoggingService _logger;

    public AdoClient(LoggingService logger, IAdoAuthHeaderProvider? authProvider = null, HttpClient? httpClient = null)
    {
        _logger = logger;
        _authProvider = authProvider;
        _http = httpClient ?? new HttpClient();
        _ownsHttpClient = httpClient is null;
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _http.Dispose();
        }
    }

    public Task<AdoProfile> GetProfileAsync(CancellationToken cancellationToken = default)
    {
        var url = $"https://app.vssps.visualstudio.com/_apis/profile/profiles/me?api-version={ApiVersion}";
        return GetJsonAsync<AdoProfile>(url, cancellationToken);
    }

    public async Task<IReadOnlyList<AdoOrganization>> ListOrganizationsAsync(string memberId, CancellationToken cancellationToken = default)
    {
        var url = $"https://app.vssps.visualstudio.com/_apis/accounts?api-version={ApiVersion}&memberId={Uri.EscapeDataString(memberId)}";
        var res = await GetJsonAsync<AdoListResponse<AccountsResponseEntry>>(url, cancellationToken).ConfigureAwait(false);
        return res.Value.Select(a => new AdoOrganization
        {
            AccountId = a.AccountId,
            AccountName = a.AccountName,
            AccountUri = string.IsNullOrEmpty(a.AccountUri) ? $"https://dev.azure.com/{a.AccountName}" : a.AccountUri!,
        }).ToList();
    }

    public async Task<IReadOnlyList<AdoProject>> ListProjectsAsync(string organizationName, CancellationToken cancellationToken = default)
    {
        var url = $"https://dev.azure.com/{Uri.EscapeDataString(organizationName)}/_apis/projects?api-version={ApiVersion}&stateFilter=wellFormed&$top=1000";
        var res = await GetJsonAsync<AdoListResponse<AdoProject>>(url, cancellationToken).ConfigureAwait(false);
        return res.Value;
    }

    public async Task<IReadOnlyList<AdoPipeline>> ListPipelinesAsync(string organizationName, string projectName, CancellationToken cancellationToken = default)
    {
        var url = $"https://dev.azure.com/{Uri.EscapeDataString(organizationName)}/{Uri.EscapeDataString(projectName)}/_apis/pipelines?api-version={ApiVersion}&$top=1000";
        var res = await GetJsonAsync<AdoListResponse<AdoPipeline>>(url, cancellationToken).ConfigureAwait(false);
        return res.Value;
    }

    public Task<AdoPipelineDetail> GetPipelineAsync(string organizationName, string projectName, int pipelineId, CancellationToken cancellationToken = default)
    {
        var url = $"https://dev.azure.com/{Uri.EscapeDataString(organizationName)}/{Uri.EscapeDataString(projectName)}/_apis/pipelines/{pipelineId}?api-version={ApiVersion}";
        return GetJsonAsync<AdoPipelineDetail>(url, cancellationToken);
    }

    /// <summary>Look up a Git repository by id. Returns <c>null</c> on 404.</summary>
    public async Task<AdoRepository?> GetRepositoryAsync(string organizationName, string projectName, string repositoryId, CancellationToken cancellationToken = default)
    {
        var url = $"https://dev.azure.com/{Uri.EscapeDataString(organizationName)}/{Uri.EscapeDataString(projectName)}/_apis/git/repositories/{Uri.EscapeDataString(repositoryId)}?api-version={ApiVersion}";
        try
        {
            return await GetJsonAsync<AdoRepository>(url, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.Message.Contains(" 404 ", StringComparison.Ordinal))
        {
            return null;
        }
    }

    /// <summary>
    /// Fetch the raw text content of a file from a Git repository hosted in
    /// Azure DevOps. Returns <c>null</c> if the file is missing (404).
    /// </summary>
    public async Task<string?> GetFileContentAsync(
        string organizationName,
        string projectName,
        string repositoryId,
        string path,
        string? branch = null,
        CancellationToken cancellationToken = default)
    {
        var normalized = path.StartsWith("/", StringComparison.Ordinal) ? path : "/" + path;
        var url =
            $"https://dev.azure.com/{Uri.EscapeDataString(organizationName)}/{Uri.EscapeDataString(projectName)}/_apis/git/repositories/{Uri.EscapeDataString(repositoryId)}/items?path={Uri.EscapeDataString(normalized)}&api-version={ApiVersion}&includeContent=true&$format=text";
        if (!string.IsNullOrEmpty(branch))
        {
            url += $"&versionDescriptor.versionType=branch&versionDescriptor.version={Uri.EscapeDataString(branch!)}";
        }
        return await GetTextAsync(url, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// List heads (branches) of a Git repository. Returns short branch names
    /// (without the <c>refs/heads/</c> prefix), sorted invariant-culture
    /// ordinal — same ordering as the VS Code client.
    /// </summary>
    public async Task<IReadOnlyList<string>> ListBranchesAsync(string organizationName, string projectName, string repositoryId, CancellationToken cancellationToken = default)
    {
        var url = $"https://dev.azure.com/{Uri.EscapeDataString(organizationName)}/{Uri.EscapeDataString(projectName)}/_apis/git/repositories/{Uri.EscapeDataString(repositoryId)}/refs?filter=heads/&api-version={ApiVersion}&$top=1000";
        var res = await GetJsonAsync<AdoListResponse<GitRefEntry>>(url, cancellationToken).ConfigureAwait(false);
        return res.Value
            .Select(r => r.Name.StartsWith("refs/heads/", StringComparison.Ordinal) ? r.Name.Substring("refs/heads/".Length) : r.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
    }

    private async Task<T> GetJsonAsync<T>(string url, CancellationToken cancellationToken)
    {
        using var request = await BuildRequestAsync(url, cancellationToken).ConfigureAwait(false);
        _logger.Debug($"GET {url}");

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            await ThrowForFailureAsync(response, url, cancellationToken).ConfigureAwait(false);
        }

        var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        if (!contentType.Contains("application/json", StringComparison.OrdinalIgnoreCase))
        {
            throw new AdoUnauthorizedException(
                401,
                $"Unexpected non-JSON response from {url}. Authentication may have expired.");
        }

#if NET6_0_OR_GREATER
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
#else
        using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
#endif
        var value = await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
        return value ?? throw new InvalidOperationException($"Empty JSON response from {url}");
    }

    private async Task<string?> GetTextAsync(string url, CancellationToken cancellationToken)
    {
        using var request = await BuildRequestAsync(url, cancellationToken).ConfigureAwait(false);
        _logger.Debug($"GET {url}");

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        if (!response.IsSuccessStatusCode)
        {
            await ThrowForFailureAsync(response, url, cancellationToken).ConfigureAwait(false);
        }

#if NET6_0_OR_GREATER
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
#else
        return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
#endif
    }

    private async Task<HttpRequestMessage> BuildRequestAsync(string url, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd("application/json");

        if (_authProvider is not null)
        {
            var header = await _authProvider.GetAuthHeaderAsync(cancellationToken).ConfigureAwait(false);
            if (header is not null)
            {
                request.Headers.Authorization = header;
            }
        }

        return request;
    }

    private async Task ThrowForFailureAsync(HttpResponseMessage response, string url, CancellationToken cancellationToken)
    {
        string? body = null;
        try
        {
#if NET6_0_OR_GREATER
            body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
#else
            body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
#endif
            if (body.Length > 500)
            {
                body = body.Substring(0, 500) + "…";
            }
        }
        catch
        {
            // ignored
        }

        var msg = $"ADO REST call failed: {(int)response.StatusCode} {response.ReasonPhrase} for {url}"
            + (string.IsNullOrEmpty(body) ? string.Empty : $" :: {body}");
        _logger.Error(msg);

        if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new AdoUnauthorizedException(
                (int)response.StatusCode,
                $"Azure DevOps rejected the credentials ({(int)response.StatusCode}). The stored token may be expired or revoked.");
        }

        throw new HttpRequestException(msg);
    }
}

using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PipelinesExplorer.VisualStudio.AzureDevOps;
using PipelinesExplorer.VisualStudio.Services;

namespace PipelinesExplorer.VisualStudio.Auth;

/// <summary>
/// Unified auth facade for Azure DevOps. Mirrors <c>AuthService</c> from the
/// VS Code client: only one active sign-in at a time, the chosen kind is
/// persisted so subsequent activations restore silently.
/// </summary>
public sealed class AdoAuthService : IAdoAuthHeaderProvider, IDisposable
{
    private const string SignInKindKey = "pipelinesexplorer.signInKind";
    private const string MsTenantKey = "pipelinesexplorer.microsoftTenant";
    private const string MsTenantNameKey = "pipelinesexplorer.microsoftTenantName";

    private readonly LoggingService _logger;
    private readonly JsonStateStore _store;
    private readonly PatCredentialStore _patStore;
    private readonly MicrosoftAuthClient _msal;
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    private AdoSession? _currentSession;

    public AdoAuthService(
        LoggingService logger,
        JsonStateStore? store = null,
        PatCredentialStore? patStore = null,
        MicrosoftAuthClient? msal = null,
        HttpClient? httpClient = null)
    {
        _logger = logger;
        _store = store ?? JsonStateStore.Shared;
        _patStore = patStore ?? new PatCredentialStore();
        _msal = msal ?? new MicrosoftAuthClient(logger);
        _http = httpClient ?? new HttpClient();
        _ownsHttpClient = httpClient is null;
    }

    /// <summary>Raised when the active session is created, replaced, or cleared.</summary>
    public event EventHandler<AdoSession?>? SessionChanged;

    public AdoSession? Session => _currentSession;

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _http.Dispose();
        }
        _refreshGate.Dispose();
    }

    /// <inheritdoc />
    public async Task<AuthenticationHeaderValue?> GetAuthHeaderAsync(CancellationToken cancellationToken)
    {
        var session = _currentSession;
        if (session is null)
        {
            return null;
        }

        if (session.Kind == SignInKind.Microsoft)
        {
            // Re-acquire silently to honour MSAL's cached refresh; if it fails
            // we surface 401 to the caller, who will react accordingly.
            var refreshed = await TryRefreshMicrosoftAsync(cancellationToken).ConfigureAwait(false);
            session = refreshed ?? session;
            return new AuthenticationHeaderValue("Bearer", session.AccessToken);
        }

        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes(":" + session.AccessToken));
        return new AuthenticationHeaderValue("Basic", basic);
    }

    /// <summary>Best-effort silent restore using the previously chosen provider.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var kind = GetStoredKind();
        _logger.Info($"AdoAuthService.Initialize: stored kind = {kind?.ToString() ?? "<none>"}");
        if (kind is null)
        {
            return;
        }

        try
        {
            var restored = await AcquireSessionAsync(kind.Value, createIfNone: false, cancellationToken).ConfigureAwait(false);
            _logger.Info($"AdoAuthService.Initialize: silent restore {(restored is null ? "returned no session" : "succeeded")}");
        }
        catch (Exception ex)
        {
            _logger.Error("AdoAuthService.Initialize: silent restore failed", ex);
            ClearSession();
        }
    }

    public Task<AdoSession?> SignInWithMicrosoftAsync(CancellationToken cancellationToken = default)
    {
        _logger.Info("SignInWithMicrosoft invoked");
        return AcquireSessionAsync(SignInKind.Microsoft, createIfNone: true, cancellationToken);
    }

#pragma warning disable IDE0060 // cancellationToken kept for API symmetry with other SignIn methods
    public Task<AdoSession?> SignInWithPatAsync(string token, CancellationToken cancellationToken = default)
#pragma warning restore IDE0060
    {
        _logger.Info("SignInWithPat invoked");
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        _patStore.Write(token);
        _store.Set(SignInKindKey, nameof(SignInKind.Pat));

        var session = new AdoSession(SignInKind.Pat, token, "Personal Access Token");
        _currentSession = session;
        SessionChanged?.Invoke(this, session);
        _logger.Info("PAT sign-in completed");
        return Task.FromResult<AdoSession?>(session);
    }

    public Task SignOutAsync(CancellationToken cancellationToken = default) => SignOutInternalAsync(reset: false);

    /// <summary>Wipes ALL persisted state (PAT secret + chosen provider + tenant override).</summary>
    public Task ResetAsync(CancellationToken cancellationToken = default) => SignOutInternalAsync(reset: true);

    /// <summary>
    /// Lists the Microsoft Entra tenants the signed-in account has access to,
    /// by calling ARM's <c>/tenants</c> endpoint with a tenant-agnostic token.
    /// </summary>
    public async Task<IReadOnlyList<TenantInfo>> ListAvailableTenantsAsync(CancellationToken cancellationToken = default)
    {
        var arm = await _msal.AcquireArmTokenAsync(cancellationToken).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{MicrosoftAuthClient.ArmResource}/tenants?api-version=2022-12-01");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", arm.AccessToken);

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            throw new InvalidOperationException($"ARM /tenants returned HTTP {(int)response.StatusCode}: {body}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

        var result = new List<TenantInfo>();
        if (doc.RootElement.TryGetProperty("value", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in arr.EnumerateArray())
            {
                if (!t.TryGetProperty("tenantId", out var tid) || tid.ValueKind != JsonValueKind.String)
                {
                    continue;
                }
                var displayName = t.TryGetProperty("displayName", out var dn) && dn.ValueKind == JsonValueKind.String
                    ? dn.GetString()!
                    : (t.TryGetProperty("defaultDomain", out var ddTry) && ddTry.ValueKind == JsonValueKind.String ? ddTry.GetString()! : tid.GetString()!);
                string? defaultDomain = null;
                if (t.TryGetProperty("defaultDomain", out var dd) && dd.ValueKind == JsonValueKind.String)
                {
                    defaultDomain = dd.GetString();
                }
                else if (t.TryGetProperty("domains", out var domains) && domains.ValueKind == JsonValueKind.Array)
                {
                    var first = domains.EnumerateArray().FirstOrDefault();
                    if (first.ValueKind == JsonValueKind.String)
                    {
                        defaultDomain = first.GetString();
                    }
                }
                result.Add(new TenantInfo(tid.GetString()!, displayName, defaultDomain));
            }
        }
        return result;
    }

    /// <summary>
    /// Switch the active Microsoft sign-in to a specific tenant. Pass <c>null</c>
    /// to clear the override and fall back to the default tenant.
    /// </summary>
    public Task<AdoSession?> SwitchTenantAsync(string? tenantId, string? displayName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(tenantId))
        {
            _store.Remove(MsTenantKey);
            _store.Remove(MsTenantNameKey);
        }
        else
        {
            _store.Set(MsTenantKey, tenantId!);
            if (!string.IsNullOrEmpty(displayName))
            {
                _store.Set(MsTenantNameKey, displayName!);
            }
            else
            {
                _store.Remove(MsTenantNameKey);
            }
        }
        _logger.Info($"Microsoft tenant override = {(tenantId ?? "<default>")}{(displayName is null ? string.Empty : $" ({displayName})")}");
        return AcquireSessionAsync(SignInKind.Microsoft, createIfNone: true, cancellationToken);
    }

    public string? GetStoredTenant() => _store.Get<string?>(MsTenantKey, null);

    public string? GetStoredTenantName() => _store.Get<string?>(MsTenantNameKey, null);

    private async Task<AdoSession?> AcquireSessionAsync(SignInKind kind, bool createIfNone, CancellationToken cancellationToken)
    {
        if (kind == SignInKind.Microsoft)
        {
            var tenant = GetStoredTenant();
            var token = await _msal.AcquireAdoTokenAsync(tenant, createIfNone, cancellationToken).ConfigureAwait(false);
            if (token is null)
            {
                ClearSession();
                return null;
            }

            var tid = ExtractTenantFromJwt(token.AccessToken) ?? tenant;
            var label = token.Account?.Username ?? "Microsoft Account";
            var session = new AdoSession(SignInKind.Microsoft, token.AccessToken, label, tid);
            ApplySession(session);
            return session;
        }

        // PAT
        var pat = _patStore.Read();
        if (string.IsNullOrEmpty(pat))
        {
            ClearSession();
            return null;
        }
        var patSession = new AdoSession(SignInKind.Pat, pat!, "Personal Access Token");
        ApplySession(patSession);
        return patSession;
    }

    private async Task<AdoSession?> TryRefreshMicrosoftAsync(CancellationToken cancellationToken)
    {
        if (_currentSession?.Kind != SignInKind.Microsoft)
        {
            return null;
        }

        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var tenant = GetStoredTenant();
            var token = await _msal.AcquireAdoTokenAsync(tenant, createIfNone: false, cancellationToken).ConfigureAwait(false);
            if (token is null)
            {
                return null;
            }
            var tid = ExtractTenantFromJwt(token.AccessToken) ?? tenant;
            var session = new AdoSession(SignInKind.Microsoft, token.AccessToken, token.Account?.Username ?? "Microsoft Account", tid);
            ApplySession(session);
            return session;
        }
        catch (Exception ex)
        {
            _logger.Warn($"Microsoft token refresh failed: {ex.Message}");
            return null;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private void ApplySession(AdoSession session)
    {
        _currentSession = session;
        _store.Set(SignInKindKey, session.Kind == SignInKind.Microsoft ? nameof(SignInKind.Microsoft) : nameof(SignInKind.Pat));
        SessionChanged?.Invoke(this, session);
    }

    private void ClearSession()
    {
        if (_currentSession is null)
        {
            return;
        }
        _currentSession = null;
        SessionChanged?.Invoke(this, null);
    }

    private async Task SignOutInternalAsync(bool reset)
    {
        var kind = GetStoredKind();
        _logger.Info($"SignOut invoked (kind={kind?.ToString() ?? "<none>"}, reset={reset})");

        if (kind == SignInKind.Pat || reset)
        {
            try
            {
                _patStore.Delete();
            }
            catch (Exception ex)
            {
                _logger.Warn($"PAT delete failed: {ex.Message}");
            }
        }

        if (kind == SignInKind.Microsoft || reset)
        {
            try
            {
                await _msal.SignOutAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Warn($"MSAL SignOut failed: {ex.Message}");
            }
        }

        _store.Remove(SignInKindKey);
        if (reset)
        {
            _store.Remove(MsTenantKey);
            _store.Remove(MsTenantNameKey);
        }
        ClearSession();
    }

    private SignInKind? GetStoredKind()
    {
        var raw = _store.Get<string?>(SignInKindKey, null);
        return raw switch
        {
            nameof(SignInKind.Microsoft) => SignInKind.Microsoft,
            nameof(SignInKind.Pat) => SignInKind.Pat,
            _ => null,
        };
    }

    private static string? ExtractTenantFromJwt(string token)
    {
        try
        {
            var jwt = new JwtSecurityToken(token);
            return jwt.Payload.TryGetValue("tid", out var tid) ? tid?.ToString() : null;
        }
        catch
        {
            return null;
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;
using PipelinesExplorer.VisualStudio.Services;

namespace PipelinesExplorer.VisualStudio.Auth;

/// <summary>
/// MSAL-based public client that signs the user in with Microsoft Entra ID
/// using the WAM broker (so the experience matches the OS-level account picker
/// already used by Visual Studio itself). Mirrors the scopes and tenant
/// switching behaviour of the VS Code client's built-in Microsoft provider.
/// </summary>
public sealed class MicrosoftAuthClient
{
    /// <summary>Azure DevOps "well-known" application id used as resource scope.</summary>
    public const string AdoResourceId = "499b84ac-1321-427f-aa17-267ca6975798";

    /// <summary>Azure Resource Manager endpoint (used to enumerate available tenants).</summary>
    public const string ArmResource = "https://management.azure.com";

    /// <summary>
    /// Application (client) id used by MSAL. Replace with the id of your own
    /// Entra app registration before publishing the extension. The default
    /// value is Visual Studio's own first-party client id which works during
    /// local F5 development against personal/dev tenants but is not licensed
    /// for distribution.
    /// </summary>
    public const string DefaultClientId = "872cd9fa-d31f-45e0-9eab-6e460a02d1f1";

    private static readonly string[] AdoScopes = { AdoResourceId + "/.default" };
    private static readonly string[] ArmScopes = { ArmResource + "/.default" };

    private readonly LoggingService _logger;
    private readonly string _clientId;
    private IPublicClientApplication? _commonApp;
    private readonly Dictionary<string, IPublicClientApplication> _tenantApps = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Threading.Lock _gate = new();

    /// <summary>
    /// Cross-platform MSAL token cache shared by every <see cref="IPublicClientApplication"/>
    /// instance owned by this client (common authority + every tenant-pinned
    /// authority). Created lazily on first <see cref="GetAppAsync"/> call.
    /// Documented at https://learn.microsoft.com/entra/msal/dotnet/how-to/token-cache-serialization?tabs=desktop.
    /// </summary>
    private MsalCacheHelper? _cacheHelper;
    private readonly SemaphoreSlim _cacheGate = new(1, 1);

    public MicrosoftAuthClient(LoggingService logger, string? clientId = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _clientId = clientId ?? DefaultClientId;
    }

    /// <summary>
    /// Acquire an Azure DevOps access token. Tries silent first; falls back to
    /// an interactive WAM prompt when <paramref name="createIfNone"/> is true.
    /// Returns <c>null</c> if no cached account exists and interaction is not
    /// allowed.
    /// </summary>
    public async Task<AuthenticationResult?> AcquireAdoTokenAsync(string? tenantId, bool createIfNone, CancellationToken cancellationToken)
    {
        var app = await GetAppAsync(tenantId, cancellationToken).ConfigureAwait(false);
        _logger.Info($"MSAL ADO token requested (tenant={tenantId ?? "common"}, createIfNone={createIfNone})");

        // 1. Cached MSAL account (previous interactive sign-in).
        var accounts = (await app.GetAccountsAsync().ConfigureAwait(false)).ToList();
        _logger.Info($"MSAL cache has {accounts.Count} account(s).");
        var account = accounts.FirstOrDefault();
        if (account is not null)
        {
            try
            {
                var r = await app.AcquireTokenSilent(AdoScopes, account).ExecuteAsync(cancellationToken).ConfigureAwait(false);
                _logger.Info($"MSAL silent (cached) ok for {r.Account?.Username}.");
                return r;
            }
            catch (MsalUiRequiredException ex)
            {
                _logger.Info($"MSAL silent (cached) failed ({ex.ErrorCode}).");
            }
        }

        // 2. Try the OS-signed-in account (best-effort SSO with the Windows /
        //    Visual Studio user). Works only on AAD-joined / WAM-capable boxes
        //    when the OS account is licensed for the tenant.
        try
        {
            var r = await app.AcquireTokenSilent(AdoScopes, PublicClientApplication.OperatingSystemAccount)
                .ExecuteAsync(cancellationToken)
                .ConfigureAwait(false);
            _logger.Info($"MSAL silent (OS) ok for {r.Account?.Username}.");
            return r;
        }
        catch (MsalUiRequiredException) { /* fall through */ }
        catch (MsalException ex) { _logger.Info($"MSAL OS-account silent failed: {ex.ErrorCode}"); }

        if (!createIfNone)
        {
            _logger.Info("MSAL: no cached account and createIfNone=false; returning null.");
            return null;
        }

        // 3. Interactive sign-in via the system browser. The system browser
        //    flow does not require a parent HWND, which is critical because
        //    this extension runs out-of-process and has no top-level window
        //    of its own to hand to WAM.
        return await ExecuteInteractiveAsync(app, AdoScopes, "ADO", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Acquire an ARM token (used to enumerate available tenants). Always uses
    /// the common authority so the result spans every tenant the user can see.
    /// </summary>
    public async Task<AuthenticationResult> AcquireArmTokenAsync(CancellationToken cancellationToken)
    {
        var app = await GetAppAsync(tenantId: null, cancellationToken).ConfigureAwait(false);
        var account = (await app.GetAccountsAsync().ConfigureAwait(false)).FirstOrDefault();

        if (account is not null)
        {
            try
            {
                return await app.AcquireTokenSilent(ArmScopes, account).ExecuteAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (MsalUiRequiredException) { /* fall through */ }
        }

        try
        {
            return await app.AcquireTokenSilent(ArmScopes, PublicClientApplication.OperatingSystemAccount)
                .ExecuteAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (MsalUiRequiredException) { /* fall through */ }
        catch (MsalException ex) { _logger.Info($"MSAL OS-account silent (ARM) failed: {ex.ErrorCode}"); }

        return (await ExecuteInteractiveAsync(app, ArmScopes, "ARM", cancellationToken).ConfigureAwait(false))!;
    }

    /// <summary>
    /// Wrap <see cref="AcquireTokenInteractiveParameterBuilder"/> with a
    /// generous timeout (the user might take a few minutes to sign in) and
    /// detailed logging so we can tell from the Output window whether the
    /// browser flow completed, was cancelled, or timed out.
    /// </summary>
    private async Task<AuthenticationResult?> ExecuteInteractiveAsync(
        IPublicClientApplication app,
        string[] scopes,
        string label,
        CancellationToken cancellationToken)
    {
        // The command-level CancellationToken can be cancelled while the
        // browser is still open (e.g. the user clicks elsewhere in VS), which
        // would silently abort sign-in. Detach from it and impose a 5-minute
        // timeout instead so the listener is reclaimed if the user walks away.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
        cts.CancelAfter(TimeSpan.FromMinutes(5));

        _logger.Info($"MSAL interactive ({label}) starting system-browser flow…");
        try
        {
            var r = await app.AcquireTokenInteractive(scopes)
                .WithUseEmbeddedWebView(false)
                .WithPrompt(Prompt.SelectAccount)
                .ExecuteAsync(cts.Token)
                .ConfigureAwait(false);
            _logger.Info($"MSAL interactive ({label}) succeeded for {r.Account?.Username}.");
            return r;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            _logger.Warn($"MSAL interactive ({label}) timed out after 5 minutes.");
            throw;
        }
        catch (MsalException ex)
        {
            _logger.Error($"MSAL interactive ({label}) failed: {ex.ErrorCode} - {ex.Message}", ex);
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error($"MSAL interactive ({label}) crashed", ex);
            throw;
        }
    }

    /// <summary>Sign out by removing every cached account from MSAL/WAM.</summary>
    public async Task SignOutAsync()
    {
        IPublicClientApplication[] apps;
        lock (_gate)
        {
            apps = new[] { _commonApp! }.Where(a => a is not null).Concat(_tenantApps.Values).ToArray();
        }
        foreach (var app in apps)
        {
            foreach (var account in await app.GetAccountsAsync().ConfigureAwait(false))
            {
                try
                {
                    await app.RemoveAsync(account).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.Warn($"MSAL RemoveAsync failed for {account.Username}: {ex.Message}");
                }
            }
        }
    }

    private async Task<IPublicClientApplication> GetAppAsync(string? tenantId, CancellationToken cancellationToken)
    {
        IPublicClientApplication app;
        bool justCreated = false;
        lock (_gate)
        {
            if (string.IsNullOrEmpty(tenantId))
            {
                if (_commonApp is null)
                {
                    _commonApp = Build("common");
                    justCreated = true;
                }
                app = _commonApp;
            }
            else if (_tenantApps.TryGetValue(tenantId!, out var existing))
            {
                app = existing;
            }
            else
            {
                app = Build(tenantId!);
                _tenantApps[tenantId!] = app;
                justCreated = true;
            }
        }

        if (justCreated)
        {
            await RegisterCacheAsync(app, cancellationToken).ConfigureAwait(false);
        }
        return app;
    }

    /// <summary>
    /// Hook the cross-platform DPAPI-protected token cache into a freshly built
    /// <see cref="IPublicClientApplication"/>. The same <see cref="MsalCacheHelper"/>
    /// is shared by every app instance so accounts persist across the
    /// "common" authority + every tenant-pinned authority.
    /// </summary>
    private async Task RegisterCacheAsync(IPublicClientApplication app, CancellationToken cancellationToken)
    {
        var helper = await GetCacheHelperAsync(cancellationToken).ConfigureAwait(false);
        if (helper is null)
        {
            return;
        }
        try
        {
            helper.RegisterCache(app.UserTokenCache);
        }
        catch (Exception ex)
        {
            _logger.Warn($"MSAL cache RegisterCache failed: {ex.Message}");
        }
    }

    private async Task<MsalCacheHelper?> GetCacheHelperAsync(CancellationToken cancellationToken)
    {
        if (_cacheHelper is not null)
        {
            return _cacheHelper;
        }
        await _cacheGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cacheHelper is not null)
            {
                return _cacheHelper;
            }
            try
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "PipelinesExplorer");
                Directory.CreateDirectory(dir);
                // StorageCreationProperties uses DPAPI on Windows by default
                // (see learn.microsoft.com/entra/msal/dotnet/how-to/token-cache-serialization?tabs=desktop).
                var props = new StorageCreationPropertiesBuilder("msalcache.bin", dir).Build();
                _cacheHelper = await MsalCacheHelper.CreateAsync(props).ConfigureAwait(false);
                return _cacheHelper;
            }
            catch (Exception ex)
            {
                _logger.Warn($"MSAL cache initialization failed; tokens will not persist: {ex.Message}");
                return null;
            }
        }
        finally
        {
            _cacheGate.Release();
        }
    }

    private IPublicClientApplication Build(string tenant)
    {
        // Out-of-process VS extensions don't own a top-level HWND, so we cannot
        // reliably parent the WAM dialog. We use the system browser flow which
        // needs a loopback redirect URI instead of the WAM "nativeclient" one.
        return PublicClientApplicationBuilder
            .Create(_clientId)
            .WithAuthority(AzureCloudInstance.AzurePublic, tenant)
            .WithDefaultRedirectUri()
            // Skip the legacy ADAL cache compatibility scan – big perf win,
            // see https://learn.microsoft.com/entra/msal/dotnet/advanced/high-availability#use-the-token-cache
            .WithLegacyCacheCompatibility(false)
            .Build();
    }
}

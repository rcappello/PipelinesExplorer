using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Broker;
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
        var app = GetApp(tenantId);
        var account = (await app.GetAccountsAsync().ConfigureAwait(false)).FirstOrDefault();

        if (account is not null)
        {
            try
            {
                return await app.AcquireTokenSilent(AdoScopes, account).ExecuteAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (MsalUiRequiredException ex)
            {
                _logger.Info($"MSAL silent failed ({ex.ErrorCode}); will {(createIfNone ? "prompt" : "skip")}.");
                if (!createIfNone)
                {
                    return null;
                }
            }
        }
        else if (!createIfNone)
        {
            return null;
        }

        return await app.AcquireTokenInteractive(AdoScopes)
            .WithUseEmbeddedWebView(false)
            .WithParentActivityOrWindow(() => (object)GetForegroundWindow())
            .ExecuteAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Acquire an ARM token (used to enumerate available tenants). Always uses
    /// the common authority so the result spans every tenant the user can see.
    /// </summary>
    public async Task<AuthenticationResult> AcquireArmTokenAsync(CancellationToken cancellationToken)
    {
        var app = GetApp(tenantId: null);
        var account = (await app.GetAccountsAsync().ConfigureAwait(false)).FirstOrDefault();

        if (account is not null)
        {
            try
            {
                return await app.AcquireTokenSilent(ArmScopes, account).ExecuteAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (MsalUiRequiredException)
            {
                // fall through to interactive
            }
        }

        return await app.AcquireTokenInteractive(ArmScopes)
            .WithUseEmbeddedWebView(false)
            .WithParentActivityOrWindow(() => (object)GetForegroundWindow())
            .ExecuteAsync(cancellationToken)
            .ConfigureAwait(false);
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

    private IPublicClientApplication GetApp(string? tenantId)
    {
        lock (_gate)
        {
            if (string.IsNullOrEmpty(tenantId))
            {
                return _commonApp ??= Build("common");
            }
            if (!_tenantApps.TryGetValue(tenantId!, out var app))
            {
                app = Build(tenantId!);
                _tenantApps[tenantId!] = app;
            }
            return app;
        }
    }

    private IPublicClientApplication Build(string tenant)
    {
        var builder = PublicClientApplicationBuilder
            .Create(_clientId)
            .WithAuthority(AzureCloudInstance.AzurePublic, tenant)
            .WithRedirectUri("https://login.microsoftonline.com/common/oauth2/nativeclient")
            .WithBroker(new BrokerOptions(BrokerOptions.OperatingSystems.Windows)
            {
                Title = "Pipelines Explorer (Visual Studio)",
                ListOperatingSystemAccounts = true,
            });

        return builder.Build();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}

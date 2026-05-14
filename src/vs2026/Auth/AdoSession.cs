namespace PipelinesExplorer.VisualStudio.Auth;

/// <summary>
/// Snapshot of the currently authenticated Azure DevOps session. Mirrors the
/// VS Code client's <c>AdoSession</c> shape.
/// </summary>
public sealed class AdoSession
{
    public AdoSession(SignInKind kind, string accessToken, string accountLabel, string? tenantId = null)
    {
        Kind = kind;
        AccessToken = accessToken;
        AccountLabel = accountLabel;
        TenantId = tenantId;
    }

    public SignInKind Kind { get; }

    public string AccessToken { get; }

    /// <summary>Best-effort display label for the account.</summary>
    public string AccountLabel { get; }

    /// <summary>Tenant id (Microsoft sign-in only).</summary>
    public string? TenantId { get; }
}

/// <summary>Microsoft Entra tenant the signed-in account has access to.</summary>
public sealed class TenantInfo
{
    public TenantInfo(string tenantId, string displayName, string? defaultDomain = null)
    {
        TenantId = tenantId;
        DisplayName = displayName;
        DefaultDomain = defaultDomain;
    }

    public string TenantId { get; }

    public string DisplayName { get; }

    public string? DefaultDomain { get; }
}

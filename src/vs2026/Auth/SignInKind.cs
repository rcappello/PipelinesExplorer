namespace PipelinesExplorer.VisualStudio.Auth;

/// <summary>Sign-in mechanism currently active.</summary>
public enum SignInKind
{
    /// <summary>Microsoft Entra ID (AAD) interactive sign-in via MSAL + WAM broker.</summary>
    Microsoft,

    /// <summary>Azure DevOps Personal Access Token stored in Windows Credential Manager.</summary>
    Pat,
}

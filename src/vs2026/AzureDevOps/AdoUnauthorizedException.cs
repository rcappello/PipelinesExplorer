using System;

namespace PipelinesExplorer.VisualStudio.AzureDevOps;

/// <summary>
/// Thrown when an Azure DevOps REST call fails with 401/403 or returns a non-JSON
/// payload (typically a redirect to the sign-in page). Mirrors
/// <c>AdoUnauthorizedError</c> in the VS Code client.
/// </summary>
public sealed class AdoUnauthorizedException : Exception
{
    public AdoUnauthorizedException(int status, string message)
        : base(message)
    {
        Status = status;
    }

    public int Status { get; }
}

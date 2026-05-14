using Microsoft.VisualStudio.Extensibility;

namespace PipelinesExplorer.VisualStudio;

/// <summary>
/// Entry point of the Pipelines Explorer Visual Studio 2026 extension.
/// Uses the out-of-process Microsoft.VisualStudio.Extensibility SDK.
/// </summary>
[VisualStudioContribution]
internal sealed class ExtensionEntrypoint : Extension
{
    /// <inheritdoc />
    public override ExtensionConfiguration ExtensionConfiguration => new()
    {
        Metadata = new(
            id: "PipelinesExplorer.VisualStudio.f6c47e2e-5b33-4d3a-8d79-2f5e1c1b9a01",
            version: this.ExtensionAssemblyVersion,
            publisherName: "rcappello",
            displayName: "Pipelines Explorer",
            description: "Browse Azure DevOps pipelines, drill into referenced YAML templates and PowerShell scripts, and jump to the local files in your solution."),
    };
}

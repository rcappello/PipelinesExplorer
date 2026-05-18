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
            publisherName: "Riccardo Cappello",
            displayName: "Pipelines Explorer",
            description: "Browse Azure DevOps pipelines, drill into referenced YAML templates and scripts (PowerShell, Bash, Cmd, Python, Azure CLI), and jump to the local files in your solution.")
        {
            // Visual Studio 2026 hosts out-of-process extensions on .NET 10. Declaring the target
            // here keeps the VisualStudio.Extensibility analyzer happy (VSEXT0010) and lets VS pick
            // the correct runtime when launching the extension host.
            DotnetTargetVersions = new[] { DotnetTarget.Custom("net10.0") },

            // Marketplace + Extension Manager icon (90x90+ PNG). Path is relative to the VSIX root
            // and the asset is included via the Content/IncludeInVSIX item in the .csproj.
            Icon = "Resources/Icon.png",
            PreviewImage = "Resources/Icon.png",

            // Extension Manager "More info" link and bundled license shown in the details pane.
            MoreInfo = "https://github.com/rcappello/PipelinesExplorer",
            License = "LICENSE",

            // Search hints surfaced in Extension Manager and on the Marketplace.
            Tags = new[] { "azure devops", "pipelines", "yaml", "ci/cd", "build" },

            // The new SDK defaults the packaged manifest to <Preview>true</Preview>; we publish a
            // production listing, not a preview build.
            Preview = false,
        },
    };
}

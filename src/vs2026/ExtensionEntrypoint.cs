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
            publisherName: "RiccardoCappello",
            displayName: "%PipelinesExplorer.Extension.DisplayName%",
            description: "%PipelinesExplorer.Extension.Description%")
        {
            // Visual Studio 2026 hosts out-of-process extensions on .NET 10. Declaring the target
            // here keeps the VisualStudio.Extensibility analyzer happy (VSEXT0010) and lets VS pick
            // the correct runtime when launching the extension host.
            DotnetTargetVersions = new[] { DotnetTarget.Custom("net10.0") },

            // Marketplace + Extension Manager icon (90x90+ PNG). Path is relative to the VSIX root
            // and the asset is included via the Content/IncludeInVSIX item in the .csproj.
            Icon = "Resources/Icon.png",
            PreviewImage = "Resources/Icon.png",

            // The new SDK defaults the packaged manifest to <Preview>true</Preview>; we publish a
            // production listing, not a preview build.
            Preview = false,
        },
    };
}

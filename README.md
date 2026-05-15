# Pipelines Explorer

> Browse Azure DevOps pipelines, drill into referenced YAML templates and scripts (PowerShell, Bash, Cmd, Python, Azure CLI), and jump to the local files in your editor — from VS Code **and** Visual Studio 2026.

This repository hosts two client implementations of the same Pipelines Explorer experience, sharing a common Azure DevOps backend model and Microsoft Entra ID + PAT authentication design.

## Clients

| Client | Path | Tech | Status |
| --- | --- | --- | --- |
| **VS Code extension** | [`src/vscode/`](src/vscode/) | TypeScript, esbuild, `vsce` | Stable — see [`src/vscode/README.md`](src/vscode/README.md) |
| **Visual Studio 2026 extension** | [`src/vs2026/`](src/vs2026/) | C# / .NET 8 / WPF, Microsoft.VisualStudio.Extensibility SDK | Scaffold — see [`src/vs2026/README.md`](src/vs2026/README.md) |

Both clients ship from this repo independently, with their own build pipelines, releases and CHANGELOG entries.

## Repository layout

```
PipelinesExplorer/
├── src/
│   ├── vscode/      # VS Code extension (npm-based, packaged as .vsix)
│   └── vs2026/      # Visual Studio 2026 extension (dotnet build, .vsix)
├── docs/            # Cross-client documentation
├── .github/
│   └── workflows/
│       ├── vscode.yml          # Path-filtered build CI for src/vscode
│       ├── vs2026.yml          # Path-filtered build CI for src/vs2026
│       └── release-vscode.yml  # Tag-triggered release for the VS Code client
├── CHANGELOG.md     # Aggregate changelog (entries prefixed [vscode] / [vs2026])
└── PipelinesExplorer.code-workspace   # Multi-root workspace for VS Code
```

## Working in the repo

Open the multi-root workspace for the best experience:

```powershell
code PipelinesExplorer.code-workspace
```

### VS Code client

```powershell
cd src/vscode
npm ci
npm run package        # type-check + lint + production bundle
npx @vscode/vsce package
```

### Visual Studio 2026 client

```powershell
cd src/vs2026
dotnet build PipelinesExplorer.VisualStudio.csproj -c Release
```

The produced `.vsix` lands under `src/vs2026/bin/Release/`.

## Versioning & releases

- The two clients version **independently**.
- Tag conventions:
  - `vscode-vX.Y.Z` → triggers `release-vscode.yml`, publishes the VS Code `.vsix`.
  - `vs2026-vX.Y.Z` → reserved for the future VS 2026 release workflow.
- CHANGELOG entries are prefixed with `[vscode]` or `[vs2026]` so the aggregate file stays readable.

## License

[MIT](LICENSE)

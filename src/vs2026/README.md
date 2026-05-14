# Pipelines Explorer — Visual Studio 2026 client

> ⚠️ **Scaffold only.** This client is the early skeleton of the Visual Studio 2026 port of [Pipelines Explorer](../../README.md). The VS Code client under [`../vscode/`](../vscode/) is the reference implementation.

## Stack

| Concern | Choice |
| --- | --- |
| Target | Visual Studio 2026 (`Microsoft.VisualStudio.{Community,Pro,Enterprise} [18.0,)`) |
| SDK | [Microsoft.VisualStudio.Extensibility](https://learn.microsoft.com/visualstudio/extensibility/visualstudio.extensibility/) (out-of-process) |
| Runtime | `.NET 8` (`net8.0-windows`), WPF |
| Auth (planned) | MSAL with WAM broker (`Microsoft.Identity.Client.Broker`) |
| Azure DevOps client | `Microsoft.TeamFoundationServer.Client` + `Microsoft.VisualStudio.Services.Client` |

## Build

```powershell
cd src/vs2026
dotnet restore
dotnet build PipelinesExplorer.VisualStudio.csproj -c Release
```

The produced `.vsix` lands under `bin/Release/`. Double-click it (with VS 2026 closed) to install, or use `VSIXInstaller.exe`.

## Layout

```
src/vs2026/
├── PipelinesExplorer.VisualStudio.csproj
├── source.extension.vsixmanifest
├── ExtensionEntrypoint.cs
└── Commands/
    └── SignInWithMicrosoftCommand.cs   # placeholder
```

## Roadmap (parity with the VS Code client)

1. Microsoft Entra ID sign-in via MSAL + WAM broker, plus PAT fallback (stored in Windows Credential Manager).
2. Tool window with the same `Org → Project → Repo → Pipeline → Templates / PowerShell scripts` tree.
3. Recursive YAML analysis (`template:` references + `PowerShell@2` / `AzurePowerShell@5` / `AzureCLI@2` tasks).
4. Workspace-folder linking and "open the local file" navigation.
5. Tenant picker driven by ARM `https://management.azure.com/tenants?api-version=2022-12-01`.

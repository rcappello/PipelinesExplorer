# Pipelines Explorer — Visual Studio 2026 client

Visual Studio 2026 port of [Pipelines Explorer](../../README.md) — feature parity with the [VS Code client](../vscode/) for browsing Azure DevOps pipelines and jumping into the underlying YAML / PowerShell.

## What it does

- Sign in to one or more Azure DevOps organizations
  - **Microsoft Entra ID** via MSAL with the WAM broker (`Microsoft.Identity.Client.Broker`).
  - **Personal Access Token** fallback (Build Read + Code Read), persisted in Windows Credential Manager.
  - Tenant picker driven by `https://management.azure.com/tenants?api-version=2022-12-01`.
- Tool window with a hierarchical tree:
  - `Organization → Project → Repository → Pipeline → Templates / PowerShell scripts`.
  - Same-repo template references are recursively analysed and expanded.
  - Templates from external repository resources show as leaves (with the `@alias`).
- Right-click context menu:
  - **Open** on Pipeline / Template / Script — opens the file in Visual Studio when the workspace is linked, falls back to "Re-link / Open in browser" otherwise.
  - **Link / Unlink workspace folder** on Repository — uses the WPF `OpenFolderDialog`.
  - **Select branch…** on Repository — pick the branch the YAML is read from (or "use default branch").

## Stack

| Concern | Choice |
| --- | --- |
| Target | Visual Studio 2026 (`Microsoft.VisualStudio.{Community,Pro,Enterprise} [18.0,)`) |
| SDK | [Microsoft.VisualStudio.Extensibility 17.13](https://learn.microsoft.com/visualstudio/extensibility/visualstudio.extensibility/) (out-of-process) |
| Runtime | `.NET 8` (`net8.0-windows`), WPF Remote UI |
| Auth | MSAL 4.66 + WAM broker, ADO PAT |
| YAML | `YamlDotNet 15.1` (`RepresentationModel`) |
| State | JSON file under `%LocalAppData%\PipelinesExplorer\` |

## Build

```powershell
cd src\vs2026
dotnet restore
dotnet build PipelinesExplorer.VisualStudio.csproj -c Release
```

The produced `.vsix` lands under `bin\Release\net8.0-windows\`. Double-click it (with VS 2026 closed) or use `VSIXInstaller.exe`.

## Run / debug

The project is set up for the standard Visual Studio Extensibility experimental-instance debug experience. From Visual Studio: open the solution, press <kbd>F5</kbd> — VS launches an experimental instance with the extension deployed. From CLI:

```powershell
dotnet build src\vs2026\PipelinesExplorer.VisualStudio.csproj -c Debug
# install into the experimental hive, then launch VS:
& "$env:ProgramFiles\Microsoft Visual Studio\2026\Community\Common7\IDE\VSIXInstaller.exe" `
    /rootSuffix:Exp src\vs2026\bin\Debug\net8.0-windows\PipelinesExplorer.VisualStudio.vsix
```

Open the tool window from **View → Other Windows → Pipelines Explorer**.

## Layout

```
src/vs2026/
├── PipelinesExplorer.VisualStudio.csproj
├── source.extension.vsixmanifest
├── ExtensionEntrypoint.cs
├── ExtensionServices.cs            # composition root (lazy singletons)
├── Auth/                           # MSAL, PAT, tenant picker, credential storage
├── AzureDevOps/                    # AdoClient + REST DTOs
├── Commands/                       # 8 menu commands (sign in/out, refresh, reset, …)
├── Services/
│   ├── PipelineYamlAnalyzer.cs     # template + PowerShell task extraction (YamlDotNet)
│   ├── OpenItemService.cs          # resolves repo paths to local files, opens in VS
│   ├── WorkspaceLinkService.cs     # repo ↔ folder mapping
│   ├── RepoBranchService.cs        # per-repo branch override
│   └── JsonStateStore.cs           # persistent state
├── ToolWindows/
│   ├── PipelinesToolWindow.cs
│   ├── PipelinesToolWindowControl.cs
│   └── PipelinesToolWindowControl.xaml
└── ViewModels/
    ├── PipelinesViewModel.cs       # tree population, lazy expansion, command wiring
    └── TreeNodeViewModel.cs        # Org/Project/Repo/Pipeline/Group/Template/Script nodes
```

## Notes / known limitations

- `OpenTarget.SelectionLine` is captured by the analyzer but not yet applied when opening — `DocumentsExtensibility.OpenDocumentAsync` returns a `DocumentSnapshot` but does not expose a selection-positioning API in 17.13.
- No free-form text input prompt exists in the 17.13 Shell API, so PAT entry happens inline in the tool window (not in a modal dialog).
- Localization (`.resx`) and additional UI polish (per-kind icons, keyboard shortcuts) are tracked separately.

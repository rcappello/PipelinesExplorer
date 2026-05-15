# Pipelines Explorer — Visual Studio 2026 client

Visual Studio 2026 port of [Pipelines Explorer](../../README.md) — feature parity with the [VS Code client](../vscode/) for browsing Azure DevOps pipelines and jumping into the underlying YAML / scripts.

## What it does

- Sign in to one or more Azure DevOps organizations
  - **Microsoft Entra ID** via MSAL with the WAM broker (`Microsoft.Identity.Client.Broker`).
  - **Personal Access Token** fallback (Build Read + Code Read), persisted in Windows Credential Manager.
  - Tenant picker driven by `https://management.azure.com/tenants?api-version=2022-12-01`.
- Tool window with a hierarchical tree:
  - `Organization → Project → Repository → Pipeline → Templates / Scripts`.
  - Same-repo template references are recursively analysed and expanded.
  - Templates from external repository resources show as leaves (with the `@alias`).
  - Scripts include PowerShell, Bash, Cmd/Batch, Python and Azure CLI tasks (file or inline), plus the shorthand step keys `script:`, `bash:`, `pwsh:`, `powershell:`.
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
| Runtime | `.NET 10` (`net10.0-windows`), WPF Remote UI |
| Auth | MSAL 4.66 + WAM broker, ADO PAT |
| YAML | `YamlDotNet 15.1` (`RepresentationModel`) |
| State | JSON file under `%LocalAppData%\PipelinesExplorer\` |

## Build

```powershell
cd src\vs2026
dotnet restore
dotnet build PipelinesExplorer.VisualStudio.csproj -c Release
```

The produced `.vsix` lands under `bin\Release\net10.0-windows\`. Double-click it (with VS 2026 closed) or use `VSIXInstaller.exe`.

## Run / debug

The project is set up for the standard Visual Studio Extensibility experimental-instance debug experience. From Visual Studio: open the solution, press <kbd>F5</kbd> — VS launches an experimental instance with the extension deployed. From CLI:

```powershell
dotnet build src\vs2026\PipelinesExplorer.VisualStudio.csproj -c Debug
# install into the experimental hive, then launch VS:
& "$env:ProgramFiles\Microsoft Visual Studio\2026\Community\Common7\IDE\VSIXInstaller.exe" `
    /rootSuffix:Exp src\vs2026\bin\Debug\net10.0-windows\PipelinesExplorer.VisualStudio.vsix
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
│   ├── PipelineYamlAnalyzer.cs     # template + script task extraction (YamlDotNet)
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

## Contributing & local testing

### Localization

The extension ships with English (default) plus Italian, French, German, Spanish and Swedish. Translations live in two places:

| Layer | Files | Loaded by |
| --- | --- | --- |
| **Runtime UI** (XAML, ViewModels, Services) | [`Resources/Strings.resx`](Resources/Strings.resx) + `Strings.{it,fr,de,es,sv}.resx` → satellite assemblies under `bin\Debug\net10.0-windows\{locale}\PipelinesExplorer.VisualStudio.resources.dll` | `ResourceManager` resolved against `CultureInfo.CurrentUICulture` via the hand-written [`Resources/Strings.cs`](Resources/Strings.cs) accessor |
| **Command/extension metadata** (display names, descriptions) | [`.vsextension/string-resources.json`](.vsextension/string-resources.json) (default) + `.vsextension/{locale}/string-resources.json` | The Visual Studio Extensibility runtime resolves `%Key%` placeholders in `CommandConfiguration` / `ExtensionConfiguration.Metadata` against the JSON for the current VS UI culture |

The brand name **"Pipelines Explorer"** is intentionally not translated — it lives in the [`Branding.ProductName`](Branding.cs) constant (used for the tool window title and the output channel name) and is repeated as-is in every locale's `string-resources.json`.

#### Testing a specific language

Visual Studio exposes its UI culture via the `/LCID` command-line switch. Launch the experimental hive in the locale you want to test:

```powershell
# Build first (CLI; F5 from VS also works for English)
dotnet build src\vs2026\PipelinesExplorer.VisualStudio.csproj -c Debug

# Then launch the Experimental hive in the target language
$devenv = "$env:ProgramFiles\Microsoft Visual Studio\2026\Community\Common7\IDE\devenv.exe"
& $devenv /rootSuffix Exp /LCID 1040    # Italian
```

| Locale | LCID | `string-resources.json` folder |
| --- | --- | --- |
| English (default) | 1033 | `.vsextension\` (root) |
| Italian | 1040 | `.vsextension\it\` |
| French | 1036 | `.vsextension\fr\` |
| German | 1031 | `.vsextension\de\` |
| Spanish | 3082 | `.vsextension\es\` |
| Swedish | 1053 | `.vsextension\sv\` |

> Visual Studio falls back to English for any UI culture not listed above (e.g. `/LCID 1041` Japanese).

#### Adding a new locale

1. Copy [`Resources/Strings.resx`](Resources/Strings.resx) to `Resources/Strings.{culture}.resx` and translate every `<value>`. Keep the brand "Pipelines Explorer" untranslated and preserve `{0}`, `{1}` placeholders verbatim.
2. Copy [`.vsextension/string-resources.json`](.vsextension/string-resources.json) to `.vsextension/{culture}/string-resources.json` and translate every value (again leaving `Pipelines Explorer` as-is).
3. Rebuild and verify a satellite assembly appears at `bin\Debug\net10.0-windows\{culture}\PipelinesExplorer.VisualStudio.resources.dll` and the JSON at `bin\Debug\net10.0-windows\.vsextension\{culture}\string-resources.json`.

The .NET SDK auto-includes any `*.resx` under the project as embedded resources — no `csproj` edit required. The Visual Studio Extensibility build target copies the entire `.vsextension\` tree (including locale subfolders) into the VSIX automatically.

### Accessibility checks

When changing XAML, keep the project compatible with the [VS Code accessibility guidelines](https://code.visualstudio.com/docs/configure/accessibility/accessibility) adapted to WPF Remote UI:

- Every icon-only `Button` must have `AutomationProperties.Name` (and ideally `AutomationProperties.HelpText` for the tooltip text).
- Inputs that have no associated `<Label>` (e.g. the masked PAT `TextBox`, the tenant `ComboBox`) need an explicit `AutomationProperties.Name`.
- All container controls users navigate (e.g. the main `TreeView`) must have `AutomationProperties.Name`.
- All A11y names live in `Strings.resx` under the `A11y_*` prefix so they are translatable.
- Use VS theme brushes (`{DynamicResource {x:Static vsui:EnvironmentColors.*Key}}` / `{x:Static vsui:CommonControlsColors.*Key}`) so the UI follows Light / Dark / Blue / High-Contrast themes automatically. Never hard-code colors.
- Tab order should follow visual order; rely on default WPF `KeyboardNavigation.TabNavigation="Continue"` unless you have a reason to override it.

A quick smoke test: open the tool window, hit <kbd>Tab</kbd> from the search-anywhere box and confirm focus reaches every interactive control with a meaningful announcement (Narrator: <kbd>Win</kbd>+<kbd>Ctrl</kbd>+<kbd>Enter</kbd>).

## Notes / known limitations

- `OpenTarget.SelectionLine` is captured by the analyzer but not yet applied when opening — `DocumentsExtensibility.OpenDocumentAsync` returns a `DocumentSnapshot` but does not expose a selection-positioning API in 17.14.
- No free-form text input prompt exists in the 17.14 Shell API, so PAT entry happens inline in the tool window (not in a modal dialog).
- Additional UI polish (per-kind icons, keyboard shortcuts) is tracked separately.

# Pipelines Explorer — Visual Studio 2026 client

> Browse Azure DevOps pipelines, drill into referenced YAML templates and scripts (PowerShell, Bash, Cmd, Python, Azure CLI), and jump to the local files in your loaded solution — from a dedicated Visual Studio 2026 tool window.

**Pipelines Explorer** brings your Azure DevOps pipelines into a Visual Studio 2026 tool window.
Navigate organizations → projects → repositories → pipelines, then expand each pipeline
to inspect every YAML template and script task it references — recursively. Link a
local repository folder to the matching tree node and a single click opens the actual
file inside Visual Studio.

> *Stop hopping between browser tabs to read your pipelines.*

This is the Visual Studio 2026 port of [Pipelines Explorer](../../README.md), with feature parity to the [VS Code client](../vscode/).

## Features

- 🔐 **Two sign-in modes** — Microsoft Entra ID single sign-on (MSAL + WAM broker, same account picker Visual Studio uses) or a classic Azure DevOps Personal Access Token. When signed in with Microsoft, a top-of-tree row shows the active account and tenant, and a toolbar button opens a tenant picker populated with the tenants your account can access. Expired or revoked tokens are detected automatically: the extension clears the stored credential and prompts you to sign in again.
- 🌳 **Org → Project → Repository → Pipeline** tree, with friendly empty states (`No pipelines in this project`, missing-permission warnings, etc.).
- 🔍 **Recursive YAML analysis** — every `template:` reference and every script-running task is surfaced under each pipeline. Recognised tasks include `PowerShell@2`, `AzurePowerShell@5`, `PowerShellOnTargetMachines@3`, `Bash@3`, `ShellScript@2`, `CmdLine@2`, `BatchScript@1`, `AzureCLI@2`, `PythonScript@0`, plus the shorthand step keys `script:`, `bash:`, `pwsh:`, `powershell:`. Same-repo templates can be expanded to reveal *their* templates and scripts. Empty groups are hidden.
- 🔗 **Link a workspace folder to a repository** and click any pipeline / template / script to open the local file inside Visual Studio. Pipeline variables like `$(System.DefaultWorkingDirectory)` and `$(Build.SourcesDirectory)`, repo-absolute paths and relative `../` segments are all resolved automatically.
- � **Filter by name** — type in the filter box at the top of the tool window to shrink the tree to pipelines, templates and scripts whose name contains a substring. The scan is debounced (~200 ms), covers every organization / project / repository the signed-in identity can see, and recurses into same-repo templates so matches deep in the nested graph still surface their owning pipeline.
- �📋 **Filename-first labels** with full repo paths in the tooltip, so the tree stays readable on long deployment templates.

## Tree at a glance

```
Pipelines
└── My Organization
    └── My Project
        └── Project-Repo · 32 · azureReposGit · linked
            ├── Build & Deploy
            │   ├── Templates (3)
            │   │   ├── deployment.yml          (tooltip: solutions/foo/.ci/deployment.yml)
            │   │   │   ├── Templates (2)
            │   │   │   │   ├── prepare-parameters.yml
            │   │   │   │   └── deploy-infrastructure.yml
            │   │   │   └── Scripts (1)
            │   │   │       └── Get-WorkloadPath.ps1     PowerShell@2
            │   │   ├── build.yml
            │   │   └── lint.yml
            │   └── Scripts (4)
            │       ├── New-EntraIdWorkload.ps1   AzurePowerShell@5
            │       ├── deploy.sh                 Bash@3
            │       ├── (inline script)           bash
            │       └── (inline script)           PowerShell@2
            └── …
```

## Screenshot

![Pipelines Explorer in Visual Studio 2026](https://raw.githubusercontent.com/rcappello/PipelinesExplorer/main/docs/screenshots/vs2026-screenshot.png)

### Filter

![Filter Pipelines Explorer in Visual Studio 2026](https://raw.githubusercontent.com/rcappello/PipelinesExplorer/main/docs/screenshots/vs2026-search.png)

## Getting started

1. Install the `.vsix` (double-click with VS 2026 closed, or use `VSIXInstaller.exe`):
   ```powershell
   & "$env:ProgramFiles\Microsoft Visual Studio\2026\Community\Common7\IDE\VSIXInstaller.exe" `
       PipelinesExplorer.VisualStudio.vsix
   ```
   (Or, once published, install from the Marketplace.)
2. Restart Visual Studio 2026 and open **View → Other Windows → Pipelines Explorer**.
3. Choose a sign-in method:
   - **Sign in with Microsoft** — uses MSAL with the Windows WAM broker. Recommended for organizations connected to Microsoft Entra ID.
   - **Sign in with Personal Access Token** — paste a PAT with at least `Code (Read)`, `Build (Read)` and `Project and Team (Read)` scopes, and **`All accessible organizations`** as *Organizations* scope so the tree can enumerate every org your account can reach. The PAT is persisted in Windows Credential Manager. See [PAT scope and the 1 Dec 2026 deprecation](#pat-scope-and-the-1-dec-2026-deprecation) below before you generate one.
4. Browse organizations → projects → repositories → pipelines.

A header row at the top of the tree shows the active connection (account name and, for Microsoft sign-in, the current tenant). Clicking the row — or the **organization** icon next to **Refresh** in the tool window toolbar — opens a tenant picker listing the Entra ID tenants your account belongs to, so you can switch tenant without signing out. The choice is persisted across restarts; **Reset all settings** clears it.

### Linking a local clone

If you already have a local clone of one of your repositories open in Visual Studio:

1. Right-click the repository node and choose **Link workspace folder…**.
2. Pick the folder that contains the clone (uses the WPF `OpenFolderDialog`).
3. Single-click any pipeline / template / `*.ps1` underneath that repo to open the file in Visual Studio.

If a referenced file isn't found in the linked folder (e.g. it's on a different branch), the extension shows a warning with **Re-link workspace folder** and **Open in browser** options. The warning also tells you which branch the YAML was read from on Azure DevOps.

To remove a link: right-click the repository → **Unlink workspace folder**.

### Choosing a branch (per repository)

By default Pipelines Explorer reads pipeline YAML, templates and scripts from each repository's **default branch** on Azure DevOps. You can override the branch on a per-repository basis:

1. Right-click a repository node → **Select branch…**.
2. Pick a branch from the list, or choose **Use default branch** to clear the override.
3. The tree refreshes and reads YAML from the chosen branch. The repository node shows `· branch: <name>` while an override is active.

## Commands

All commands live under **Tools → Pipelines Explorer**, and most also have a counterpart on the tool window toolbar or context menu.

| Command | Description |
| --- | --- |
| `Pipelines Explorer` | Open the **Pipelines Explorer** tool window. |
| `Pipelines Explorer: Sign in with Microsoft` | Sign in with Microsoft Entra ID via MSAL + WAM broker. |
| `Pipelines Explorer: Sign in with PAT` | Sign in with an Azure DevOps Personal Access Token (asked on first use). |
| `Pipelines Explorer: Select Microsoft tenant` | Pick an Entra ID tenant (from the list of tenants your account can access) to scope the Azure DevOps sign-in. Available only with Microsoft sign-in. |
| `Pipelines Explorer: Sign out` | Clear the active session. |
| `Pipelines Explorer: Reset all settings` | Wipe the stored PAT, the chosen sign-in method, the tenant selection and per-repository branch overrides. |
| `Pipelines Explorer: Refresh` | Re-fetch the tree and re-analyse pipelines. |
| `Pipelines Explorer: Show logs` | Open the extension output pane. |
| Link / Unlink workspace folder | Available from the repository node context menu. |
| Select branch… | Available from the repository node context menu. Overrides the branch from which YAML is read. |

## Languages

The brand prefix **Pipelines Explorer** is preserved in every locale (untranslated).

| Locale | Status |
| --- | --- |
| English (`en`) | Stable, source language. |
| Italian (`it`) | Stable, author quality. |
| French (`fr`) | **Preview** — machine-translated, awaiting native review. |
| German (`de`) | **Preview** — machine-translated, awaiting native review. |
| Spanish (`es`) | **Preview** — machine-translated, awaiting native review. |
| Swedish (`sv`) | **Preview** — machine-translated, awaiting native review. |

Runtime UI strings live in [`Resources/Strings.resx`](Resources/Strings.resx) (default) plus one `Strings.<lang>.resx` per locale; command and extension metadata live in [`.vsextension/string-resources.json`](.vsextension/string-resources.json) plus one `<lang>/string-resources.json` per locale. Visual Studio picks the locale from its own display language — see [Testing a specific language](#testing-a-specific-language) below to force a culture from the command line. To improve a translation, open a PR editing the relevant files.

## Accessibility

Pipelines Explorer follows the
[VS Code accessibility guidelines](https://code.visualstudio.com/docs/configure/accessibility/accessibility),
adapted to WPF Remote UI:

- **Keyboard navigation** — every action is reachable via the Tools menu
  (`Tools → Pipelines Explorer: …`), the toolbar, and context menu commands.
  Tab order follows visual order; the tree supports the standard WPF
  `TreeView` keyboard model. No mouse-only interactions.
- **Screen reader support** — every interactive control exposes an
  `AutomationProperties.Name`. The tree summarises each node with a
  localized announcement (e.g. *"Repository foo, 12 pipelines, linked to
  local folder, branch override main"*). Tested with Narrator
  (<kbd>Win</kbd>+<kbd>Ctrl</kbd>+<kbd>Enter</kbd>).
- **No color-only signals** — node states (linked, branch override,
  warnings) are conveyed through icons and text labels, never color alone.
- **High contrast & themes** — all colors come from VS theme brushes
  (`EnvironmentColors`, `CommonControlsColors`), so the UI follows the
  active Light / Dark / Blue / High Contrast theme automatically.
- **Localized announcements** — accessibility labels live in
  `Strings.resx` under the `A11y_*` prefix and are translated through the
  same satellite assemblies as the rest of the UI.

## PAT scope and the 1 Dec 2026 deprecation

Pipelines Explorer supports **two** shapes of Azure DevOps Personal Access
Token and picks the right flow automatically:

- **All accessible organizations** (also called a *global PAT*) — the historical
  path. On sign-in the extension calls
  `https://app.vssps.visualstudio.com/_apis/accounts` to enumerate every
  organization your account can reach and shows them as roots in the tree.
- **Organization-scoped PAT** — a PAT whose *Organizations* dropdown was
  narrowed to a single organization at creation time. `_apis/accounts`
  returns an empty list for these tokens by design, so on sign-in the
  extension opens an inline *Add Azure DevOps organization* panel in the
  tool window with the just-entered token pre-filled and asks you for the
  organization name once. **Type the exact organization identifier as it
  appears in your Azure DevOps URL** — the segment right after
  `dev.azure.com/` (e.g. `contoso` in `https://dev.azure.com/contoso/`), or
  the subdomain before `.visualstudio.com` on legacy URLs. It is
  case-insensitive but must otherwise match exactly; typos, project names
  or friendly display names will not resolve. The extension validates the
  pair against `dev.azure.com/{org}/_apis/projects?$top=1` and, if the
  token is not authorized for that org (or the org does not exist),
  surfaces an inline error inside the panel with *Verify & add* / *Cancel*
  actions rather than a broken slot. On success the pair is stored in
  Windows Credential Manager under a per-org target
  (`PipelinesExplorer.VisualStudio:AzureDevOpsPAT/{canonical-org}`). Layer
  more per-organization PATs on top later via the **Pipelines Explorer:
  Add Azure DevOps organization…** command in the Tools menu, which reopens
  the same panel.

### Why the extension has to ask you for the organization name

Azure DevOps PATs are opaque strings, not JWTs, and Microsoft does not expose
any client-facing endpoint that returns the owning organization from a
scoped token. This was verified empirically on 2026-07-07 against every
candidate path: `_apis/accounts?memberId=` returns `count: 0`,
`_apis/ConnectionData` (SPS-level) responds `401 Unauthorized`,
`_apis/tokens/pats` requires the organization already in the URL, and
`_apis/profile/profiles/me` only carries the identity. There is no client-
side reverse lookup; the name has to come from you the first time. After
that it is remembered for the duration of the sign-in.

### Global PAT retirement

Microsoft has announced the retirement of global PATs for Azure DevOps
Services on **1 December 2026** ([aka.ms/GlobalPATDeprecation](https://aka.ms/GlobalPATDeprecation)).
On that date every existing global PAT stops working and new ones can no
longer be created. Azure DevOps *Server* is not affected.

What this means for Pipelines Explorer:

- **Until 1 Dec 2026** — both PAT shapes work; keep using *All accessible
  organizations* if you want the single-token enumeration path and are aware
  that the underlying endpoint is already best-effort ([plan 002 §1.1](https://github.com/rcappello/PipelinesExplorer/blob/main/.specify/plans/002-pat-per-org-fallback.md)).
- **Recommended today** — use **Sign in with Microsoft**. Entra-backed
  sign-in is the durable path and is unaffected by the retirement.
- **After 1 Dec 2026** — PAT sign-in continues to work in per-organization
  mode only. The extension already routes every request to the right
  per-org token so no UX regression is expected — you will just add one
  organization at a time.

## Requirements

- Visual Studio 2026 (Community, Professional or Enterprise — `[18.0,)`).
- An Azure DevOps account with read access to the organizations / projects / pipelines you want to browse.
- (Optional) Local clones of the repositories whose files you want to open.

## Known limitations

- **YAML is read from a single branch per repository on Azure DevOps** — the default branch, or the branch chosen via **Select branch…** on the repository node. The tree (templates, scripts, line numbers) reflects that branch. If your local clone is on a different branch, opening a script may fail with *"File not found in linked workspace"*; either change the override (right-click the repo → **Select branch…**) or align the local clone (`git switch` / `git pull`).
- Cross-repo template references (`template: file.yml@otherRepo`) are shown as leaves in the tree (no recursion). They can still be opened locally if a workspace folder has been linked to a repo with the same alias.
- Only `azureReposGit` repositories are inspected for YAML content. GitHub-hosted pipeline definitions are listed but not expanded.
- Pipeline variables other than `System.DefaultWorkingDirectory`, `Build.SourcesDirectory`, `Pipeline.Workspace` and `Agent.BuildDirectory` are not interpolated when resolving local file paths.
- Opening a referenced YAML / script jumps to the file but does not yet position the cursor on the referenced line — `OpenTarget.SelectionLine` is captured by the analyzer but `DocumentsExtensibility.OpenDocumentAsync` does not expose a selection-positioning API in 17.14.
- PAT entry happens inline in the tool window (no free-form modal text prompt exists in the 17.14 Shell API).

## Telemetry

This extension does **not** collect telemetry.

## Contributing & local testing

This section is for developers building, debugging or extending the extension.

### Stack

| Concern | Choice |
| --- | --- |
| Target | Visual Studio 2026 (`Microsoft.VisualStudio.{Community,Pro,Enterprise} [18.0,)`) |
| SDK | [Microsoft.VisualStudio.Extensibility 17.13](https://learn.microsoft.com/visualstudio/extensibility/visualstudio.extensibility/) (out-of-process) |
| Runtime | `.NET 10` (`net10.0-windows`), WPF Remote UI |
| Auth | MSAL 4.66 + WAM broker, ADO PAT |
| YAML | `YamlDotNet 15.1` (`RepresentationModel`) |
| State | JSON file under `%LocalAppData%\PipelinesExplorer\` |

### Build

```powershell
cd src\vs2026
dotnet restore
dotnet build PipelinesExplorer.VisualStudio.csproj -c Release
```

The produced `.vsix` lands under `bin\Release\net10.0-windows\`. Double-click it (with VS 2026 closed) or use `VSIXInstaller.exe`.

### Run / debug

The project is set up for the standard Visual Studio Extensibility experimental-instance debug experience. From Visual Studio: open the solution, press <kbd>F5</kbd> — VS launches an experimental instance with the extension deployed. From CLI:

```powershell
dotnet build src\vs2026\PipelinesExplorer.VisualStudio.csproj -c Debug
# install into the experimental hive, then launch VS:
& "$env:ProgramFiles\Microsoft Visual Studio\2026\Community\Common7\IDE\VSIXInstaller.exe" `
    /rootSuffix:Exp src\vs2026\bin\Debug\net10.0-windows\PipelinesExplorer.VisualStudio.vsix
```

Open the tool window from **View → Other Windows → Pipelines Explorer**.

### Layout

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

## License

[MIT](LICENSE) © Riccardo Cappello

# Changelog

All notable changes to **Pipelines Explorer for Visual Studio 2026** are documented in this file.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and
this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

> The VS Code client has its own changelog under [`src/vscode/CHANGELOG.md`](../vscode/CHANGELOG.md).

## [Unreleased]

## [0.3.2] - 2026-07-02

### Changed

- Group headers under the tree filter now show the visible / total count (e.g. `Templates 4/7`) when the filter hides some items, and revert to the plain total (`Templates 7`) when the filter is cleared or when every item in the group still matches.

### Security

- Override the transitive `MessagePack` dependency (pulled in at `2.5.198` by `Microsoft.VisualStudio.Extensibility.Sdk 17.14`) with an explicit `PackageReference` on `MessagePack` / `MessagePack.Annotations` `2.5.302`. Clears the two GitHub Advisory database findings ([GHSA-vh6j-jc39-fggf](https://github.com/advisories/GHSA-vh6j-jc39-fggf) — `MessagePackReader.Skip` unbounded recursion, and [GHSA-hv8m-jj95-wg3x](https://github.com/advisories/GHSA-hv8m-jj95-wg3x) — LZ4 decompression out-of-bounds read) along with the eight moderate `NU1902` advisories that `dotnet restore` reported on the same package. Kept on the `2.5.x` patch line to preserve binary compatibility with the Extensibility SDK's own use of MessagePack.

## [0.3.1] - 2026-07-02

### Fixed

- Filter pruning was incomplete when the user manually expanded a pipeline or a template after the scan: newly materialised children (nested `Templates` group, non-matching sibling templates, entire `Scripts` group) defaulted to visible and stayed visible. Reapplying visibility after lazy-load now hides non-matching siblings anywhere in the subtree, matching the VS Code client. `InfoNode` warnings/status messages ride along with the parent's decision.
- Filter pruning had two remaining gaps that could leave a matched pipeline showing an empty `Templates` group or a stale `Scripts` group depending on whether the user expanded a pipeline before, during or after the scan. The scan now (1) records deferred group-visibility marks so `BuildAnalysisChildren` can honour them even when the group nodes did not exist at scan time, and (2) tracks every intermediate template on a path to a nested match so the intermediate template surfaces without the user having to guess which one to drill into.
- Filter pruning still left the `Scripts` group visible on some pipelines even though every script inside it correctly resolved to `IsVisibleUnderFilter=false`. Root cause was an ordering race in the Remote UI channel: `BuildAnalysisChildren` published the new children with the default visibility (`true`) via `ReplaceList`, then immediately fired `PropertyChanged(false)` on the freshly-added group — the property change was dropped by the WPF-side binding because the container had not yet been materialised for the just-added item. Visibility is now applied to the local `children` list **before** it is handed to `ReplaceList`, so the initial snapshot sent to the client already carries the correct value and no post-add property change is needed.

### Documentation

- Note in the README that PAT sign-in currently requires the *All accessible organizations* scope, and that Azure DevOps global PATs are retired on 1 December 2026 ([aka.ms/GlobalPATDeprecation](https://aka.ms/GlobalPATDeprecation)). Microsoft sign-in is recommended as the durable path; multi-org PAT support after the retirement is tracked as plan 002.

## [0.3.0] - 2026-07-02

### Added

- **Filter by name** — a filter box in the tool window header restricts the tree to pipelines, templates and scripts whose name contains a substring (case-insensitive, debounced ~200 ms). Matching leaves stay visible together with their ancestors; non-matching branches collapse. The scan follows same-repo `template:` references recursively (up to 10 levels deep, cycle-safe), so a match deep in the nested-template graph still surfaces its owning pipeline. Cross-repo template aliases are skipped. Before scanning, the filter automatically loads every organization, project and repository the signed-in identity can see, so a match is found even if the user has not manually expanded that subtree. YAML analysis is still capped at 500 pipelines. Localized in all six languages. Parity with the VS Code client (plan 001).

## [0.2.1] - 2026-05-18

### Added

- **Auto-detect of the local clone branch** — after linking a workspace folder to a repository node, the extension reads `.git/HEAD` (resolving `gitdir:` for git worktrees) and offers to use that branch as the per-repository override, keeping the tree aligned with what is checked out locally. Parity with the VS Code client.
- **"Open in browser" shortcut in the link prompt** — when a pipeline / template / script can't be opened because no workspace folder is linked, the prompt now offers an *Open in browser* button alongside *Link folder…*, so you can jump straight to the file on Azure DevOps without going through the folder picker.
- **401 / 403 recovery prompt** — when an Azure DevOps request fails with `Unauthorized` or `Forbidden`, the extension clears the cached credentials and shows a modal prompt with two choices: **Sign in with Microsoft** or **Sign in with PAT**. A one-shot gate (cleared on the next successful session) ensures the modal only appears once per broken session. Parity with the VS Code client.

### Fixed

- **Script-task file paths with Azure Pipelines variables are now resolved correctly.** When a `PowerShell@2` (or any script task) referenced a file like `$(System.DefaultWorkingDirectory)/modules/foo/bar.ps1`, the variable token was kept *and* the repository base directory was prepended, producing a nonsense path such as `…/modules/.workloadregistration/$(System.DefaultWorkingDirectory)/modules/.workloadregistration/Get-WorkloadRegistrationPath.ps1`. The path resolver now strips the leading variable (`$(System.DefaultWorkingDirectory)`, `$(Build.SourcesDirectory)`, `$(Pipeline.Workspace)`, `$(Agent.BuildDirectory)`) and treats the remaining path as repository-absolute, matching the VS Code client.
- **Reset no longer wipes linked workspace folders or branch overrides.** Previously the confirmation copy implied — and users reasonably expected — that **Reset** would only forget credentials. The underlying service was already auth-only (it only clears the PAT, the MSAL session and the stored sign-in method), but the prompt text was misleading. The confirmation message has been updated in all six languages (en / de / es / fr / it / sv) to explicitly state that workspace links and branch overrides are preserved.

### Changed

- **Local .vsix filename is now stable** (`PipelinesExplorer.VisualStudio.vsix`) for `F5` / experimental-hive deploys, so repeated debug sessions don't accumulate version-suffixed artifacts under `bin\Debug`. The GitHub release workflow opts in to the versioned filename (`PipelinesExplorer.VisualStudio-<version>.vsix`) by passing `-p:VsixIncludeVersionInName=true` to `dotnet build`.

## [0.2.0] - 2026-05-15

Initial public preview of the Visual Studio 2026 client. Feature-equivalent with the VS Code client at the same version:

- Activity-pane tool window **Pipelines Explorer** with a tree of `Organization → Project → Repository → Pipeline`.
- Two sign-in modes:
  - Microsoft Entra ID via MSAL (with WAM broker on Windows).
  - Azure DevOps **Personal Access Token** stored in the Windows Credential Manager.
- Recursive YAML analysis of each pipeline:
  - `template:` references (including under `extends:`).
  - Script tasks: `PowerShell@2`, `AzurePowerShell@5`, `PowerShellOnTargetMachines@3`, `Bash@3`, `ShellScript@2`, `CmdLine@2`, `BatchScript@1`, `AzureCLI@2`, `PythonScript@0`, plus shorthand `script:` / `bash:` / `pwsh:` / `powershell:`.
  - `AzureCLI@2` refined to PowerShell / Bash / Cmd based on its `scriptType` (`ps`, `pscore`, `bash`, `batch`).
- Per-kind icons under the **Scripts** group.
- **Link Workspace Folder** per repository: opens pipeline / template / script files from a local clone with a single click.
- **Per-repository branch override** — *Select Branch…* reads pipeline / template / script YAML from a chosen branch instead of the repository's default branch.
- **Microsoft tenant switching** — header row shows the active sign-in; *Select Microsoft Entra Tenant…* opens a picker populated from the ARM `/tenants` endpoint.
- **Localization** — UI and runtime messages in English, Italian, French, German, Spanish and Swedish.
- **Show Logs** command opens the current Pipelines Explorer log file.

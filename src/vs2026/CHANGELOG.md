# Changelog

All notable changes to **Pipelines Explorer for Visual Studio 2026** are documented in this file.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and
this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

> The VS Code client has its own changelog under [`src/vscode/CHANGELOG.md`](../vscode/CHANGELOG.md).

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

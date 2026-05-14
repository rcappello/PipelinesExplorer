# Changelog

All notable changes to **Pipelines Explorer** are documented in this file.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and
this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Fixed

- **Inline scripts no longer share a backing tree node.** Multiple inline tasks of the same type (e.g. several `- task: PowerShell@2` with `targetType: inline` in the same YAML) used to receive identical `TreeItem.id`s, causing VS Code to route every click on the duplicates to a single backing node — they all opened the same line. Inline scripts are now identified by `task + line`, so each entry navigates to its own task.
- **PAT silent restore on activation.** After signing in with a Personal Access Token, the next time the extension activated (e.g. a new VS Code window or `F5` in the Extension Development Host) it would prompt for the PAT again even though the token was still in `SecretStorage`. Cause: silent restore went through `vscode.authentication.getSession({ createIfNone: false })`, but the consent gate is never recorded for our own provider (sign-in bypasses it on purpose), so VS Code returned `undefined`. Silent restore now reads the session from our provider directly.

### Added

- **Inline scripts are now clickable** — clicking an `(inline script)` node opens the containing YAML at the line of the task.
- **Per-repository branch override** — right-click a repository and choose **Select Branch…** to read pipeline / template / script YAML from a specific branch instead of the repository's default branch. The repository node shows `· branch: <name>` while the override is active.
- **Auto-detect of the local clone branch** — when linking a workspace folder, the extension reads `.git/HEAD` and offers to use that branch as the override, keeping the tree aligned with what is checked out locally.
- The *"File not found in linked workspace"* warning now states which branch the YAML was read from on Azure DevOps.
- **Localization** — full UI and runtime messages translated into Italian (stable) plus French, German, Spanish and Swedish (preview, machine-translated, awaiting native review). The brand prefix *"Pipelines Explorer"* is preserved across every locale. See the **Languages** section in the README for how to contribute improvements.
- **Accessibility** — every tree item now exposes a localized `accessibilityInformation` label summarising its type and metadata (e.g. *"Repository foo, 12 pipelines, linked to local folder, branch override main"*). New **Accessibility** section in the README documents keyboard navigation, screen reader behaviour and theme support, aligned with the [VS Code accessibility guidelines](https://code.visualstudio.com/docs/configure/accessibility/accessibility).
- **Microsoft tenant switching** — a header row at the top of the tree shows the active sign-in (account name + current tenant for Microsoft sign-in). A new **Select Microsoft Entra Tenant…** button in the view title bar (next to **Refresh**) — also reachable by clicking the header row — opens a quick-pick populated with the tenants your account can access (via the Azure Resource Manager `/tenants` endpoint), so you can switch tenant without signing out. The choice is persisted across restarts and cleared by **Reset**.

## [0.1.0] - 2026-05-13

### Added

- Activity bar view **Pipelines Explorer** with a tree of `Organization → Project → Repository → Pipeline`.
- Two sign-in modes:
  - Microsoft Entra ID via VS Code's built-in `microsoft` authentication provider.
  - Custom Azure DevOps **Personal Access Token** provider (stored in `SecretStorage`).
- Recursive YAML analysis of each pipeline:
  - `template:` references (including under `extends:`).
  - `PowerShell@2`, `AzurePowerShell@5`, `AzureCLI@2` tasks (file path or inline).
  - Same-repo templates are expandable; empty groups are hidden.
- **Link Workspace Folder** per repository: opens pipeline / template / script files
  from a local clone with a single click. Pipeline variables (`System.DefaultWorkingDirectory`,
  `Build.SourcesDirectory`, `Pipeline.Workspace`, `Agent.BuildDirectory`) and relative
  `../` paths are resolved automatically.
- Auto-recovery on `401`/`403`: the stored token is cleared and the user is prompted to sign in again.
- Welcome view with sign-in shortcuts when no session is active.
- Output channel **Pipelines Explorer** with debug logging.

# Changelog

All notable changes to **Pipelines Explorer** are documented in this file.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and
this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- **Inline scripts are now clickable** — clicking an `(inline script)` node opens the containing YAML at the line of the task.
- **Per-repository branch override** — right-click a repository and choose **Select Branch…** to read pipeline / template / script YAML from a specific branch instead of the repository's default branch. The repository node shows `· branch: <name>` while the override is active.
- **Auto-detect of the local clone branch** — when linking a workspace folder, the extension reads `.git/HEAD` and offers to use that branch as the override, keeping the tree aligned with what is checked out locally.
- The *"File not found in linked workspace"* warning now states which branch the YAML was read from on Azure DevOps.

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

# Pipelines Explorer

> Browse Azure DevOps pipelines, drill into referenced YAML templates and PowerShell scripts, and jump to the local files in your workspace.

**Pipelines Explorer** brings your Azure DevOps pipelines into the VS Code activity bar.
Navigate organizations → projects → repositories → pipelines, then expand each pipeline
to inspect every YAML template and PowerShell task it references — recursively. Link
your local repository clone to the matching tree node and a single click opens the
actual file in your editor.

> *Stop hopping between browser tabs to read your pipelines.*

## Features

- 🔐 **Two sign-in modes** — Microsoft Entra ID single sign-on, or a classic Azure DevOps Personal Access Token. Expired or revoked tokens are detected automatically: the extension clears the stored credential and prompts you to sign in again.
- 🌳 **Org → Project → Repository → Pipeline** tree, with friendly empty states (`No pipelines in this project`, missing-permission warnings, etc.).
- 🔍 **Recursive YAML analysis** — every `template:` reference and every `PowerShell@2`, `AzurePowerShell@5` and `AzureCLI@2` task is surfaced under each pipeline. Same-repo templates can be expanded to reveal *their* templates and scripts. Empty groups are hidden.
- 🔗 **Link a workspace folder to a repository** and click any pipeline / template / script to open the local file. Pipeline variables like `$(System.DefaultWorkingDirectory)` and `$(Build.SourcesDirectory)`, repo-absolute paths and relative `../` segments are all resolved automatically.
- 📋 **Filename-first labels** with full repo paths in the tooltip, so the tree stays readable on long deployment templates.

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
            │   │   │   └── PowerShell scripts (1)
            │   │   │       └── Get-WorkloadPath.ps1     PowerShell@2
            │   │   ├── build.yml
            │   │   └── lint.yml
            │   └── PowerShell scripts (2)
            │       ├── New-EntraIdWorkload.ps1   AzurePowerShell@5
            │       └── (inline script)           PowerShell@2
            └── …
```

<!-- TODO: replace with real screenshots before publishing on the public Marketplace -->

## Getting started

1. Install the `.vsix`:
   ```powershell
   code --install-extension vscode-pipelinesexplorer-0.1.0.vsix
   ```
   (Or, once published, install from the Marketplace.)
2. Open the **Pipelines Explorer** view in the activity bar.
3. Choose a sign-in method:
   - **Sign in with Microsoft** — uses VS Code's built-in Microsoft account. Recommended for organizations connected to Microsoft Entra ID.
   - **Sign in with Personal Access Token** — paste a PAT with at least `Code (Read)`, `Build (Read)` and `Project and Team (Read)` scopes.
4. Browse organizations → projects → repositories → pipelines.

### Linking a local clone

If you already have a local clone of one of your repositories open in VS Code:

1. Right-click the repository node and choose **Link Workspace Folder…** (or use the inline link icon).
2. Pick the workspace folder that contains the clone.
3. Single-click any pipeline / template / `*.ps1` underneath that repo to open the file in the editor.

If a referenced file isn't found in the linked folder (e.g. it's on a different branch), the extension shows a warning with **Re-link Workspace** and **Open in Browser** options.

To remove a link: right-click the repository → **Unlink Workspace Folder**.

## Commands

| Command | Description |
| --- | --- |
| `Pipelines Explorer: Sign in with Microsoft` | Sign in with the Microsoft authentication provider. |
| `Pipelines Explorer: Sign in with Personal Access Token` | Sign in with a stored PAT (asked on first use). |
| `Pipelines Explorer: Sign out` | Clear the active session. |
| `Pipelines Explorer: Reset (clear stored credentials)` | Wipe the stored PAT and forget the chosen sign-in method. |
| `Pipelines Explorer: Refresh` | Re-fetch the tree and re-analyse pipelines. |
| `Pipelines Explorer: Show Logs` | Open the extension output channel. |
| Link / Unlink Workspace Folder | Available from the repository node context menu. |

## Requirements

- VS Code **1.99.0** or newer.
- An Azure DevOps account with read access to the organizations / projects / pipelines you want to browse.
- (Optional) Local clones of the repositories whose files you want to open.

## Known limitations

- Cross-repo template references (`template: file.yml@otherRepo`) are shown as leaves in the tree (no recursion). They can still be opened locally if a workspace folder has been linked to a repo with the same alias.
- Only `azureReposGit` repositories are inspected for YAML content. GitHub-hosted pipeline definitions are listed but not expanded.
- Pipeline variables other than `System.DefaultWorkingDirectory`, `Build.SourcesDirectory`, `Pipeline.Workspace` and `Agent.BuildDirectory` are not interpolated when resolving local file paths.

## Telemetry

This extension does **not** collect telemetry.

## License

[MIT](LICENSE) © Riccardo Cappello

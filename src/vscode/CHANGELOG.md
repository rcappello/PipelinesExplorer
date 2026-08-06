# Changelog

All notable changes to **Pipelines Explorer** are documented in this file.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and
this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.4.0] - 2026-08-05

### Added

- **Per-organization PAT sign-in.** Sign-in with a Personal Access Token now works with **both** *All accessible organizations* PATs (the historical path, unchanged) and **organization-scoped** PATs. When the classic discovery call to `_apis/accounts` returns nothing usable — the deterministic outcome for an org-scoped PAT, and increasingly the case even for global PATs (see plan 002 §1.1) — the extension prompts for the **exact Azure DevOps organization identifier** (the segment after `dev.azure.com/` in your URL, e.g. `contoso`), validates the token against `dev.azure.com/{org}/_apis/projects?$top=1`, and stores the `<org, pat>` pair in its own [`SecretStorage`](https://code.visualstudio.com/api/references/vscode-api#SecretStorage) slot. Mistyped or unauthorized names surface an actionable *Try another* / *Cancel* dialog rather than a broken slot. The tree then renders that organization normally. Multiple per-organization PATs coexist and every request routes to the right token automatically.
- **`Pipelines Explorer: Add Azure DevOps organization…`** command (title-bar `+` icon under PAT sign-in, and Command Palette). Prompts for an organization name and a matching PAT, then adds that organization as a new root without touching the existing ones.
- **Organization-name suggestions** in the *Add organization* prompt (plan 002 §B.1). The extension now:
  - Remembers every organization ever added via the per-org flow in a rolling history (up to 20 entries, most-recent first) that survives `Sign out` — only `Reset` wipes it. From the second add onwards the prompt opens as a QuickPick over that history, plus a *"Type another organization name…"* fallback.
  - Sniffs the system clipboard when the input box is shown, and pre-selects the org portion of any `https://dev.azure.com/{org}/…` or `https://{org}.visualstudio.com/…` URL it finds. Covers the common case of copying an ADO URL from the browser before running the command.

### Changed

- The tree merges organizations from the global `_apis/accounts` call **and** every per-organization slot, deduplicated by canonical name (per-org slots win on conflict).
- `Sign out` also clears every per-organization PAT slot, matching plan 002 §2.3.
- Internal: `AdoClient` request routing threads the target organization through to `AuthService.getHeaders`, so each `dev.azure.com/{org}/…` call uses the right PAT.
- Copy: the *Add organization* error for `unauthorized` now reads *"The token was rejected for organization … . This can happen if the token is invalid, revoked, or not scoped to this organization."* — the previous wording implied the org was the problem when a wrong PAT is just as likely.

### Fixed

- Cancelling the *Add Azure DevOps organization* prompt right after a fresh PAT sign-in now signs the user out and discards the just-entered token, so a fake or unverifiable PAT no longer lingers in `SecretStorage` and reactivates as a zombie session on the next activation. Cancelling on top of an already-working session (prompt opened via the *Add another organization* command) still leaves the existing per-org PATs untouched.

### Documentation

- README: reworked the *PAT scope and the 1 Dec 2026 deprecation* section to describe the two supported PAT shapes and to explain the small UX cost of an org-scoped PAT — you must type the organization name once at first sign-in. Verified empirically (2026-07-07) that Azure DevOps does not expose any endpoint that returns the org from an opaque scoped PAT: `_apis/accounts?memberId=` returns `0`, `_apis/ConnectionData` (SPS-level) responds `401`, and `_apis/tokens/pats` requires the org already. The org name is intrinsically unrecoverable client-side.

### Maintenance

- Updated vulnerable transitive development dependencies in `package-lock.json`: `brace-expansion` (`1.1.18`, `2.1.4`, and `5.0.9`), `fast-uri` `3.1.5`, `js-yaml` `4.3.1`, `linkify-it` `5.0.2`, `shell-quote` `1.10.0`, and `undici` `7.29.0`. `npm audit` now reports no vulnerabilities.

## [0.3.2] - 2026-07-02

### Changed

- Group headers under the tree filter now show the visible / total count (e.g. `Templates 4/7`) when the filter hides some items, and revert to the plain total (`Templates 7`) when the filter is cleared or when every item in the group still matches. Matches the same behaviour in the Visual Studio 2026 client.

### Documentation

- Note in the README that PAT sign-in currently requires the *All accessible organizations* scope, and that Azure DevOps global PATs are retired on 1 December 2026 ([aka.ms/GlobalPATDeprecation](https://aka.ms/GlobalPATDeprecation)). Microsoft sign-in is recommended as the durable path; multi-org PAT support after the retirement is tracked as plan 002.

> The VS Code client skipped `0.3.1`: that patch shipped fixes only in the Visual Studio 2026 client. `0.3.2` is the next joint release after `0.3.0`.

## [0.3.0] - 2026-07-02

### Added

- **Filter by name** — a new **Filter Pipelines Explorer** command (title-bar icon and Command Palette) restricts the tree to pipelines, templates and scripts whose name contains a substring (case-insensitive, debounced ~200 ms). A status node reports scanning / result count / capped / no-results states; matching pipelines are auto-revealed. The scan follows same-repo `template:` references recursively (up to 10 levels deep, cycle-safe), so a match deep in the nested-template graph still surfaces its owning pipeline. Cross-repo template aliases are skipped. Before scanning, the filter automatically loads every organization, project and repository the signed-in identity can see, so a match is found even if the user has not manually expanded that subtree. YAML analysis is still capped at 500 pipelines. Localized in all six languages. See plan 001.

## [0.2.0] - 2026-05-15

### Added

- **Broader script-task detection.** The pipeline tree now surfaces every script-running step, not just PowerShell. Recognised tasks: `PowerShell@2`, `AzurePowerShell@5`, `PowerShellOnTargetMachines@3`, `Bash@3`, `ShellScript@2`, `CmdLine@2`, `BatchScript@1`, `AzureCLI@2`, `PythonScript@0`. Shorthand step keys are also recognised: `script:` (cmd/bash on the agent), `bash:`, `pwsh:`, `powershell:`. The `BatchScript@1` `filename` input is resolved as a file path, and `AzureCLI@2` is refined to PowerShell / Bash / Cmd based on its `scriptType`.
- **Per-kind icons** under the new **Scripts** group: PowerShell (`terminal-powershell`), Bash (`terminal-bash`), Cmd / Batch (`terminal-cmd`), Python (`snake`), Azure CLI (`azure`), and a generic terminal icon as fallback.

### Changed

- **Tree group renamed.** The previous *PowerShell scripts* group is now simply **Scripts**, since it can contain non-PowerShell entries. Localized in every supported language.
- The *No PowerShell scripts referenced* placeholder becomes *No scripts referenced*.

## [0.1.1] - 2026-05-14

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

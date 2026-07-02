# Changelog

This file aggregates notable changes across all clients in this repository.
Per-client changelogs (used by their respective marketplaces) live under each
client folder:

- [`src/vscode/CHANGELOG.md`](src/vscode/CHANGELOG.md)
- [`src/vs2026/CHANGELOG.md`](src/vs2026/CHANGELOG.md)

Entries below use the format `[client] message`, where `client` is `vscode`
or `vs2026`.

## [Unreleased]

### Fixed

- `[vs2026]` Filter pruning did not survive lazy-load: when a pipeline or template was expanded manually after the filter scan, non-matching sibling templates and the whole `Scripts` group stayed visible. Visibility is now reapplied to every newly materialised subtree, matching the VS Code client (plan 001 §2 parity).

### Documentation

- `[vscode]` `[vs2026]` Note the Azure DevOps global PAT retirement scheduled for 1 December 2026 ([aka.ms/GlobalPATDeprecation](https://aka.ms/GlobalPATDeprecation)): both READMEs now spell out that PAT sign-in currently requires the *All accessible organizations* scope and recommend Microsoft sign-in as the durable path. Multi-org PAT support after the retirement is tracked as plan 002.

## [0.3.0] - 2026-07-02

- `[vscode]` `[vs2026]` **0.3.0** — **Filter by name.** New filter (title-bar icon and Command Palette in VS Code; filter box in the tool window in VS 2026) restricts the tree to pipelines, templates and scripts whose name contains a substring (debounced, case-insensitive). Before scanning, every organization / project / repository the signed-in identity can see is preloaded, and same-repo `template:` references are followed recursively (up to 10 levels deep, cycle-safe). Cross-repo aliases are skipped. YAML analysis is capped at 500 pipelines. Localized in all six languages. See plan 001 and the per-client CHANGELOGs.

## [0.2.0] - 2026-05-18

- `[repo]` Split the repository into a monorepo layout with `src/vscode` and
  `src/vs2026` client roots; added path-filtered CI workflows
  (`.github/workflows/vscode.yml`, `vs2026.yml`) and renamed the VS Code
  release workflow to `release-vscode.yml` (now triggered by `vscode-v*` tags).
- `[vs2026]` Initial scaffold of the Visual Studio 2026 extension using the
  Microsoft.VisualStudio.Extensibility SDK (out-of-process, .NET 10, WPF).
- `[vscode]` `[vs2026]` **0.2.0** — Broaden script-task detection beyond PowerShell to include Bash, Cmd/Batch, Python, Azure CLI and the shorthand step keys (`script:`, `bash:`, `pwsh:`, `powershell:`); rename the *PowerShell scripts* tree group to **Scripts**; add per-kind icons. See [`src/vscode/CHANGELOG.md`](src/vscode/CHANGELOG.md) for the full VS Code entry.

## VS Code client

See [`src/vscode/CHANGELOG.md`](src/vscode/CHANGELOG.md) for the full history
of the VS Code extension (latest: 0.2.0).

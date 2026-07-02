# Changelog

This file aggregates notable changes across all clients in this repository.
Per-client changelogs (used by their respective marketplaces) live under each
client folder:

- [`src/vscode/CHANGELOG.md`](src/vscode/CHANGELOG.md)
- [`src/vs2026/CHANGELOG.md`](src/vs2026/CHANGELOG.md)

Entries below use the format `[client] message`, where `client` is `vscode`
or `vs2026`.

## [Unreleased]

## [0.3.2] - 2026-07-02

### Changed

- `[vscode]` `[vs2026]` Group headers under the tree filter now show the visible / total count (e.g. `Templates 4/7`) when the filter hides some items, and revert to the plain total when the filter is cleared or when every item in the group still matches.

### Fixed

- `[vs2026]` Filter pruning did not survive lazy-load: when a pipeline or template was expanded manually after the filter scan, non-matching sibling templates and the whole `Scripts` group stayed visible. Visibility is now reapplied to every newly materialised subtree, matching the VS Code client (plan 001 §2 parity).
- `[vs2026]` Filter pruning had two remaining gaps that made results depend on when the user expanded a pipeline. The scan now records deferred group-visibility marks (honoured when `Templates` / `Scripts` groups are materialised later) and tracks intermediate templates that transitively contain a match, so a matched pipeline no longer shows an empty `Templates` group and a `Scripts` group without direct matches no longer leaks in.
- `[vs2026]` A residual Remote UI race could leave a matched pipeline's `Scripts` group visible under an active filter even after `ApplyVisibilityRecursive` had already flipped it to hidden. Visibility is now applied to the local `children` list **before** it is handed to `ReplaceList`, so the initial snapshot sent to the client already carries the correct `IsVisibleUnderFilter` for every new node.

### Security

- `[vs2026]` Override the transitive `MessagePack` dependency (pulled in at `2.5.198` by `Microsoft.VisualStudio.Extensibility.Sdk 17.14`) with an explicit `PackageReference` on `MessagePack` / `MessagePack.Annotations` `2.5.302`. Clears the two GitHub Advisory database findings ([GHSA-vh6j-jc39-fggf](https://github.com/advisories/GHSA-vh6j-jc39-fggf) — `MessagePackReader.Skip` unbounded recursion, and [GHSA-hv8m-jj95-wg3x](https://github.com/advisories/GHSA-hv8m-jj95-wg3x) — LZ4 decompression out-of-bounds read) along with the eight moderate `NU1902` advisories that `dotnet restore` reported on the same package. Kept on the `2.5.x` patch line to preserve binary compatibility with the Extensibility SDK's own use of MessagePack.

### Documentation

- `[vscode]` `[vs2026]` Note the Azure DevOps global PAT retirement scheduled for 1 December 2026 ([aka.ms/GlobalPATDeprecation](https://aka.ms/GlobalPATDeprecation)): both READMEs now spell out that PAT sign-in currently requires the *All accessible organizations* scope and recommend Microsoft sign-in as the durable path. Multi-org PAT support after the retirement is tracked as plan 002.

> `[vscode]` skipped `0.3.1`: that patch shipped fixes only in the Visual Studio 2026 client. `0.3.2` is the next joint release after `0.3.0` for both clients.

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

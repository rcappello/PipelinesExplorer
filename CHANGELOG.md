# Changelog

This file aggregates notable changes across all clients in this repository.
Per-client changelogs (used by their respective marketplaces) live under each
client folder:

- [`src/vscode/CHANGELOG.md`](src/vscode/CHANGELOG.md)
- [`src/vs2026/CHANGELOG.md`](src/vs2026/CHANGELOG.md)

Entries below use the format `[client] message`, where `client` is `vscode`
or `vs2026`.

## [Unreleased]

## [0.4.0] - 2026-08-05

### Added

- `[vscode]` **Per-organization PAT sign-in.** Sign-in now works with both *All accessible organizations* PATs (unchanged) and organization-scoped PATs; a new *Add Azure DevOps organization…* command (title-bar `+` icon + Command Palette) layers additional per-org PATs on top of an existing session. Roots merge the global discovery result and every per-org slot, deduplicated. See plan 002 §2 for the full flow.
- `[vscode]` **Organization-name suggestions** in the *Add organization* prompt: a rolling history (survives `Sign out`, wiped by `Reset`) offers past organizations as picks, and the input box is pre-filled from the clipboard when it contains a `dev.azure.com/{org}` or `{org}.visualstudio.com` URL (plan 002 §B.1).
- `[vs2026]` **Per-organization PAT sign-in** with the same behavior contract as the VS Code client: after a PAT sign-in the extension runs the classic `_apis/accounts` discovery and, if it returns nothing usable, opens an inline *Add Azure DevOps organization* panel in the tool window with the just-entered PAT pre-filled. `dev.azure.com/{org}/_apis/projects?$top=1` validates the pair and a per-org slot is stored in Windows Credential Manager. A new *Pipelines Explorer: Add Azure DevOps organization…* Tools-menu command opens the panel manually to add more organizations on top of an existing sign-in.
- `[vs2026]` **Organization-name suggestions** in the *Add organization* panel: the same rolling history (survives *Sign out*, wiped by *Reset*, capped at 20 entries) is rendered as clickable buttons that fill the organization field in one click.

### Changed

- `[vs2026]` `AdoClient` now creates its owned `HttpClient` with `HttpClientHandler { UseCookies = false }`. Azure DevOps REST authenticates entirely via the `Authorization` header, and shared cookies (e.g. `VstsSession` set by `app.vssps.visualstudio.com`) can only pollute subsequent requests. Hygiene fix; no user-visible behavior change.
- `[vs2026]` **Backend plumbing for per-organization PATs** — `PatCredentialStore` grows per-org slots and a survives-sign-out history; `IAdoAuthHeaderProvider` gains an `orgHint` parameter so `AdoClient` routes every `dev.azure.com/{org}/…` call to the right token via an in-memory cache; new `ProbeOrganizationAsync` classifies validation outcomes as `OrgProbeResult`. `SignOut` now clears every per-org slot alongside the global slot (plan 002 §2.3) but preserves the org-name history. The tool-window UX to feed the new store lands in the same release.
- `[vscode]` `Sign out` now clears every per-organization PAT slot in addition to the global slot, matching plan 002 §2.3.
- `[vscode]` `[vs2026]` Copy: the *Add organization* error for `unauthorized` now reads *"The token was rejected for organization … . This can happen if the token is invalid, revoked, or not scoped to this organization."* — the previous wording implied the org was the problem when a wrong PAT is just as likely.
- `[vs2026]` The tree's "no organizations" placeholder now reads *"No Azure DevOps organizations added yet. Use 'Add Azure DevOps organization…' to name one."* under PAT sign-in (previously the Microsoft-only wording *"…found for this tenant"* was shown for PAT too). VS Code already had the corresponding behavior.

### Fixed

- `[vs2026]` Sign-in with an organization-scoped PAT used to trigger the unauthorized-recovery dialog and forcibly sign the user out, because the refresh treated the deterministic 401 from `app.vssps.visualstudio.com/_apis/profile/profiles/me` as a real credentials failure. For PAT sessions this specific 401 is now recognised as the *expected* signal that the token cannot enumerate cross-org and the flow falls through to the per-organization slots as intended.
- `[vscode]` `[vs2026]` Cancelling the *Add Azure DevOps organization* prompt/panel right after a fresh PAT sign-in now signs the user out and discards the just-entered token, so a fake or unverifiable PAT no longer lingers in `SecretStorage` / Credential Manager and reactivates as a zombie session on the next activation. Cancelling on top of an already-working session (opened via the *Add another organization* command) still leaves the existing per-org PATs untouched.

### Documentation

- `[vscode]` `[vs2026]` Reworked the *PAT scope and the 1 Dec 2026 deprecation* section of both READMEs to describe the two supported PAT shapes and to explain the small UX cost of an org-scoped PAT: the extension has to ask for the organization name once, because Azure DevOps does not expose any endpoint that returns the org from an opaque scoped PAT (verified on 2026-07-07: `_apis/accounts?memberId=` returns `0`, `_apis/ConnectionData` at the SPS level responds `401`, `_apis/tokens/pats` requires the org already in the URL, and `_apis/profile/profiles/me` only carries the identity).
- Plan 002 §1.1 documents field evidence (2026-07-07) that `_apis/accounts` is already non-deterministic today \u2014 same PAT + same `memberId` returned 4, then 1, then 2 organizations across three calls minutes apart.

### Maintenance

- `[vscode]` Updated vulnerable transitive development dependencies in `package-lock.json`: `brace-expansion` (`1.1.18`, `2.1.4`, and `5.0.9`), `fast-uri` `3.1.5`, `js-yaml` `4.3.1`, `linkify-it` `5.0.2`, `shell-quote` `1.10.0`, and `undici` `7.29.0`. `npm audit` now reports no vulnerabilities.

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
of the VS Code extension (latest: 0.4.0).

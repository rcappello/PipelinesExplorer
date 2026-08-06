---
applyTo: "**"
---

# Pipelines Explorer — Shared Instructions

These instructions apply to all agents and Copilot interactions defined within this
repository. They complement (never override) the
[Project Constitution](../../.specify/memory/constitution.md).

Constraints below are grouped into three priority tiers numbered 1, 2, and 3.
Lower numbers always take precedence: tier 1 wins over tier 2, and tier 2 wins
over tier 3. When in doubt, satisfy the lowest-numbered tier first and re-check
higher-numbered tiers only if they do not contradict it.

1. **Must** (security, correctness, process) — never violate; see the bullets
   under `### Must` below.
2. **Should** (language, structure, stack) — follow unless the plan documents an
   exception; see the bullets under `### Should` below.
3. **Style** (output formatting for agent responses) — apply when the prior two
   tiers are satisfied; see the bullets under `## Output style for agents`.

## Repository map

- `src/vscode/` — VS Code extension (TypeScript).
  - Entry: `src/extension.ts`
  - ADO HTTP: `src/adoClient.ts`
  - Auth: `src/authProvider.ts`, `src/authService.ts`
  - Tree: `src/pipelinesTreeProvider.ts`
  - YAML analysis: `src/pipelineYamlAnalyzer.ts`
  - Logging: `src/LoggingService.ts`
  - Tests: `src/test/`
- `src/vs2026/` — Visual Studio 2026 extension (C# / .NET 10 / WPF).
  - Entry: `ExtensionEntrypoint.cs`, `ExtensionServices.cs`
  - Auth: `Auth/`
  - ADO HTTP & models: `AzureDevOps/`
  - Commands: `Commands/`
  - Tool windows & view models: `ToolWindows/`, `ViewModels/`
  - Services: `Services/`
- `docs/` — cross-client documentation.
- `.specify/memory/constitution.md` — non-negotiable principles.
- `.specify/plans/` — confirmed plans consumed by implementer/reviewer agents.
- `.github/chatmodes/` — reusable agents (planner/implementer/reviewer).
- `.github/prompts/` — prompts that invoke each agent.

## Build & validate

| Client | Build | Test | Package |
| --- | --- | --- | --- |
| VS Code | `npm run compile` (in `src/vscode`) | `npm test` (in `src/vscode`) | `npm run package` then `npx @vscode/vsce package` |
| VS 2026 | `dotnet build src/vs2026/PipelinesExplorer.VisualStudio.csproj -c Debug` | `dotnet test` (if a test project exists) | `dotnet build -c Release` produces the `.vsix` |

Background watch tasks (`npm: watch - src/vscode` and friends) may already be running —
prefer them over launching duplicates.

## Conventions

### Must (security & process)

- **Auth.** Use `authService` / `AdoAuthService`. Surface 401s via the existing
  unauthorized flows.
- Don't log secrets. Don't commit secrets. Don't print PATs into chat.
- Don't edit files outside the plan's declared scope.
- Don't bump versions, publish, push tags, or run release workflows unless the plan
  explicitly says so.
- Don't add dependencies without listing them in the plan and justifying them.
- **VS Code lockfile.** Before pushing or opening/updating a PR that changes
  `src/vscode/`, run `npm ci --ignore-scripts` in `src/vscode/` to verify that
  `package-lock.json` is aligned with `package.json`. If the check fails, regenerate
  the lockfile from the manifest and include both files in the same change.
- **Release preparation.** When the developer asks to prepare a release, for each
  requested client: promote its `Unreleased` changelog entries to the requested
  version and release date; update the publishing version in the source-of-truth
  file listed below; synchronize derived manifests or lockfiles; run the client's
  build, tests, packaging, and release-specific consistency checks; and report the
  exact tag the developer should create. Do not create tags, push commits or tags,
  or trigger release workflows. The developer performs those actions.
- Don't change formatting of unrelated code.

### Should (stack & structure)

- **Languages.** TypeScript for `src/vscode/`, C# for `src/vs2026/`. Do not introduce a
  new language without an approved plan.
- **HTTP.** Use the existing `AdoClient` (TS) / `AdoClient.cs` (C#) — do not add a
  second HTTP stack.
- **Localization.** VS Code strings go through the `l10n` bundles
  (`src/vscode/l10n/` and `package.nls*.json`). Do not hard-code user-visible strings.
- **Resources.** VS 2026 user-visible strings live in `src/vs2026/Resources/Strings.resx`
  (default) plus one `Strings.<lang>.resx` per locale, accessed through the generated
  `Strings.cs`. Add new strings to **all** existing locale files when you add them to
  the default.
- **Versioning source of truth.**
  - VS Code: `src/vscode/package.json` `version`.
  - VS 2026: `src/vs2026/source.extension.vsixmanifest` `Identity/@Version` — the
    csproj reads it and propagates it to the assembly `Version`. Do **not** try to
    bump the VS 2026 version via `-p:Version` in the workflow or csproj.
- **Commands.** Register VS Code commands in `package.json` (`contributes.commands`)
  and wire them in `extension.ts`. VS 2026 commands go under `Commands/` and are
  registered via the VisualStudio.Extensibility attributes already used in the repo.

## Release decision policy

After merging a batch of changes (typical case: several Dependabot PRs), use the
matrices below to decide whether to **propose** a version bump. Per the Must rule
above, never actually bump versions, publish, push tags, or run release workflows
without an explicit plan or developer instruction — only propose.

Guiding principle: a change warrants a release **only if it can affect what the
end user installs from the Marketplace**. Pure build-time, CI, lockfile, or
developer-tooling changes do not.

### VS Code client (`src/vscode/`)

The published VSIX contains only `dist/extension.js` (compiled from `src/`),
shipped assets under `media/` and `resources/`, the localized
`package.nls*.json` and `l10n/bundle.l10n.*.json` bundles, and the runtime
`dependencies` declared in `src/vscode/package.json`. Anything under
`devDependencies` — including their transitive packages in the lockfile — does
**not** reach end users.

| Change | Propose release? | Suggested bump |
| --- | --- | --- |
| `src/` TypeScript, shipped `media/` / `resources/` | Yes | Per semver |
| Runtime `dependencies` in `package.json` (e.g. `yaml`) | Yes | Patch (security/bugfix) or minor (feature) |
| `engines.vscode` raise, `activationEvents`, `contributes.*` | Yes | Minor (new capability) or patch (engine bump alone) |
| User-visible strings in `package.nls*.json` or `l10n/bundle.l10n.*.json` | Yes | Patch |
| Direct `devDependencies` bumps (`esbuild`, `typescript`, `eslint`, `@vscode/vsce`, test tooling) | No | — |
| Transitive dev-dep bumps in `package-lock.json` only (Dependabot security alerts on `shell-quote`, `form-data`, `undici`, `markdown-it`, `js-yaml`, etc.) | No | — |
| `README.md`, `CHANGELOG.md` formatting, `docs/`, `.github/`, CI workflows | No | — |

Notes:

- An `esbuild` major bump may produce a byte-different bundle without any
  behavior change — still not changelog-worthy on its own.
- Accumulated dev-dep upgrades may be listed under `### Internal` or
  `### Maintenance` in the CHANGELOG of the next functional release, but must
  never be the sole reason for a release.

### VS 2026 client (`src/vs2026/`)

The published VSIX contains the compiled assembly plus any embedded
`Resources/Strings*.resx`. NuGet `PackageReference` items resolved at build
time ship inside the VSIX **unless** marked `PrivateAssets="all"`, declared as
analyzers, or otherwise build-only.

| Change | Propose release? | Suggested bump |
| --- | --- | --- |
| C# under `Auth/`, `AzureDevOps/`, `Commands/`, `Services/`, `ToolWindows/`, `ViewModels/`, or the root entry files | Yes | Per semver |
| User-visible strings in any `Resources/Strings*.resx` | Yes | Patch |
| Runtime NuGet `PackageReference` bumps (i.e. not `PrivateAssets="all"`) | Yes | Patch (security/bugfix) or minor (feature) |
| `source.extension.vsixmanifest` metadata changes beyond `<Identity Version="…">` (display name, description, targets, prerequisites, install targets) | Yes | Patch |
| Analyzers, SourceLink, MSBuild SDKs, build-only NuGets (`PrivateAssets="all"`, `IncludeAssets="build…"`) | No | — |
| `.csproj` / `.editorconfig` / build pipeline / `.github/` workflows | No | — |
| Local-only artefacts (`*.user`, `bin/`, `obj/`, `*.lscache`) | No | — |

Notes:

- Bump source remains `src/vs2026/source.extension.vsixmanifest`
  `Identity/@Version` — never `-p:Version` and never a `<Version>` in the
  csproj.

### Agent workflow on a batch of merges

1. Classify each PR / commit with the matrix for its client.
2. If **all** entries land in the "No" rows, recommend skipping the release and
   rolling the changes into the next functional release.
3. If **any** entry lands in a "Yes" row, recommend a bump using the highest
   required level (minor > patch). Cite the triggering PR(s) and the exact file
   under "Versioning source of truth" the developer would edit.
4. Never edit the version files yourself unless the developer explicitly
   confirms — and only inside a plan.

## What to do before writing code

1. Read the plan referenced by the user (under `.specify/plans/`).
2. Read every file the plan lists as "touched".
3. Re-read the constitution sections relevant to the change (logging, security,
   testing, performance).
4. Only then propose edits.

## Output style for agents

- Be concise. Use Markdown headings and bullet lists.
- Reference files as workspace-relative Markdown links.
- When asking the developer to decide something, present **numbered options**.

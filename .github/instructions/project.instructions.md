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

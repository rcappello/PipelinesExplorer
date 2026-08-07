# Project Constitution — Pipelines Explorer

Non-negotiable principles for both clients (VS Code and VS 2026). Everything
else may be negotiated within a plan in [`.specify/plans/`](../plans/).

## 1. Cross-client feature parity

Every feature must be implemented in **both** `src/vscode/` and `src/vs2026/`.
Exceptions:

- are allowed only when an objective blocker (unavailable platform API or SDK
  constraint) is documented in the plan's **"Cross-client parity"** section;
- require explicit confirmation from the author before implementation;
- must state whether and when parity will be restored.

## 2. Security & secrets

- Do not include PATs, Bearer tokens, or secrets **in logs, UI messages, or
  tests**.
- Credentials must always pass through:
  - VS Code: `AuthService` ([authService.ts](../../src/vscode/src/authService.ts));
  - VS 2026: `AdoAuthService` ([Auth/AdoAuthService.cs](../../src/vs2026/Auth/AdoAuthService.cs)).
- Azure DevOps 401/403 responses must use the existing *unauthorized* flows and
  must not generate ad hoc popups.

## 3. One HTTP stack per client

All Azure DevOps calls must pass through:

- VS Code: `AdoClient` ([adoClient.ts](../../src/vscode/src/adoClient.ts));
- VS 2026: `AdoClient` ([AzureDevOps/AdoClient.cs](../../src/vs2026/AzureDevOps/AdoClient.cs)).

Do not introduce a parallel `fetch`/`HttpClient` stack or an additional HTTP
library without an approved plan.

## 4. Mandatory localization

Do not hard-code user-facing strings.

- **VS Code**: use `vscode.l10n.t(...)` with bundles in
  [`src/vscode/l10n/`](../../src/vscode/l10n/) and
  [`src/vscode/package.nls*.json`](../../src/vscode/package.nls.json).
- **VS 2026**: use `Strings.<Key>` from
  [`Resources/Strings.resx`](../../src/vs2026/Resources/Strings.resx) (default)
  and every existing `Strings.<culture>.resx` file (it, fr, de, es, sv).

Adding a new key **requires** adding it to every existing locale file, even if
the English value must be copied when a translation is unavailable.

## 5. Logging

- Log through `LoggingService` (in both clients) at the `info`, `warn`, or
  `error` level.
- Do not use `console.log`, `Debug.WriteLine`, or `Console.WriteLine` in shipping
  code.
- Logs must not contain secrets, `Authorization` headers, or URLs containing a
  PAT.

## 6. Performance & Azure DevOps I/O

- Repeated Azure DevOps calls over collections must run **concurrently with a
  limit** (see `mapWithConcurrency` in VS Code and `Chunk(..., 8)` in VS 2026).
- Long-running operations must be **cancellable** (`CancellationToken`,
  `vscode.CancellationToken`, or an internal token).
- Loading operations must not block the UI; the model is lazy by default. Any
  departure from lazy loading must be justified in the plan.

## 7. Testing & validation

Every plan must declare at least:

- how the build was validated (`npm run compile`, `dotnet build`);
- a **manual smoke test** checklist for both clients;
- new automated tests when the change introduces non-trivial logic (parsers,
  filters, path resolution, and similar behavior).

## 8. Versioning — source of truth

- VS Code: `version` in
  [`src/vscode/package.json`](../../src/vscode/package.json).
- VS 2026: `Identity/@Version` in
  [`src/vs2026/source.extension.vsixmanifest`](../../src/vs2026/source.extension.vsixmanifest).

Version bumps, tags, pushes, or release workflows are allowed **only** when the
plan explicitly requires them and the author confirms. The "Release decision
policy" matrix in
[`.github/instructions/project.instructions.md`](../../.github/instructions/project.instructions.md)
remains authoritative when deciding whether a change warrants a version bump.

## 9. Scope discipline

- Do not perform opportunistic refactoring outside the plan's scope.
- Do not add runtime dependencies without declaring and justifying them in the
  plan.
- Do not reformat code unrelated to the feature.

# NNN — &lt;Feature title&gt;

> Status: **Draft** | **Approved** | **In progress** | **Done** | **Abandoned**  
> Owner: &lt;handle&gt;  
> Created: YYYY-MM-DD  
> Last updated: YYYY-MM-DD

## 1. Context / problem

What user problem are we solving? What's missing today? Link to the request,
screenshot, or thread that triggered the work.

## 2. UX and expected behavior

Describe what the user sees and does. Use bullet lists for the happy path and
the relevant edge cases. Mention keyboard / accessibility expectations when
they're not obvious.

## 3. Cross-client parity

Default: **identical** behavior in VS Code and VS 2026. If a divergence is
required, list it here with the reason and how/when parity is restored.

| Aspect | VS Code | VS 2026 | Notes |
| --- | --- | --- | --- |

## 4. Scope — VS Code (`src/vscode/`)

- Files touched: …
- New commands / settings / context keys: …
- New l10n strings (added to `package.nls*.json` and/or `l10n/bundle.l10n.*.json`): …
- New runtime dependencies (must be justified): …

## 5. Scope — VS 2026 (`src/vs2026/`)

- Files touched: …
- New commands / tool-window controls: …
- New resources (added to `Resources/Strings.resx` **and** all `Strings.<culture>.resx`): …
- New runtime NuGet references (must be justified): …

## 6. Out of scope

What we explicitly are **not** doing in this iteration.

## 7. Tests & validation

- Automated tests: …
- Manual smoke (VS Code): …
- Manual smoke (VS 2026): …
- Build commands run: `npm run compile`, `dotnet build src/vs2026/PipelinesExplorer.VisualStudio.csproj -c Debug`.

## 8. Risks / open questions

Anything unresolved. Note assumptions.

## 9. Release impact

Per the matrix in
[project.instructions.md](../../.github/instructions/project.instructions.md):

- VS Code: **No / Patch / Minor** — reason: …
- VS 2026: **No / Patch / Minor** — reason: …
- Action: do not bump until the author confirms.

## 10. Change log (filled during implementation)

- YYYY-MM-DD — &lt;short note&gt;

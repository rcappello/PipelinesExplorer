# 001 — Tree search / filter by name

> Status: **Approved**  
> Owner: rcappello  
> Created: 2026-06-30  
> Last updated: 2026-06-30

## 1. Context / problem

The Pipelines Explorer tree is fully lazy: every level
(Organization → Project → Repository → Pipeline → Templates|Scripts) is
fetched only when the user expands the parent node. To answer questions like
*"which pipelines reference `build.yml`?"* or *"is there a template named
`deploy-prod.yml` anywhere in this project?"* the user has to manually click
through every pipeline. We want a **search box** that filters the tree to
the items whose **name** matches a substring.

Scenarios from the request:

- Filter pipelines by **pipeline name** *or* by the **YAML file name** of the
  pipeline root.
- Filter **templates** by file name — the whole branch above the match must
  remain visible.
- Filter **scripts** by file name — same rule for the parent branch.

## 2. UX and expected behavior

- A new **search input** sits at the top of the pipelines view:
  - VS Code: command `pipelinesexplorer.filter` (title-bar icon `$(search)`)
    that opens a `vscode.window.showInputBox`. The current filter is stored on
    the provider and displayed as a virtual `InfoNode` at the very top of the
    tree (`Filter active: <term> — N results`). A second title-bar action
    `pipelinesexplorer.clearFilter` (icon `$(close)`, visible only while a
    filter is active) clears it.
  - VS 2026: a `TextBox` with debounce ~200 ms placed above the `TreeView` in
    [PipelinesToolWindowControl.xaml](../../src/vs2026/ToolWindows/PipelinesToolWindowControl.xaml),
    with a small clear button (`KnownMonikers.Cancel`) shown only when not
    empty.
- **Match rules (v1)**:
  - Case-insensitive **substring** match. No regex / fuzzy.
  - Scope of `name`:
    - `PipelineNode`: `pipeline.name` OR `basename(detail.configuration.path)`.
    - `TemplateItemNode`: `basename(ref.path)`.
    - `ScriptItemNode`: only when `ref.filePath` is set
      → `basename(ref.filePath)`. Inline / unknown scripts are excluded.
  - `InfoNode`, connection header, organization/project/repository labels are
    **not** match targets — they appear in the filtered tree only as ancestors
    of a match (or, for the connection header, always).
- **Pruning + auto-expand**: only ancestors of at least one match are shown
  and they are expanded automatically. Matched leaves keep their normal label
  (no highlighting in v1 — codicons + `vscode.TreeItemLabel.highlights` can be
  added in a follow-up).
- **Scope of the scan (on-demand)**: filtering walks **only the subtrees that
  are already loaded** (i.e. the organizations / projects the user has
  expanded at least once during the current session). Organizations and
  projects never expanded are not crawled. The virtual info node tells the
  user when this is the case
  (`Filter limited to already-loaded scope — expand more orgs/projects to widen`).
- **Cache**: the existing `analysisCache` / VS 2026 lazy-load flags are
  reused. The filter triggers `getAnalysis` for every visible pipeline in
  scope; results stay in cache so a subsequent filter change is fast.
- **Cancellation**: changing the filter while a scan is in flight cancels it
  and starts a new one. A lightweight "Filtering…" virtual node is shown at
  the top during the scan.
- **Safety cap (v1)**: at most **500** pipelines analyzed per filter
  invocation. If exceeded, the virtual info node says
  `Filter scope limited to first 500 pipelines`. Tunable in v1.1 via a
  setting.
- **No persistence**: the filter is cleared on window close / signed-out
  state / refresh.

## 3. Cross-client parity

| Aspect            | VS Code                                               | VS 2026                                          | Notes |
| ----------------- | ----------------------------------------------------- | ------------------------------------------------ | ----- |
| Input UI          | `InputBox` from title-bar command                     | `TextBox` above tree with clear button           | Different host capabilities; behavior is equivalent. |
| Debounce          | n/a (input box is modal-ish)                          | ~200 ms                                          | |
| Active-filter UI  | Virtual `InfoNode` at top + `clearFilter` title icon  | Visible TextBox value + clear button + counter   | |
| Match engine      | Shared logic in TS / C# (see §4, §5)                  | Same rules                                       | Two implementations, same spec. |
| Scope             | Already-loaded subtrees only                          | Already-loaded subtrees only                     | |
| Cap               | 500 pipelines                                         | 500 pipelines                                    | |

No client-only divergence requested.

## 4. Scope — VS Code (`src/vscode/`)

### Files touched / created

- `src/pipelinesTreeProvider.ts`
  - Add `private currentFilter: string | undefined`.
  - Add `setFilter(term: string | undefined)` that stores the term, fires
    `_onDidChangeTreeData`, kicks off a background pre-walk
    (`runFilterScan`) with a `CancellationTokenSource`.
  - In `getChildren`, when a filter is active, route through new
    `filterChildren(parent, rawChildren)` that:
    - keeps a node if it is a match (per §2) **or** it has at least one
      descendant match (consult an in-memory `Set<string>` of matched node
      ids populated by the scan);
    - opens matched branches automatically (set
      `collapsibleState = Expanded` on synthetic copies, or rely on
      `TreeView.reveal` after the scan).
  - In `getChildren(undefined)`, prepend the synthetic info nodes
    `FilterStatusNode` (active term + counter / scanning state /
    cap-reached).
  - Run `runFilterScan` over the already-loaded org/project subtrees only:
    pre-call `getPipeline` for any pipeline missing detail, then
    `getAnalysis` for every pipeline up to the 500 cap, then
    `getTemplateAnalysis` recursively (same-repo only) to populate match
    ids. Reuse the existing caches.
- `src/extension.ts`
  - Register two new commands: `pipelinesexplorer.filter` (prompts via
    `showInputBox`, calls `setFilter`) and `pipelinesexplorer.clearFilter`
    (calls `setFilter(undefined)`).
  - Set a new context key `pipelinesexplorer.filterActive` so the
    `clearFilter` action shows only when relevant.
- `package.json`
  - Add the two commands under `contributes.commands` with localized titles
    and codicon icons (`$(search)`, `$(close)`).
  - Add the two title-bar entries under `menus.view/title` (and the right
    `commandPalette` `when` clauses, mirroring the existing pattern).
- `package.nls.json` + `package.nls.{de,es,fr,it,sv}.json`
  - New keys:
    - `command.filter.title` = `Pipelines Explorer: Filter by name…`
    - `command.clearFilter.title` = `Pipelines Explorer: Clear filter`
  - Localized values added to **all** existing locale files (English fallback
    OK if a translation is not ready, but the key **must** exist).
- `l10n/bundle.l10n.json` (+ all `bundle.l10n.<lang>.json`)
  - New strings used at runtime:
    - `Filter pipelines, templates and scripts by name`
      (input box prompt)
    - `Filter active: {0} — {1} result(s)`
    - `Filter active: {0} — scanning…`
    - `Filter active: {0} — no results in loaded scope`
    - `Filter scope limited to first {0} pipelines`
    - `Filter limited to already-loaded scope — expand more orgs/projects to widen`
- `src/test/extension.test.ts`
  - Unit tests for the new `matchesFilter(node, term)` helper and the
    propagate-up-to-ancestors rule on a fixture tree.

### New runtime dependencies

None.

## 5. Scope — VS 2026 (`src/vs2026/`)

### Files touched / created

- `ToolWindows/PipelinesToolWindowControl.xaml`
  - New `Grid.Row` between the header and the welcome panel (or just above
    `Row 3`, the `TreeView`) hosting:
    - a `TextBox` two-way bound to `FilterText` with `UpdateSourceTrigger=PropertyChanged`;
    - a small clear `Button` (`KnownMonikers.Cancel`) bound to
      `ClearFilterCommand`, visible only when `FilterText` is non-empty;
    - a `TextBlock` to the right showing the live result counter / scanning
      state, bound to `FilterStatusText`.
- `ViewModels/PipelinesViewModel.cs`
  - Add `FilterText` (debounced via a timer / `Task.Delay` cancellation
    pattern) and `FilterStatusText` properties.
  - Add `ClearFilterCommand`.
  - Add a private `RunFilterScanAsync(CancellationToken)` that walks
    `Roots` for already-loaded org / project / repo children, calls
    `_ado.GetPipelineAsync` for any pipeline missing detail, then
    `_analyzer.AnalyzeAsync` / `AnalyzeFileAsync` recursively up to the
    same 500-pipeline cap, building a `HashSet<string>` of matched node
    ids.
  - Apply the filter by setting an `IsVisibleUnderFilter` flag on
    `TreeNodeViewModel` (new property, defaults to `true`) and bind
    `TreeViewItem.Visibility` to it via a converter. (No new "tree
    rebuild" — keep the same nodes, just hide.)
- `ViewModels/TreeNodeViewModel.cs`
  - Add `IsVisibleUnderFilter` (`DataMember`, default `true`,
    `OnPropertyChanged`).
  - Add `IsAutoExpandedByFilter` (`DataMember`) used to remember the
    pre-filter `IsExpanded` value so we can restore it when the filter
    clears.
- `Resources/Strings.resx` (and **every** `Strings.<culture>.resx`)
  - New keys (English defaults; copy to other locales — translation can land
    in a follow-up but the key must exist):
    - `Filter_Placeholder` = `Filter by name…`
    - `Filter_Clear_Tooltip` = `Clear filter`
    - `Filter_Status_NoFilter` = `` (empty)
    - `Filter_Status_Format` = `{0} result(s)`
    - `Filter_Status_Scanning` = `Scanning…`
    - `Filter_Status_NoResults` = `No results in loaded scope`
    - `Filter_Status_Capped_Format` = `Showing first {0} pipelines`
    - `Filter_Status_LoadedScopeOnly` = `Only loaded organizations and projects are searched`
- `ViewModels/LocalizedStrings.cs`
  - Expose the new strings to the Remote UI XAML
    (mirrors existing wiring).

### New runtime NuGet references

None.

## 6. Out of scope

- Highlighting of the matched substring inside the label
  (`TreeItemLabel.highlights` in VS Code, `Run`-based highlight in VS 2026).
- Regex / fuzzy / multi-term boolean search.
- "Recent searches" list and persistence across sessions.
- Crawling organizations / projects the user has never expanded
  (would require a different cost model — possible v2).
- Filter on connection header, organization, project, repository labels.
- Filter on inline / unknown scripts.
- A settings entry to tune the 500-pipeline cap (v1.1).

## 7. Tests & validation

### Automated

- VS Code:
  [`src/test/extension.test.ts`](../../src/vscode/src/test/extension.test.ts)
  — new tests for `matchesFilter` (positive/negative on pipeline name, YAML
  basename, template path, script file path; inline / unknown scripts
  excluded) and for the "keep ancestors" walk on a fixture tree of
  `Node[]`.
- VS 2026: optionally add a small `xUnit` project under
  `src/vs2026/Tests/` later — not required for v1; the matcher is pure
  and can be exercised manually.

### Manual smoke — VS Code

1. Sign in, expand at least two projects with a mix of pipelines and
   template-heavy YAMLs.
2. Run `Pipelines Explorer: Filter by name…`, type a substring of a known
   pipeline name → the tree prunes to that pipeline's branch.
3. Type a substring of a YAML root file (e.g. `build.yml`) → matching
   pipelines visible even when the *pipeline name* doesn't contain the
   substring.
4. Type a substring of a known template / script file → ancestors (project /
   repository / pipeline / group) remain visible; siblings hidden.
5. Click the `Clear filter` title-bar icon → tree returns to its full state
   with previous expansion preserved.
6. Re-run filter, then `Refresh` → filter is cleared.
7. Sign out → filter is cleared; on next sign-in the tree starts unfiltered.

### Manual smoke — VS 2026

Same matrix as VS Code, driving the `TextBox` instead of the command,
including the clear button.

### Build commands

- `npm run compile` (in `src/vscode`).
- `npm test` (in `src/vscode`).
- `dotnet build src/vs2026/PipelinesExplorer.VisualStudio.csproj -c Debug`.

## 8. Risks / open questions

- **Cost on big projects**: on tenants with hundreds of pipelines the first
  filter invocation can fan out a lot of `getPipeline` + YAML downloads. The
  500 cap and "loaded scope only" rule are the main mitigations. The
  concurrency cap stays at the existing 8.
- **VS Code expansion**: VS Code does not expose a synchronous "expand all"
  for arbitrary tree items. We rely on the existing
  `TreeView.reveal(node, { expand: true })` for matched leaves *after* the
  scan completes. Open question: should we batch-reveal or only reveal the
  first N matches to avoid flicker on a long result list? **Assumption for
  v1**: reveal up to the first 50 matches.
- **VS 2026 Remote UI** binding for `Visibility` from a `DataMember` `bool`
  is already used elsewhere — confirmed working. The `Converter` (existing
  `BoolToVis`) can be reused.

## 9. Release impact

Per the matrix in
[project.instructions.md](../../.github/instructions/project.instructions.md):

- **VS Code**: **Yes — Minor**. Touches shipped `src/`, adds new commands
  under `contributes.commands`, adds new user-visible strings to
  `package.nls*.json` and `l10n/bundle.l10n.*.json`. Suggested bump source:
  `src/vscode/package.json` `version` → next minor.
- **VS 2026**: **Yes — Minor**. Touches shipped C# under
  `ViewModels/`, `ToolWindows/`, adds user-visible strings to
  `Resources/Strings*.resx`. Suggested bump source:
  `src/vs2026/source.extension.vsixmanifest` `Identity/@Version` → next
  minor.
- **Action**: do **not** bump versions, tag, push, or run release workflows
  until the author confirms after the feature lands.

## 10. Change log (filled during implementation)

- 2026-06-30 — Plan approved, no implementation started yet.

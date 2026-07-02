# 003 — Filter: "Show pipeline in context" (per-pipeline reveal + match highlight)

> Status: **Draft**
> Owner: rcappello
> Created: 2026-07-02
> Last updated: 2026-07-02

## 1. Context / problem

The current filter (plan 001) prunes the tree aggressively: only ancestors
and matched leaves are shown, every sibling is hidden. This answers *"where
is X?"* efficiently but loses context — once the user has found the
pipeline, they often want to see **what else it does** without leaving the
filtered view (typical follow-up: "OK, `build.yml` is here; what other
templates and scripts does that pipeline run?").

Today the only way to see the surrounding context is to clear the filter,
which throws away the reason the user was there in the first place. On
tenants with 50+ pipelines that means starting the exploration over.

Trigger: after upgrading to 0.3.0 on a real tenant, the developer's
immediate reaction was *"I want to see the full pipeline with the match
highlighted, not just the match."*

## 2. UX and expected behavior

### 2.1 Default behavior (unchanged)

Filter still prunes to matches by default. Sibling templates and scripts
under a matched pipeline stay hidden. This is the fast lookup mode.

### 2.2 New action: **Show pipeline in context**

- A **new context menu entry** on any `PipelineNode` that is currently
  visible under the filter (either self-matched by name or ancestor of a
  matched template/script):
  - Label: **Show pipeline in context** (icon: `$(eye)` in VS Code /
    `KnownMonikers.ShowAll` in VS 2026).
  - Enabled only while a filter is active.
- Clicking it flips a per-pipeline flag `IsUnprunedByUser`. When set:
  - Every descendant of that pipeline is forced visible, regardless of the
    match rule.
  - The matched substring is **highlighted** in the label of each matching
    template / script (see §2.4).
  - The pipeline gets a small badge in the tree label, e.g.
    `Build & Deploy · in context`, so the user knows why they see extra
    nodes.
- A second click on the same action reverts to *matches only*
  (menu label toggles to **Collapse to matches**).

### 2.3 Scope

- The flag is **per pipeline** — expanding one pipeline in context does
  not affect siblings.
- The flag is **filter-session-scoped**: cleared automatically when the
  filter is cleared, changed, or the session is reset. Not persisted
  across restarts.
- Recursion: when the user expands a nested `TemplateNode` under an
  in-context pipeline, its children are also shown in full (i.e. the
  "in context" flag inherits down to descendants).

### 2.4 Match highlighting

- Independent of the "in context" action — highlighting the matched
  substring in every visible matched leaf is useful in the default
  matches-only view too.
- VS Code: use
  [`TreeItemLabel.highlights`](https://code.visualstudio.com/api/references/vscode-api#TreeItemLabel)
  (already recorded as a follow-up in plan 001's *Out of scope* section).
  A `[start, end]` pair per match; single case-insensitive substring match.
- VS 2026: the tree cell uses a `TextBlock` for `Label`. Replace it with a
  `TextBlock` whose `Inlines` are built by a small converter or a
  view-model helper that splits the label around the term and applies a
  `Bold` + `Foreground={DynamicResource EnvironmentColors.SearchMatchHighlight...}`
  run. Fall back to `SystemColors.HighlightTextBrush` if the specific
  environment brush is not resolvable in Remote UI.

Highlight only when a filter is active. No highlight for the "in context"
extra descendants that don't match.

### 2.5 Not a global setting

Deliberately **no** toggle in Settings (VS Code) or Options (VS 2026) to
change the default from *matches only* to *full pipeline*. Reasoning:

- The right view depends on the moment — "find" vs "explore" — not on a
  persistent preference.
- A global *full pipeline* mode on a big tenant negates the point of
  filtering (14 pipelines × dozens of scripts).
- Per-pipeline reveal is discoverable via the context menu and needs zero
  configuration.

## 3. Cross-client parity

Default: **identical** behavior.

| Aspect                          | VS Code                                                       | VS 2026                                                     | Notes |
| ------------------------------- | ------------------------------------------------------------- | ----------------------------------------------------------- | ----- |
| Trigger                         | Context menu **Show pipeline in context** on `PipelineNode`   | Same, in the WPF context menu                               | Same command name across clients. |
| Toggle back                     | Second click (menu label flips to **Collapse to matches**)    | Same                                                        | |
| Persistence                     | None (filter-session-scoped)                                  | None                                                        | |
| Highlight engine                | `TreeItemLabel.highlights` (native)                           | `TextBlock.Inlines` with a converter                        | Two implementations, identical spec. |
| Highlight targets               | Matched `PipelineNode`, `TemplateNode`, `ScriptNode` labels   | Same                                                        | |
| Highlight in "in context" extras| Only when the leaf's basename contains the term               | Same                                                        | |
| Badge                           | `· in context` suffix in the description                      | Same, appended to `Description`                             | |

## 4. Scope — VS Code (`src/vscode/`)

### Files touched / created

- `src/pipelinesTreeProvider.ts`
  - Add `private _unprunedPipelines = new Set<string>()` (pipeline node
    id).
  - Add `togglePipelineInContext(pipelineId: string): void` that flips
    the flag and calls `_onDidChangeTreeData.fire()`.
  - In `filterChildren` (existing), when the parent pipeline id is in
    `_unprunedPipelines`, return the full unfiltered children.
  - In `getTreeItem`, when the node is a match and a filter is active,
    compute `label` as a `TreeItemLabel` with `highlights = [[i, i + term.length]]`.
  - Clear `_unprunedPipelines` from `setFilter(undefined)` and on session
    reset.
- `src/extension.ts`
  - Register two new commands:
    - `pipelinesexplorer.togglePipelineInContext` — takes the tree node
      as arg, calls `togglePipelineInContext(node.id)`.
    - (The command title is dynamic — see l10n below; VS Code doesn't
      support fully dynamic menu titles, so we register **two** commands
      and swap the `when` clauses on a `pipelinesexplorer.inContext.<id>`
      context key.)
  - Set the context key `pipelinesexplorer.inContext.<pipelineId>` when a
    pipeline is in context. Alternative: one boolean per selection —
    simpler, since the context menu opens per selected node.
- `package.json`
  - Two entries under `contributes.commands`:
    - `pipelinesexplorer.showPipelineInContext`
      (title: `Pipelines Explorer: Show pipeline in context`)
    - `pipelinesexplorer.collapsePipelineToMatches`
      (title: `Pipelines Explorer: Collapse pipeline to matches`)
  - Two `menus.view/item/context` entries, gated by
    `viewItem == pipeline && pipelinesexplorer.filterActive` plus the
    per-node context key.
- `package.nls.json` + every `package.nls.<lang>.json`
  - New keys:
    - `command.showPipelineInContext.title`
    - `command.collapsePipelineToMatches.title`
- `l10n/bundle.l10n.json` + every `bundle.l10n.<lang>.json`
  - `In context` (badge suffix).
- `src/test/extension.test.ts`
  - Unit tests: highlight range computation; `togglePipelineInContext`
    flipping visibility and clearing on filter reset.

### New runtime dependencies

None.

## 5. Scope — VS 2026 (`src/vs2026/`)

### Files touched / created

- `ViewModels/TreeNodeViewModel.cs`
  - Add `bool IsUnprunedByUser` (`[DataMember]`, default `false`) on
    `PipelineNode`. Propagated to descendants at visibility time (§2.3).
  - Add `IReadOnlyList<Inline>? LabelRuns` (or expose a
    `LabelHighlightRanges` int[] pair) used by the label template.
- `ViewModels/PipelinesViewModel.cs`
  - Add `TogglePipelineInContextCommand` bound to the pipeline node.
  - Extend `ApplyVisibilityRecursive` so that when the current subtree's
    root ancestor pipeline has `IsUnprunedByUser == true`, every
    descendant returns `visible = true`.
  - When applying visibility, also compute
    `LabelHighlightRanges` (or equivalent) for each visible matched
    leaf/pipeline.
  - Clear the in-context set on `ClearFilterInternal` and on session
    reset (already wired via `FilterText = string.Empty` in the sign-out
    path).
- `Commands/TogglePipelineInContextCommand.cs` **(new)**
  - Wire the context-menu action.
- `ToolWindows/PipelinesToolWindowControl.xaml`
  - Add the new context menu entry on the pipeline template (gated by
    `IsFilterActive` + `Kind == Pipeline`).
  - Replace the label `<TextBlock Text="{Binding Label}" />` with a
    `TextBlock` whose `Inlines` are built by a small
    `LabelHighlightConverter` (or use attached property) — highlighted
    runs use `EnvironmentColors.SearchMatchTextBrushKey`.
- `Resources/Strings.resx` + every `Strings.<culture>.resx`
  - New keys:
    - `Context_ShowPipelineInContext` = `Show pipeline in context`
    - `Context_CollapsePipelineToMatches` = `Collapse pipeline to matches`
    - `Filter_Badge_InContext` = `in context`

### New runtime NuGet references

None.

## 6. Out of scope

- Full-pipeline default (via setting or otherwise) — see §2.5.
- Highlighting inside the **tooltip** (path) — v1.1 if useful.
- Fuzzy / regex match — still on plan 001's *Out of scope* list.
- Auto-scroll to the first match after enabling *in context*.
- A separate command palette entry (only the context menu drives it —
  keeps discoverability tied to the selection).

## 7. Tests & validation

### Automated

- **VS Code** (`src/test/extension.test.ts`)
  - Highlight range computation: given `label = "prepare-parameters.yml"`
    and `term = "param"`, expect `[[8, 13]]`.
  - `togglePipelineInContext` returns the same pipeline's children to
    full when flipped on, prunes when flipped off.
  - Clearing the filter empties `_unprunedPipelines`.
- **VS 2026** — deferred to manual until we introduce a test project.

### Manual smoke — VS Code

1. Filter by `build.yml`. Right-click a matched pipeline → **Show pipeline
   in context**. → All sibling templates and the entire `Scripts` group
   become visible under that pipeline; `build.yml` is highlighted.
2. Same right-click again → menu now reads **Collapse pipeline to
   matches**; click → siblings hide again.
3. Enable *in context* on 3 different pipelines simultaneously → only
   those three show all children; others remain pruned.
4. Change the filter term → *in context* state is cleared.
5. Clear the filter → state cleared; tree returns to unfiltered.

### Manual smoke — VS 2026

Same matrix using the WPF context menu; verify highlight rendering under
Light, Dark, Blue, and High Contrast themes.

### Build commands

- `npm run compile` (in `src/vscode`).
- `npm test` (in `src/vscode`).
- `dotnet build src/vs2026/PipelinesExplorer.VisualStudio.csproj -c Debug`.

## 8. Risks / open questions

- **Highlighting cost in a big tree** — `TreeItemLabel.highlights` is
  native and cheap; the WPF `TextBlock.Inlines` build runs per-node and
  should still be sub-millisecond, but confirm with 500-pipeline tenants
  at manual smoke time.
- **Nested unprune propagation** — when the user unprunes a pipeline and
  later expands a `TemplateNode` under it, the freshly materialised
  children (from `BuildAnalysisChildren` — see fix in
  `fix/vs2026-filter-prune-descendants`) must respect the in-context
  flag. Implementation: the visibility recursion already walks from the
  parent down; passing an `inContext` flag along the recursion covers it.
- **Context menu label toggle** — VS Code menus don't support runtime
  label swap on a single command; we register two commands + `when`
  clauses. Reasonable trade-off; VS 2026 can do it directly via a
  `DataTrigger` on the menu item.
- **A11y** — the highlight must not be color-only. Screen-reader text
  should still read the full label. `TreeItemLabel.highlights` handles
  this natively; in VS 2026 we keep `AutomationProperties.Name` bound to
  the raw label so screen readers get the full text.

## 9. Release impact

Per the matrix in
[project.instructions.md](../../.github/instructions/project.instructions.md):

- **VS Code**: **Yes — Minor**. New commands under
  `contributes.commands`, new user-visible strings, new tree-provider
  code paths. Suggested bump source: `src/vscode/package.json` `version`
  → next minor.
- **VS 2026**: **Yes — Minor**. New context-menu action, new resource
  strings, new view-model plumbing. Suggested bump source:
  `src/vs2026/source.extension.vsixmanifest` `Identity/@Version` → next
  minor.
- **Action**: do **not** bump versions, tag, push, or run release
  workflows until the author confirms after the feature lands.

## 10. Change log (filled during implementation)

- 2026-07-02 — Plan drafted.

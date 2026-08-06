# 002 — PAT sign-in: per-organization fallback

> Status: **Ready to release**
> Release preparation: **Complete for VS Code and VS 2026**
> Owner: rcappello
> Created: 2026-07-03
> Last updated: 2026-08-06

## 1. Context / problem

PAT sign-in in Pipelines Explorer currently discovers organizations through
two calls to `app.vssps.visualstudio.com`:

- `GET /_apis/profile/profiles/me?api-version=7.1`
- `GET /_apis/accounts?api-version=…&memberId={memberId}`

Both endpoints only enumerate the caller's organizations when the PAT is
authorized against the *All accessible organizations* scope — a so-called
**global PAT**.

Microsoft has announced the retirement of global PATs for Azure DevOps
Services on **1 December 2026**
([aka.ms/GlobalPATDeprecation](https://aka.ms/GlobalPATDeprecation)).
After that date:

- Every existing global PAT stops working (Services only; Server unaffected).
- New global PATs can no longer be created.
- Microsoft's recommendation is Entra-backed, short-lived tokens.

Impact on Pipelines Explorer PAT sign-in mode:

- Users who paste an organization-scoped PAT (today or after 1 Dec 2026)
  see an empty tree with no explanation.
- After 1 Dec 2026 the current PAT flow cannot enumerate multiple
  organizations regardless of the token.

### 1.1 Additional evidence — `_apis/accounts` is already unreliable today

Field-tested on 2026-07-07 with a single *All accessible organizations*
PAT (the same redacted `memberId` was verified across every call).
Successive calls returned different, non-overlapping
sets:

| Client | Time | `count` | `accountName` values |
| ------ | ---- | ------- | -------------------- |
| VS 2026 extension | first cold start | 4 | org-a, org-b, org-c, org-d |
| VS 2026 extension | after sign-out + sign-in | 1 | org-c |
| PowerShell `Invoke-RestMethod` (same PAT, same memberId, same host) | ~minutes later | 2 | org-e, org-c |

Same PAT, same `memberId`, three completely different responses. Cookie
scrubbing (`HttpClientHandler.UseCookies = false`, landed on 2026-07-07 in
[`AdoClient.cs`](../../src/vs2026/AzureDevOps/AdoClient.cs)) did **not**
stabilise the result. SPS is routing to different regional shards whose
`accounts` views of the same identity diverge.

**Consequence for this plan:** the `_apis/accounts` path must be treated
as *best-effort* — not authoritative — even *before* 1 Dec 2026. The
per-organization fallback is the only way to give the user a
deterministic, complete list of the org(s) they care about.

We already recommend Microsoft sign-in in the READMEs (see plan **000-docs**
/ the `docs/pat-global-deprecation` branch). This plan covers the **PAT
fallback** so that users who cannot use Microsoft sign-in (air-gapped
tenants and service accounts) keep a working
experience by naming the organizations up front.

The user-requested behavior is:

> *Prendo il PAT in input, provo a chiamare `_apis/accounts`. Se torna 0 o
> 401 o errore, provo il path per-organizzazione.*

## 2. UX and expected behavior

### 2.1 Sign-in flow

1. User picks **Sign in with Personal Access Token**.
2. Extension prompts for the PAT (existing UI: `showInputBox` in VS Code,
   inline `PasswordBox` in the VS 2026 tool window).
3. Extension tries the existing *global* path:
   - `GET /_apis/profile/profiles/me` → get `memberId`.
   - `GET /_apis/accounts?memberId={memberId}`.
4. **Decision:**

    | Result of steps 3.a / 3.b                                   | Extension does                                                       |
    | ----------------------------------------------------------- | -------------------------------------------------------------------- |
    | 200 + non-empty `value[]`                                   | Existing behavior: store PAT in the shared credential slot, render tree with all returned orgs. |
    | 200 + empty `value[]`                                      | Enter **per-org fallback** (§2.2).                                    |
    | 401 / 403 on either call                                    | Show a warning explaining that the token cannot list orgs; enter **per-org fallback**. |
    | Network / 5xx on either call                                | Same as 401/403 (warning wording is different — "network"). Enter fallback. |

5. In the fallback, the PAT is **not** discarded — it is used against the
   per-organization endpoint, and stored under a new per-org key (§2.3).

### 2.2 Per-organization fallback

1. Extension shows a second prompt: **"Enter Azure DevOps organization
   name"** with placeholder `contoso` and help text explaining the
   deprecation (link to `aka.ms/GlobalPATDeprecation`).
2. Extension calls
   `GET https://dev.azure.com/{org}/_apis/projects?$top=1&api-version=7.1`
   with the pasted PAT to verify the token is valid for that organization.

    | Result                                             | Extension does                                                                 |
    | -------------------------------------------------- | ------------------------------------------------------------------------------ |
    | 200                                               | Store `<org, pat>` in the credential store (§2.3); render the tree with a single organization root; existing project/repo/pipeline flows unchanged (they already scope to `dev.azure.com/{org}/…`). |
    | 401 / 403                                         | Error: *"The token is not authorized for organization `{org}`."* Offer *Try another org* / *Cancel*.                                          |
    | 404                                               | Error: *"Organization `{org}` not found."* Offer *Try another org* / *Cancel*. |
    | Network / 5xx                                     | Error: *"Could not reach `dev.azure.com/{org}`."* Offer *Retry* / *Cancel*.    |

3. If the user cancels, sign-in is aborted and the tree returns to the
   signed-out state. The PAT is **not** persisted.

### 2.3 Multiple organizations under PAT sign-in

- The credential store gains a **per-organization** slot in addition to the
  existing global slot:
  - Global slot key (unchanged): `pipelinesexplorer.pat` — kept for
    backward compatibility with global PATs until 1 Dec 2026.
  - New per-org slot key: `pipelinesexplorer.pat.org.{org}` — one PAT per
    organization, plaintext value.
- A new command **`Add another Azure DevOps organization…`** (title-bar
  action + Command Palette entry in VS Code; header button in the VS 2026
  tool window) prompts for a new PAT + org name, verifies with the same
  `_apis/projects` probe, and appends the organization to the tree.
- The tree lists all organizations from:
  1. the global slot (if a global PAT is still valid), plus
  2. every per-org slot.

  Duplicates by canonical `org` name are collapsed; the per-org PAT wins if
  the same org is reachable both ways.
- **`Sign out` / `Reset`** both wipe every per-org slot in addition to the
  global slot.

### 2.4 Discoverability & copy

- The primary PAT prompt gets a new inline hint: *"Tip: after 1 Dec 2026 the
  extension will ask you for one organization at a time."*
- All new prompts and error messages route through the existing
  localization pipeline (VS Code `l10n`, VS 2026 `Strings.resx`).
- The fallback prompt links to `aka.ms/GlobalPATDeprecation` in the
  description text.

### 2.5 Non-goals in v1

- No auto-migration from global slot to per-org slots. Users who still
  have a working global PAT keep it until they reset.
- No UI to *remove* a single organization without wiping every PAT — that
  is a v1.1 enhancement.
- No detection of *Server* vs *Services* — Server URLs (`{host}/{coll}`)
  are still out of scope for PAT sign-in.

## 3. Cross-client parity

Default: **identical** behavior in VS Code and VS 2026.

| Aspect                     | VS Code                                                             | VS 2026                                                              | Notes |
| -------------------------- | ------------------------------------------------------------------- | -------------------------------------------------------------------- | ----- |
| Primary PAT prompt         | `vscode.window.showInputBox` (`password: true`)                     | Inline `PasswordBox` in the tool window                              | Existing UI, only copy changes. |
| Fallback org prompt        | `showInputBox` with `placeHolder: 'contoso'`                        | `TextBox` in a new "Add organization" panel of the tool window       | The VS 2026 shell has no free-form modal text input, so it lives inline (same pattern used today for the PAT prompt — see [`src/vs2026/README.md`](../../src/vs2026/README.md)). |
| Verification call          | `AdoClient.listProjects(org, top: 1)` (new helper)                  | `AdoClient.ListProjectsAsync(org, top: 1)` (new helper)              | Both add a `top` parameter to the existing method. |
| Credential storage         | `SecretStorage` under `pipelinesexplorer.pat.org.{org}`             | Windows Credential Manager, target `PipelinesExplorer/pat/{org}`     | |
| "Add another org" command  | `pipelinesexplorer.addOrganization` (title-bar + palette)          | `Commands/AddOrganizationCommand.cs` (title-bar action)              | |
| Sign-out / Reset semantics | Wipes global + all per-org slots                                    | Same                                                                 | Existing `Reset` command must be updated. |
| Localization               | `package.nls*.json` + `l10n/bundle.l10n.*.json`                     | `Resources/Strings.resx` + every `Strings.<culture>.resx`            | English defaults for all locales in v1. |

## 4. Scope — VS Code (`src/vscode/`)

### Files touched / created

- `src/adoClient.ts`
  - Add `listProjects(org: string, top?: number): Promise<Project[]>`
    helper (or overload the existing one) that calls
    `GET https://dev.azure.com/{org}/_apis/projects?$top={top}&api-version=7.1`
    and returns the raw list.
  - Add `probeOrganization(org: string): Promise<'ok' | 'unauthorized' | 'not-found' | 'network-error'>`
    thin wrapper used by the fallback prompt.
  - Existing `listAccounts()` / `getProfile()` remain — they are the *first*
    attempt.
- `src/authService.ts`
  - Add a `PatCredentialStore` abstraction with two backing keys:
    - `pipelinesexplorer.pat` (global, existing).
    - `pipelinesexplorer.pat.org.{org}` (new; `org` lowercased,
      URI-safe).
  - Add `listPerOrgPats(): Promise<{ org: string; pat: string }[]>`.
  - Add `savePerOrgPat(org: string, pat: string): Promise<void>`.
  - Add `clearAllPats(): Promise<void>` used by `Reset`.
- `src/authProvider.ts`
  - Update the PAT sign-in flow to implement §2.1 → §2.2.
  - When the tree is enumerated under PAT sign-in, iterate:
    1. The global slot's organizations from `listAccounts`.
    2. Every per-org slot.
    De-duplicate by canonical org name.
- `src/pipelinesTreeProvider.ts`
  - Route each org root to its correct PAT (global vs per-org) when
    calling the ADO client. Requires a `PatSelector` that maps
    `org -> pat`.
- `src/extension.ts`
  - Register the new command `pipelinesexplorer.addOrganization`
    (title-bar icon `$(add)`, visible when signed in with PAT).
  - Update the `Reset` command to call `authService.clearAllPats()`.
- `package.json`
  - Add `pipelinesexplorer.addOrganization` under
    `contributes.commands` and `menus.view/title`.
  - New `when` clause: `pipelinesexplorer.signedIn && pipelinesexplorer.authKind == 'pat'`.
- `package.nls.json` + every `package.nls.<lang>.json`
  - `command.addOrganization.title` = `Pipelines Explorer: Add Azure DevOps organization…`
- `l10n/bundle.l10n.json` + every `bundle.l10n.<lang>.json`
  - New strings (English defaults are the source of truth; translations
    can land in a follow-up but the key **must** exist):
    - `Enter the name of the Azure DevOps organization (e.g. contoso).`
    - `Global Azure DevOps PATs are being retired on 1 December 2026 ({0}). Enter an organization name below to continue with a per-organization token.`
    - `The token is not authorized for organization "{0}".`
    - `Organization "{0}" not found.`
    - `Could not reach dev.azure.com/{0}.`
    - `Added organization "{0}".`
- `src/test/extension.test.ts`
  - Unit tests for `authService.savePerOrgPat` / `listPerOrgPats`
    (mock `SecretStorage`).
  - Unit tests for the `authProvider` fallback decision table (§2.1).

### New runtime dependencies

None.

## 5. Scope — VS 2026 (`src/vs2026/`)

### Files touched / created

- `AzureDevOps/AdoClient.cs`
  - Add `Task<IReadOnlyList<AdoProject>> ListProjectsAsync(string org, int top, CancellationToken ct)`.
  - Add `Task<OrgProbeResult> ProbeOrganizationAsync(string org, CancellationToken ct)`
    with `enum OrgProbeResult { Ok, Unauthorized, NotFound, NetworkError }`.
- `Auth/PatCredentialStore.cs`
  - Add per-org read/write:
    - Target format: `PipelinesExplorer/pat/{orgLower}`.
    - Methods:
      `SavePerOrgPat(string org, string pat)`,
      `ReadPerOrgPat(string org)`,
      `ListPerOrgPats() : IReadOnlyList<(string Org, string Pat)>`,
      `ClearAllPats()`.
  - Existing global-slot methods stay; existing `Clear` method calls
    `ClearAllPats` internally.
- `Auth/AdoAuthService.cs`
  - Update `SignInWithPatAsync` to implement §2.1 → §2.2.
  - Expose `AddOrganizationAsync(string org, string pat, CancellationToken ct)`
    that runs the probe then persists on success.
  - Update `PatSession` (or equivalent) so the enumerator returns every
    per-org token alongside the global one.
- `Auth/IAdoAuthHeaderProvider.cs`
  - Existing interface may need a signature change to accept an `org`
    hint so `AdoClient` picks the right PAT per call. (Alternative:
    a per-org `AdoClient` factory.)
- `Commands/AddOrganizationCommand.cs` **(new)**
  - Title-bar action on the pipelines tool window; shows the inline
    add-org panel described in §3.
- `Commands/ResetCommand.cs`
  - Wipe every per-org PAT in addition to the global slot.
- `ViewModels/PipelinesViewModel.cs`
  - Enumerate roots as `global ∪ per-org`, de-duplicated.
  - Expose an `AddOrganizationCommand` used by the new tool-window panel.
- `ToolWindows/PipelinesToolWindowControl.xaml`
  - Add an "Add organization" inline panel (initially collapsed) with
    an org `TextBox`, a masked PAT `PasswordBox`, and a *Verify & add*
    button.
- `Resources/Strings.resx` + every `Strings.<culture>.resx`
  - New keys (English defaults for all locales; translation follow-up):
    - `Pat_AddOrg_Header` = `Add Azure DevOps organization`
    - `Pat_AddOrg_OrgLabel` = `Organization`
    - `Pat_AddOrg_OrgHint` = `e.g. contoso`
    - `Pat_AddOrg_PatLabel` = `Personal Access Token`
    - `Pat_AddOrg_DeprecationNotice` = `Global PATs retire on 1 December 2026. See aka.ms/GlobalPATDeprecation.`
    - `Pat_AddOrg_Verify` = `Verify & add`
    - `Pat_AddOrg_Cancel` = `Cancel`
    - `Pat_Error_Unauthorized_Format` = `The token is not authorized for organization "{0}".`
    - `Pat_Error_NotFound_Format` = `Organization "{0}" not found.`
    - `Pat_Error_Network_Format` = `Could not reach dev.azure.com/{0}.`
    - `Pat_Info_Added_Format` = `Added organization "{0}".`
- `ViewModels/LocalizedStrings.cs`
  - Expose the new strings.

### New runtime NuGet references

None.

## 6. Out of scope

- Automatic migration from global PAT to per-org PATs.
- UI for removing a single organization / rotating a per-org PAT (v1.1).
- Detection of Azure DevOps Server and dedicated Server sign-in (separate
  plan).
- Any Entra-backed token change — Microsoft sign-in path is untouched.
- Highlighting or reflecting the org-source (global vs per-org) in the
  tree UI (kept transparent; the user just sees organizations).

## 7. Tests & validation

### Automated

- **VS Code**
  ([`src/test/extension.test.ts`](../../src/vscode/src/test/extension.test.ts))
  - `authService.listPerOrgPats` returns every persisted entry.
  - `authService.clearAllPats` wipes both global and per-org keys.
  - `authProvider`'s decision table (§2.1) — mock `AdoClient` responses
    for `listAccounts` and verify the correct branch is taken.
- **VS 2026** — a small `xUnit` test project can be added under
  `src/vs2026/Tests/` if time allows; the credential store logic is pure
  and unit-testable.

### Manual smoke — VS Code

1. Sign in with a **global PAT** → tree renders as today.
2. Sign in with an **organization-scoped PAT** → per-org prompt appears;
   enter a valid org → tree renders with just that org.
3. Enter an invalid org (`this-org-does-not-exist`) → shows *"not found"*,
   offers *Try another*.
4. Enter a valid org but a bad PAT → shows *"not authorized"*, offers
   *Try another*.
5. With an org-scoped session running, run
   `Pipelines Explorer: Add Azure DevOps organization…` → prompts,
   verifies, adds a second root.
6. `Sign out` → tree empties, all PATs cleared. Re-sign-in prompts again.
7. `Reset` → same as sign-out plus stored sign-in method forgotten.

### Manual smoke — VS 2026

Same matrix, driving the inline add-organization panel.

### Build commands

- `npm run compile` (in `src/vscode`).
- `npm test` (in `src/vscode`).
- `dotnet build src/vs2026/PipelinesExplorer.VisualStudio.csproj -c Debug`.

## 8. Risks / open questions

- **Credential-store growth** — a user with N orgs stores N PATs. On VS
  Code `SecretStorage` this is fine. On VS 2026 the credential manager
  gains N targets under `PipelinesExplorer/pat/…`. `Reset` must wipe them
  all (see §5).
- **`_apis/accounts` returns 200 + empty list** for an org-scoped PAT: we
  treat that as "fall back", which is the safest default. Open question:
  should we still show the "Add organization" panel *even if the user
  is signed in with a working global PAT* — as a way to layer a scoped
  PAT on top? Assumption for v1: **yes**, the command is always available
  under PAT sign-in.
- **Case-sensitivity of the org name** — Azure DevOps treats it as
  case-insensitive. Store the lowercased form as the credential key and
  the original form as the display name.
- **Cross-client credential portability** — the two clients do not share
  a credential store; each maintains its own set of per-org PATs. Called
  out here so users understand each client asks separately.
- **Feature flag** — should the fallback be gated behind a preview
  setting? Assumption for v1: **no**, ship on by default (it activates
  only when the current flow already fails).
- **Existing PAT prompt copy** — VS Code and VS 2026 both currently show
  "paste a PAT with at least Code (Read), Build (Read), Project and
  Team (Read)". The prompt must gain a mention that a *global* PAT is
  optional and only useful before 1 Dec 2026. Keep the change scoped to
  UI copy — no behavior change.

## 9. Release impact

Per the matrix in
[project.instructions.md](../../.github/instructions/project.instructions.md):

- **VS Code**: **Yes — Minor**. Adds a new command, new user-visible
  strings and a new sign-in code path. Suggested bump source:
  `src/vscode/package.json` `version` → next minor after 0.3.x.
- **VS 2026**: **Yes — Minor**. Adds a new command, new tool-window
  panel, new resource strings and a new sign-in code path. Suggested
  bump source: `src/vs2026/source.extension.vsixmanifest`
  `Identity/@Version` → next minor after 0.3.x.
- **Action**: do **not** bump versions, tag, push, or run release
  workflows until the author confirms after the feature lands.

## 10. Change log (filled during implementation)

- 2026-07-03 — Plan drafted.
- 2026-07-07 — Added §1.1 with field evidence that `_apis/accounts` is
  already non-deterministic today (same PAT + same `memberId`, three
  different results). `UseCookies=false` applied to `AdoClient` on VS 2026
  as a hygiene fix (does not stabilise the SPS response but eliminates
  cookie cross-contamination between calls).
- 2026-07-07 — **Phase A (VS Code backend) landed.** New
  `src/vscode/src/patCredentialStore.ts` with global + per-org slots
  (`AzureDevOpsPAT` retained for BC, `pipelinesexplorer.pat.org.{org}`
  new), `Memento`-tracked index. `AdoClient.probeOrganization(org)`
  added. Pass-through methods on `AuthService`. 15 total unit tests
  passing (8 new for the store).
- 2026-07-07 — **Phase B (VS Code UX) landed.** Discovery + fallback wired
  into `signInWithPat`; new `pipelinesexplorer.addOrganization` command
  (title-bar + Command Palette). `AuthService.getHeaders(orgHint)` routes
  per-org PATs, in-memory cache refreshed on save/delete. Tree
  enumeration merges global `_apis/accounts` result with per-org slots
  (deduplicated). New localized strings routed inline through
  `vscode.l10n.t()` — English defaults on all locales.
- 2026-07-07 — **Phase B.1 (VS Code UX polish) landed.** Rolling org
  history (cap 20) survives `SignOut`, wiped by `Reset`. Prompt opens as
  QuickPick over history when available, plus "Type another…" option.
  Clipboard sniff pre-fills input box with the org portion of any
  `dev.azure.com/{org}` or `{org}.visualstudio.com` URL. 10 new tests
  (5 history + 5 clipboard URL parsing).
- 2026-07-07 — **Phase C (VS 2026 backend) landed.** `PatCredentialStore.cs`
  rewritten with per-org slots (Credential Manager targets under
  `PipelinesExplorer.VisualStudio:AzureDevOpsPAT/{org}`) + history via
  `JsonStateStore`. New `AdoClient.ProbeOrganizationAsync` returning
  `OrgProbeResult`. `IAdoAuthHeaderProvider.GetAuthHeaderAsync` gained
  `orgHint`. `AdoAuthService` keeps an in-memory PAT cache and pass-
  through methods. `SignOut` clears per-org slots but preserves history;
  `Reset` wipes everything. No user-visible UI change in this phase.
- 2026-07-07 — **Phase D (VS 2026 UX) landed.** Inline
  *Add Azure DevOps organization* panel in the tool window with header,
  deprecation notice, history quick-picks, org + PAT inputs, error
  banner, and Verify & add / Cancel buttons. Sign-in with PAT now runs
  the SPS discovery and, on empty/error, auto-opens the panel with the
  just-entered PAT pre-filled. New `AddOrganizationCommand` (Tools menu)
  reopens the same panel to add more organizations. Tree merges global
  discovery + per-org slots. 15 new `AddOrg_*` strings routed through
  `LocalizedStrings`.
- 2026-07-08 — **Phase D UX fixes.**
  - **Bug**: sign-in with org-scoped PAT triggered the unauthorized
    recovery dialog and forced a sign-out, because `RefreshAsync` treated
    the deterministic SPS 401 (`_apis/profile/profiles/me`) as a real
    credentials failure. Fix: for PAT sessions the SPS-level 401 is
    recognised as expected and swallowed; Microsoft sign-in still surfaces
    the recovery dialog on real 401s.
  - **UX**: cancelling the fallback panel/prompt right after a fresh
    PAT sign-in now signs the user out and discards the just-entered
    token (VS Code + VS 2026 both), so a fake or unverifiable PAT no
    longer lingers as a zombie session on next activation. Cancel from
    the *Add another organization* command on an already-working session
    leaves the existing per-org PATs untouched.
  - **Copy**: the `unauthorized` probe error was reworded from
    *"The token is not authorized for organization …"* to
    *"The token was rejected for organization …. This can happen if the
    token is invalid, revoked, or not scoped to this organization."* on
    both clients.
  - **Layout (VS 2026)**: the empty-tree placeholder used to render as
    an `InfoNode` inside the `TreeView` — its label was ellipsized on
    narrow tool windows because the tree's internal `ScrollViewer`
    absorbs the overflow into a horizontal scrollbar rather than passing
    a bounded width down to items. Refactored to a standalone `TextBlock`
    with `TextWrapping="Wrap"` (`EmptyRootsMessage` + `ShouldShowTree`
    mutex on the VM) that sits in the same `Grid.Row` as the tree.
  - **XAML binding fix**: the "Recently used" history buttons in the
    add-org panel bound their command via
    `RelativeSource={RelativeSource AncestorType=UserControl}`, but the
    Remote UI root is a `<DataTemplate>` and there is no `UserControl`
    ancestor — the binding failed silently and clicks were no-ops.
    Switched to `ElementName=AddOrgPanel` against the panel's outer
    `Border`, which is the reliable Remote UI pattern.
  - **Tree message parity**: the *"no organizations"* placeholder now
    uses `Strings.Tree_NoOrganizations_Pat` when signed in via PAT —
    *"No Azure DevOps organizations added yet. Use 'Add Azure DevOps
    organization…' to name one."* — instead of the Microsoft-only
    *"…found for this tenant"* wording. VS Code already had the
    corresponding branch.
- 2026-08-05 — **Release 0.4.0 preparation.** Promoted the root and per-client
  changelogs, updated both publishing version sources, and synchronized the VS
  Code lockfile metadata. `npm install --package-lock-only --ignore-scripts
  --offline` confirmed that the manifest and lockfile are aligned, audited 671
  packages, and reported 0 vulnerabilities. The VS 2026 Release build produced
  a validated `PipelinesExplorer.VisualStudio-0.4.0.vsix` whose embedded
  manifest reports `0.4.0.0`.
- 2026-08-06 — **VS Code release gate accepted.** The corporate npm proxy now
  serves the required `brace-expansion` versions but still returns `404` for
  the transitive development-only packages `fast-uri@3.1.5` and
  `js-yaml@4.3.1`. These packages support build tooling and are not shipped in
  the VSIX. The earlier post-implementation validation remains valid: 25 tests
  passed, and the manifest and lockfile are aligned. The local clean-install
  limitation is accepted as an environment constraint. The GitHub release
  workflow must complete `npm ci`, the production build, and VSIX packaging
  before its dependent Marketplace publishing job can run. Tags and pushes
  remain with the author.

## 11. Status

- **VS Code client**: complete (Phases A + B + B.1). 25 tests passing.
- **VS 2026 client**: complete (Phases C + D + D fixes).
- Plan is ready to close on the next joint release.

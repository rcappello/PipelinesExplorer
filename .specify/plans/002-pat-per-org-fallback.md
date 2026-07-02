# 002 — PAT sign-in: per-organization fallback

> Status: **Draft**
> Owner: rcappello
> Created: 2026-07-03
> Last updated: 2026-07-03

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

We already recommend Microsoft sign-in in the READMEs (see plan **000-docs**
/ the `docs/pat-global-deprecation` branch). This plan covers the **PAT
fallback** so that users who cannot use Microsoft sign-in (air-gapped
tenants, service accounts, Azure DevOps Server users) keep a working
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

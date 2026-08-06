import * as vscode from 'vscode';

/**
 * Backing keys used by {@link PatCredentialStore}. Exported for tests.
 * `GLOBAL_SECRET_KEY` matches the pre-existing key written by
 * {@link AzureDevOpsAuthenticationProvider} so upgrades from earlier
 * versions keep the user's saved PAT.
 */
export const GLOBAL_SECRET_KEY = 'AzureDevOpsPAT';
export const PER_ORG_SECRET_PREFIX = 'pipelinesexplorer.pat.org.';
export const PER_ORG_INDEX_KEY = 'pipelinesexplorer.pat.org.index';
/**
 * Rolling list of canonical org names the user has *ever* signed in with
 * via the per-organization flow. Used to offer suggestions on the next
 * `Add organization` prompt. Survives `signOut` (which clears the PAT
 * slots) but is wiped by `Reset` alongside every other stored artefact.
 */
export const PER_ORG_HISTORY_KEY = 'pipelinesexplorer.pat.org.history';
/** Cap on the number of entries retained by the org-name history. */
export const PER_ORG_HISTORY_LIMIT = 20;

export interface PerOrgPat {
	/** Canonical (lowercased) org name, ready to use in `dev.azure.com/{org}` URLs. */
	readonly org: string;
	readonly pat: string;
}

/**
 * Storage abstraction for Azure DevOps PATs used by the PAT sign-in flow.
 *
 * Two slot kinds are supported:
 *
 * - A **global** slot (`GLOBAL_SECRET_KEY`) that holds an *All accessible
 *   organizations* PAT — the historical behavior. Still readable/writable
 *   after 1 Dec 2026 but expected to become empty over time.
 * - **Per-organization** slots (`PER_ORG_SECRET_PREFIX + <org>`) that hold
 *   one PAT scoped to a single Azure DevOps organization. Introduced by
 *   plan `002-pat-per-org-fallback` because
 *   `app.vssps.visualstudio.com/_apis/accounts` is not a deterministic
 *   enumerator across tenants (see §1.1 of that plan).
 *
 * `vscode.SecretStorage` has no "list keys" API, so the set of known
 * per-org slots is tracked in a `Memento` under {@link PER_ORG_INDEX_KEY}.
 * Callers must go through this class rather than talking to the underlying
 * `SecretStorage` directly to keep the index consistent.
 */
export class PatCredentialStore {
	constructor(
		private readonly secrets: vscode.SecretStorage,
		private readonly state: vscode.Memento,
	) { }

	// ---------- global slot ----------

	getGlobalPat(): Thenable<string | undefined> {
		return this.secrets.get(GLOBAL_SECRET_KEY);
	}

	setGlobalPat(pat: string): Thenable<void> {
		return this.secrets.store(GLOBAL_SECRET_KEY, pat);
	}

	deleteGlobalPat(): Thenable<void> {
		return this.secrets.delete(GLOBAL_SECRET_KEY);
	}

	// ---------- per-org slots ----------

	/**
	 * Persist `pat` for `org`. Overwrites any previous value for the same
	 * (canonicalized) org name.
	 */
	async savePerOrgPat(org: string, pat: string): Promise<void> {
		const canonical = canonicalizeOrg(org);
		await this.secrets.store(PER_ORG_SECRET_PREFIX + canonical, pat);
		const index = this.readIndex();
		if (!index.includes(canonical)) {
			index.push(canonical);
			index.sort((a, b) => a.localeCompare(b));
			await this.state.update(PER_ORG_INDEX_KEY, index);
		}
		await this.rememberInHistory(canonical);
	}

	getPerOrgPat(org: string): Thenable<string | undefined> {
		return this.secrets.get(PER_ORG_SECRET_PREFIX + canonicalizeOrg(org));
	}

	async deletePerOrgPat(org: string): Promise<void> {
		const canonical = canonicalizeOrg(org);
		await this.secrets.delete(PER_ORG_SECRET_PREFIX + canonical);
		const index = this.readIndex().filter(o => o !== canonical);
		await this.state.update(PER_ORG_INDEX_KEY, index.length > 0 ? index : undefined);
	}

	/**
	 * Return every known per-org PAT. Entries missing from `SecretStorage`
	 * (e.g. cleared out-of-band) are dropped from the index on the fly.
	 */
	async listPerOrgPats(): Promise<PerOrgPat[]> {
		const index = this.readIndex();
		const result: PerOrgPat[] = [];
		const survivors: string[] = [];
		for (const org of index) {
			const pat = await this.secrets.get(PER_ORG_SECRET_PREFIX + org);
			if (pat) {
				result.push({ org, pat });
				survivors.push(org);
			}
		}
		if (survivors.length !== index.length) {
			await this.state.update(PER_ORG_INDEX_KEY, survivors.length > 0 ? survivors : undefined);
		}
		return result;
	}

	/** Return the canonical org names that currently have a stored PAT. */
	listPerOrgNames(): string[] {
		return this.readIndex();
	}

	async clearAllPerOrgPats(): Promise<void> {
		const index = this.readIndex();
		for (const org of index) {
			await this.secrets.delete(PER_ORG_SECRET_PREFIX + org);
		}
		await this.state.update(PER_ORG_INDEX_KEY, undefined);
	}

	// ---------- cross-slot ----------

	async clearAll(): Promise<void> {
		await this.deleteGlobalPat();
		await this.clearAllPerOrgPats();
		await this.state.update(PER_ORG_HISTORY_KEY, undefined);
	}

	// ---------- history (survives sign-out) ----------

	/**
	 * Return org names the user has previously added via the per-org flow,
	 * most-recent first. The list survives `signOut` — only `clearAll`
	 * (backing `Reset`) wipes it.
	 */
	getHistory(): string[] {
		const raw = this.state.get<unknown>(PER_ORG_HISTORY_KEY);
		if (!Array.isArray(raw)) {
			return [];
		}
		return raw.filter((v): v is string => typeof v === 'string' && v.length > 0);
	}

	private async rememberInHistory(canonicalOrg: string): Promise<void> {
		const current = this.getHistory();
		const deduped = [canonicalOrg, ...current.filter(o => o !== canonicalOrg)];
		const trimmed = deduped.slice(0, PER_ORG_HISTORY_LIMIT);
		await this.state.update(PER_ORG_HISTORY_KEY, trimmed);
	}

	private readIndex(): string[] {
		const raw = this.state.get<unknown>(PER_ORG_INDEX_KEY);
		if (!Array.isArray(raw)) {
			return [];
		}
		return raw.filter((v): v is string => typeof v === 'string' && v.length > 0);
	}
}

/**
 * Canonical form for an Azure DevOps organization name: trimmed and
 * lowercased. Matches how `dev.azure.com` treats the org portion of a URL
 * (case-insensitive) and gives us a stable key for both storage and
 * de-duplication.
 */
export function canonicalizeOrg(org: string): string {
	return org.trim().toLowerCase();
}

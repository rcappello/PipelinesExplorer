import * as vscode from 'vscode';
import type { AdoClient } from './adoClient';
import { AzureDevOpsAuthenticationProvider } from './authProvider';
import { LoggingService } from './LoggingService';
import { PatCredentialStore, PerOrgPat, canonicalizeOrg } from './patCredentialStore';

/**
 * Azure DevOps "well-known" application id used as scope when requesting
 * a Microsoft Entra ID (AAD) token to call ADO REST APIs.
 */
const ADO_RESOURCE_ID = '499b84ac-1321-427f-aa17-267ca6975798';
const ADO_SCOPES = [`${ADO_RESOURCE_ID}/.default`];

/** Azure Resource Manager — used to enumerate the user's available tenants. */
const ARM_RESOURCE_ID = 'https://management.azure.com';

const SIGN_IN_KIND_KEY = 'pipelinesexplorer.signInKind';
const MS_TENANT_KEY = 'pipelinesexplorer.microsoftTenant';
const MS_TENANT_NAME_KEY = 'pipelinesexplorer.microsoftTenantName';
const CONTEXT_SIGNED_IN = 'pipelinesexplorer.signedIn';
const CONTEXT_SIGN_IN_KIND = 'pipelinesexplorer.signInKind';

export type SignInKind = 'microsoft' | 'pat';

export interface AdoAuthHeaders {
	authorization: string;
	'content-type'?: string;
}

export interface TenantInfo {
	tenantId: string;
	displayName: string;
	defaultDomain?: string;
}

export interface AdoSession {
	kind: SignInKind;
	accessToken: string;
	/** Display label for the account (best-effort). */
	accountLabel: string;
	/** Tenant id (Microsoft sign-in only). */
	tenantId?: string;
}

/**
 * Unified auth facade for Azure DevOps. Supports two providers:
 *  - 'microsoft': built-in Microsoft (AAD) provider, returns a Bearer token.
 *  - 'pat':      our custom provider that prompts for a PAT, used as Basic auth.
 *
 * Only ONE active sign-in at a time. The chosen kind is persisted in globalState
 * so that subsequent activations restore the right session silently.
 */
export class AuthService implements vscode.Disposable {
	private _onDidChangeSession = new vscode.EventEmitter<AdoSession | undefined>();
	readonly onDidChangeSession: vscode.Event<AdoSession | undefined> = this._onDidChangeSession.event;

	private currentSession: AdoSession | undefined;
	private readonly subscriptions: vscode.Disposable[] = [];
	private readonly patCredentials: PatCredentialStore;
	/**
	 * In-memory mirror of the per-org PATs (canonical org → PAT). Kept because
	 * {@link getHeaders} is a synchronous API and cannot await SecretStorage on
	 * every ADO request. Refreshed on initialize, sign-in, addOrganization, and
	 * SecretStorage change events.
	 */
	private readonly perOrgPatCache = new Map<string, string>();
	private adoClient: AdoClient | undefined;

	constructor(
		private readonly context: vscode.ExtensionContext,
		private readonly patProvider: AzureDevOpsAuthenticationProvider,
		private readonly logger: LoggingService,
	) {
		this.patCredentials = new PatCredentialStore(context.secrets, context.globalState);
		// React to external session changes (e.g. user signs out from Accounts menu).
		this.subscriptions.push(
			vscode.authentication.onDidChangeSessions(async e => {
				const kind = this.getStoredKind();
				if (!kind) {
					return;
				}
				if (kind === 'microsoft' && e.provider.id === 'microsoft') {
					await this.refreshSilently();
				} else if (kind === 'pat' && e.provider.id === AzureDevOpsAuthenticationProvider.id) {
					await this.refreshSilently();
				}
			}),
		);
	}

	dispose(): void {
		this.subscriptions.forEach(d => d.dispose());
		this._onDidChangeSession.dispose();
	}

	get session(): AdoSession | undefined {
		return this.currentSession;
	}

	/** Best-effort silent restore using the previously chosen provider. */
	async initialize(): Promise<void> {
		await this.reloadPerOrgPatCache();
		const kind = this.getStoredKind();
		this.logger.logInfo(`AuthService.initialize: stored kind = ${kind ?? '<none>'}`);
		if (!kind) {
			await this.setContext(undefined);
			return;
		}
		try {
			const restored = await this.acquireSession(kind, /*createIfNone*/ false);
			this.logger.logInfo(`AuthService.initialize: silent restore ${restored ? 'succeeded' : 'returned no session'}`);
		} catch (err) {
			this.logger.logError('AuthService.initialize: silent restore failed', err);
			await this.setContext(undefined);
		}
	}

	/**
	 * Late-bind the {@link AdoClient} instance. Breaks the initialization
	 * cycle: `AdoClient` depends on `AuthService.getHeaders()`, but the PAT
	 * sign-in fallback flow in this class also needs the client to hit
	 * `_apis/accounts` and `_apis/projects`.
	 */
	attachAdoClient(client: AdoClient): void {
		this.adoClient = client;
	}

	async signInWithMicrosoft(): Promise<AdoSession | undefined> {
		this.logger.logInfo('signInWithMicrosoft invoked');
		if (!(await this.confirmReplaceIfSignedIn('Microsoft'))) {
			return this.currentSession;
		}
		return this.acquireSession('microsoft', true);
	}

	/**
	 * Return the list of Microsoft Entra tenants the currently signed-in user
	 * has access to, by calling the ARM `/tenants` endpoint with a token NOT
	 * scoped to any specific tenant.
	 */
	async listAvailableTenants(): Promise<TenantInfo[]> {
		const armSession = await vscode.authentication.getSession(
			'microsoft',
			[`${ARM_RESOURCE_ID}/.default`],
			{ createIfNone: false, silent: true },
		) ?? await vscode.authentication.getSession(
			'microsoft',
			[`${ARM_RESOURCE_ID}/.default`],
			{ createIfNone: true },
		);
		if (!armSession) {
			throw new Error('Microsoft sign-in is required to list tenants.');
		}
		const res = await fetch(`${ARM_RESOURCE_ID}/tenants?api-version=2022-12-01`, {
			headers: { authorization: `Bearer ${armSession.accessToken}` },
		});
		if (!res.ok) {
			throw new Error(`ARM /tenants returned HTTP ${res.status}: ${await res.text()}`);
		}
		const body = await res.json() as { value?: Array<{ tenantId?: string; displayName?: string; defaultDomain?: string; domains?: string[] }> };
		const tenants: TenantInfo[] = (body.value ?? [])
			.filter(t => typeof t.tenantId === 'string')
			.map(t => ({
				tenantId: t.tenantId!,
				displayName: t.displayName ?? t.defaultDomain ?? t.tenantId!,
				defaultDomain: t.defaultDomain ?? t.domains?.[0],
			}));
		return tenants;
	}

	/**
	 * Switch the active Microsoft sign-in to a specific Entra ID tenant.
	 * Pass `undefined` to clear the override and fall back to the default tenant.
	 * Optionally accepts the tenant `displayName` so the UI can show a friendly
	 * label without having to re-query ARM.
	 */
	async switchTenant(tenantId: string | undefined, displayName?: string): Promise<AdoSession | undefined> {
		await this.context.globalState.update(MS_TENANT_KEY, tenantId);
		await this.context.globalState.update(MS_TENANT_NAME_KEY, tenantId ? displayName : undefined);
		this.logger.logInfo(`Microsoft tenant override = ${tenantId ?? '<default>'}${displayName ? ` (${displayName})` : ''}`);
		return this.acquireSession('microsoft', true);
	}

	async signInWithPat(): Promise<AdoSession | undefined> {
		this.logger.logInfo('signInWithPat invoked');
		if (!(await this.confirmReplaceIfSignedIn('PAT'))) {
			return this.currentSession;
		}
		// We own the provider, so call it directly (bypassing the consent dialog
		// of vscode.authentication.getSession that can silently swallow the request).
		let raw: vscode.AuthenticationSession;
		try {
			this.logger.logInfo('Prompting for new PAT...');
			raw = await this.patProvider.createSession([]);
		} catch (err) {
			if (err instanceof Error && err.message === 'PAT is required') {
				this.logger.logInfo('PAT input box cancelled by user');
				return undefined;
			}
			throw err;
		}

		// Use the freshly-created session directly. We can't rely on
		// vscode.authentication.getSession({createIfNone:false}) here because
		// VS Code's consent gate may silently return undefined on the very first
		// call after our own provider stored a token (no prior session approval
		// recorded for this extension).
		const session: AdoSession = {
			kind: 'pat',
			accessToken: raw.accessToken,
			accountLabel: raw.account?.label ?? 'Personal Access Token',
		};
		this.currentSession = session;
		await this.context.globalState.update(SIGN_IN_KIND_KEY, 'pat');
		await this.reloadPerOrgPatCache();
		await this.setContext(session);
		this._onDidChangeSession.fire(session);
		this.logger.logInfo('PAT sign-in completed');

		// Plan 002 §2.1: try the historical `_apis/accounts` discovery. On
		// empty / unauthorized / network the token likely is org-scoped, so run
		// the per-organization fallback prompt (§2.2). Any failure here is
		// non-fatal: the session is already valid and any per-org PATs already
		// stored still work.
		await this.tryPerOrgFallbackAfterSignIn(raw.accessToken);
		return session;
	}

	/**
	 * Run the discovery + per-org fallback flow described in plan 002 §2.1
	 * and §2.2. Called right after a fresh PAT sign-in with the raw PAT the
	 * user just entered (needed for the org probe when `_apis/accounts`
	 * returns nothing usable).
	 */
	private async tryPerOrgFallbackAfterSignIn(rawPat: string): Promise<void> {
		const client = this.adoClient;
		if (!client) {
			this.logger.logWarning('tryPerOrgFallbackAfterSignIn: AdoClient not attached, skipping discovery');
			return;
		}
		let decision: 'ok' | 'fallback' = 'fallback';
		try {
			const profile = await client.getProfile();
			const orgs = await client.listOrganizations(profile.id);
			decision = orgs.length > 0 ? 'ok' : 'fallback';
			this.logger.logInfo(`PAT discovery: listOrganizations returned ${orgs.length} org(s) → ${decision}`);
		} catch (err) {
			this.logger.logWarning(`PAT discovery failed, entering per-org fallback: ${err instanceof Error ? err.message : String(err)}`);
		}
		if (decision === 'ok') {
			return;
		}
		const deprecationNotice = vscode.l10n.t(
			'Global Azure DevOps PATs are being retired on 1 December 2026 (aka.ms/GlobalPATDeprecation). Enter an organization name below to continue with a per-organization token.',
		);
		vscode.window.showInformationMessage(deprecationNotice);
		const addedOrg = await this.promptAndAddOrganization(rawPat);
		if (!addedOrg) {
			// The user cancelled the fallback prompt without adding any org.
			// Roll the sign-in back so an unverified PAT (potentially a typo or
			// fake) doesn't linger in SecretStorage and surface as a zombie
			// session on the next activation. Only do this on the fresh-sign-in
			// path — the "Add another organization" command doesn't come
			// through here.
			this.logger.logInfo('Per-org fallback cancelled — signing out to discard the unverified PAT');
			try {
				await this.signOut();
			} catch (err) {
				this.logger.logError('Sign-out on fallback cancel failed', err);
			}
		}
	}

	/**
	 * Prompt for an Azure DevOps organization name, probe it with `pat`, and
	 * on success store it under a per-org slot and refresh consumers. Loops
	 * on `unauthorized` / `not-found` / `network-error` so the user can
	 * correct the org name without restarting the flow.
	 *
	 * Returns the org name on success, or `undefined` if the user cancelled.
	 */
	async promptAndAddOrganization(pat: string): Promise<string | undefined> {
		const client = this.adoClient;
		if (!client) {
			vscode.window.showErrorMessage(vscode.l10n.t('Pipelines Explorer is not fully initialized. Try again in a moment.'));
			return undefined;
		}
		// Best-effort seed for the input box: prefer the org name discovered
		// in the clipboard (e.g. the user just copied a dev.azure.com URL),
		// fall back to whatever the previous iteration typed after an error.
		let suggested: string | undefined = await sniffOrgFromClipboard();
		while (true) {
			// If the user has previously added organizations, offer them as
			// picks so the second-time experience is a single click.
			const historyOrg = await this.pickOrgFromHistory(suggested);
			const org = historyOrg ?? await this.askOrgViaInputBox(suggested);
			if (!org) {
				return undefined;
			}
			const result = await client.probeOrganization(org, pat);
			if (result === 'ok') {
				await this.patCredentials.savePerOrgPat(org, pat);
				this.perOrgPatCache.set(canonicalizeOrg(org), pat);
				this._onDidChangeSession.fire(this.currentSession);
				vscode.window.showInformationMessage(vscode.l10n.t('Added organization "{0}".', org));
				return org;
			}
			const message = result === 'unauthorized'
				? vscode.l10n.t('The token was rejected for organization "{0}". This can happen if the token is invalid, revoked, or not scoped to this organization.', org)
				: result === 'not-found'
					? vscode.l10n.t('Organization "{0}" not found.', org)
					: vscode.l10n.t('Could not reach dev.azure.com/{0}.', org);
			const tryAgain = vscode.l10n.t('Try another');
			const cancel = vscode.l10n.t('Cancel');
			const pick = await vscode.window.showErrorMessage(message, { modal: false }, tryAgain, cancel);
			if (pick !== tryAgain) {
				return undefined;
			}
			suggested = org;
		}
	}

	private async pickOrgFromHistory(prefill: string | undefined): Promise<string | undefined> {
		const history = this.patCredentials.getHistory();
		if (history.length === 0) {
			return undefined;
		}
		const typeItLabel = vscode.l10n.t('$(edit) Type another organization name…');
		const items: Array<vscode.QuickPickItem & { org?: string; typeIt?: boolean }> = history
			.map(o => ({ label: `$(organization) ${o}`, org: o }));
		items.push({ label: '', kind: vscode.QuickPickItemKind.Separator });
		items.push({ label: typeItLabel, typeIt: true });
		const pick = await vscode.window.showQuickPick(items, {
			title: vscode.l10n.t('Select an Azure DevOps organization for this PAT'),
			placeHolder: prefill
				? vscode.l10n.t('Suggested from clipboard: {0}', prefill)
				: vscode.l10n.t('Recently used organizations'),
			ignoreFocusOut: true,
		});
		if (!pick) {
			return undefined;
		}
		return pick.typeIt ? undefined : pick.org;
	}

	private async askOrgViaInputBox(prefill: string | undefined): Promise<string | undefined> {
		const input = await vscode.window.showInputBox({
			ignoreFocusOut: true,
			prompt: vscode.l10n.t('Enter the name of the Azure DevOps organization.'),
			placeHolder: vscode.l10n.t('e.g. contoso'),
			value: prefill,
			valueSelection: prefill ? [0, prefill.length] : undefined,
			validateInput: v => v.trim().length === 0 ? vscode.l10n.t('Organization name is required') : undefined,
		});
		return input?.trim() || undefined;
	}

	async signOut(): Promise<void> {
		const kind = this.getStoredKind();
		this.logger.logInfo(`signOut invoked (kind=${kind ?? '<none>'})`);
		if (kind === 'pat') {
			await this.patProvider.removeSession(AzureDevOpsAuthenticationProvider.id);
			// Plan 002 §2.3: signing out clears every per-org slot too.
			await this.patCredentials.clearAllPerOrgPats();
			this.perOrgPatCache.clear();
		}
		await this.context.globalState.update(SIGN_IN_KIND_KEY, undefined);
		this.currentSession = undefined;
		await this.setContext(undefined);
		this._onDidChangeSession.fire(undefined);
	}

	/**
	 * Wipe ALL persisted state (PAT secret + chosen provider). Used by the
	 * "Reset" command to recover from inconsistent local state.
	 */
	async reset(): Promise<void> {
		this.logger.logInfo('AuthService.reset: clearing all stored credentials');
		try {
			await this.patProvider.removeSession(AzureDevOpsAuthenticationProvider.id);
		} catch (err) {
			this.logger.logError('reset: removeSession failed (ignored)', err);
		}
		await this.patCredentials.clearAll();
		await this.context.globalState.update(SIGN_IN_KIND_KEY, undefined);
		await this.context.globalState.update(MS_TENANT_KEY, undefined);
		await this.context.globalState.update(MS_TENANT_NAME_KEY, undefined);
		this.currentSession = undefined;
		await this.setContext(undefined);
		this._onDidChangeSession.fire(undefined);
	}

	// ---------- Per-organization PAT storage (plan 002 fallback backend) ----------

	/** List every stored per-organization PAT (canonical org name + PAT). */
	listPerOrgPats(): Promise<PerOrgPat[]> {
		return this.patCredentials.listPerOrgPats();
	}

	/** Canonical names of the organizations that currently have a per-org PAT. */
	listPerOrgNames(): string[] {
		return [...this.perOrgPatCache.keys()].sort((a, b) => a.localeCompare(b));
	}

	/** Persist a PAT for a specific organization. Overwrites any previous value. */
	async savePerOrgPat(org: string, pat: string): Promise<void> {
		await this.patCredentials.savePerOrgPat(org, pat);
		this.perOrgPatCache.set(canonicalizeOrg(org), pat);
	}

	/** Look up a stored PAT for a specific organization. */
	getPerOrgPat(org: string): Thenable<string | undefined> {
		return this.patCredentials.getPerOrgPat(org);
	}

	/** Remove the PAT for a single organization without touching the others. */
	async deletePerOrgPat(org: string): Promise<void> {
		await this.patCredentials.deletePerOrgPat(org);
		this.perOrgPatCache.delete(canonicalizeOrg(org));
	}

	/** Wipe both the global and every per-organization PAT slot. */
	async clearAllPats(): Promise<void> {
		await this.patCredentials.clearAll();
		this.perOrgPatCache.clear();
	}

	private async confirmReplaceIfSignedIn(label: string): Promise<boolean> {
		if (!this.currentSession) {
			return true;
		}
		const replaceLabel = vscode.l10n.t('Replace');
		const pick = await vscode.window.showWarningMessage(
			vscode.l10n.t('Already signed in as {0} ({1}). Replace with a new {2} sign-in?', this.currentSession.accountLabel, this.currentSession.kind, label),
			{ modal: true },
			replaceLabel,
		);
		if (pick !== replaceLabel) {
			this.logger.logInfo('Sign-in cancelled (user kept existing session)');
			return false;
		}
		await this.signOut();
		return true;
	}

	/** Build the auth headers required for a REST call. */
	getHeaders(orgHint?: string): AdoAuthHeaders {
		if (!this.currentSession) {
			throw new Error('Not signed in to Azure DevOps.');
		}
		if (this.currentSession.kind === 'microsoft') {
			return {
				authorization: `Bearer ${this.currentSession.accessToken}`,
				'content-type': 'application/json',
			};
		}
		const pat = this.pickPatForOrg(orgHint);
		const basic = Buffer.from(`:${pat}`).toString('base64');
		return {
			authorization: `Basic ${basic}`,
			'content-type': 'application/json',
		};
	}

	/**
	 * Choose the right PAT for `orgHint`. When a per-organization PAT is
	 * stored for `orgHint` it wins (plan 002 §2.3); otherwise the session's
	 * primary PAT is used, which matches the historical behavior and covers
	 * calls that are not org-scoped (SPS `profiles/me`, `accounts`).
	 */
	private pickPatForOrg(orgHint: string | undefined): string {
		if (!this.currentSession || this.currentSession.kind !== 'pat') {
			throw new Error('pickPatForOrg called without an active PAT session');
		}
		if (orgHint) {
			const perOrg = this.perOrgPatCache.get(canonicalizeOrg(orgHint));
			if (perOrg) {
				return perOrg;
			}
		}
		return this.currentSession.accessToken;
	}

	private async reloadPerOrgPatCache(): Promise<void> {
		const entries = await this.patCredentials.listPerOrgPats();
		this.perOrgPatCache.clear();
		for (const e of entries) {
			this.perOrgPatCache.set(e.org, e.pat);
		}
	}

	private getStoredKind(): SignInKind | undefined {
		return this.context.globalState.get<SignInKind>(SIGN_IN_KIND_KEY);
	}

	getStoredTenant(): string | undefined {
		return this.context.globalState.get<string>(MS_TENANT_KEY);
	}

	/** Friendly display name of the persisted tenant override (if any). */
	getStoredTenantName(): string | undefined {
		return this.context.globalState.get<string>(MS_TENANT_NAME_KEY);
	}

	private async refreshSilently(): Promise<void> {
		const kind = this.getStoredKind();
		if (!kind) {
			return;
		}
		try {
			await this.acquireSession(kind, false);
		} catch {
			this.currentSession = undefined;
			await this.setContext(undefined);
			this._onDidChangeSession.fire(undefined);
		}
	}

	private async acquireSession(kind: SignInKind, createIfNone: boolean): Promise<AdoSession | undefined> {
		let raw: vscode.AuthenticationSession | undefined;
		let tenant: string | undefined;

		if (kind === 'microsoft') {
			tenant = this.getStoredTenant();
			// VS Code's built-in Microsoft auth provider recognises the special
			// `VSCODE_TENANT:<id-or-domain>` scope as a tenant filter and routes
			// the token request to that tenant.
			const scopes = tenant ? [...ADO_SCOPES, `VSCODE_TENANT:${tenant}`] : ADO_SCOPES;
			raw = await vscode.authentication.getSession('microsoft', scopes, { createIfNone });
		} else {
			// We own the PAT provider, so go straight to it. We can't use
			// vscode.authentication.getSession here because VS Code's consent
			// gate is never recorded for our extension (signInWithPat bypasses
			// it by calling patProvider.createSession directly), so getSession
			// would always return undefined on silent restore even when the
			// PAT is present in SecretStorage.
			const sessions = await this.patProvider.getSessions([]);
			raw = sessions[0];
			if (!raw && createIfNone) {
				raw = await this.patProvider.createSession([]);
			}
		}

		if (!raw) {
			this.currentSession = undefined;
			await this.setContext(undefined);
			this._onDidChangeSession.fire(undefined);
			return undefined;
		}

		const tenantId = kind === 'microsoft' ? (extractTenantFromJwt(raw.accessToken) ?? tenant) : undefined;
		const session: AdoSession = {
			kind,
			accessToken: raw.accessToken,
			accountLabel: raw.account?.label ?? (kind === 'pat' ? 'Personal Access Token' : 'Microsoft Account'),
			tenantId,
		};

		this.currentSession = session;
		await this.context.globalState.update(SIGN_IN_KIND_KEY, kind);
		await this.setContext(session);
		this._onDidChangeSession.fire(session);
		return session;
	}

	private async setContext(session: AdoSession | undefined): Promise<void> {
		await vscode.commands.executeCommand('setContext', CONTEXT_SIGNED_IN, !!session);
		await vscode.commands.executeCommand('setContext', CONTEXT_SIGN_IN_KIND, session?.kind);
	}
}

/** Decode the `tid` claim from a JWT without verifying the signature. */
function extractTenantFromJwt(token: string): string | undefined {
	try {
		const parts = token.split('.');
		if (parts.length < 2) { return undefined; }
		const pad = '='.repeat((4 - (parts[1].length % 4)) % 4);
		const json = Buffer.from(parts[1].replace(/-/g, '+').replace(/_/g, '/') + pad, 'base64').toString('utf8');
		const payload = JSON.parse(json) as { tid?: string };
		return typeof payload.tid === 'string' ? payload.tid : undefined;
	} catch {
		return undefined;
	}
}

/**
 * If the system clipboard currently holds an Azure DevOps URL like
 * `https://dev.azure.com/{org}/…` or `https://{org}.visualstudio.com/…`,
 * return the extracted `{org}` in canonical form. Silently returns
 * `undefined` on any failure (clipboard unavailable, non-URL content, etc.).
 * Best-effort UX helper for the "Add organization" prompt — never blocks
 * on user cancellation or throws.
 */
async function sniffOrgFromClipboard(): Promise<string | undefined> {
	try {
		const raw = await vscode.env.clipboard.readText();
		return parseOrgFromUrl(raw);
	} catch {
		return undefined;
	}
}

/**
 * Parse the organization name out of an Azure DevOps URL, if `text` looks
 * like one. Recognises the modern `dev.azure.com/{org}` shape and the
 * legacy `{org}.visualstudio.com` shape. Returns `undefined` on any other
 * input. Pure function to make the clipboard integration testable without
 * a live VS Code clipboard.
 */
export function parseOrgFromUrl(text: string | undefined): string | undefined {
	if (!text) {
		return undefined;
	}
	const trimmed = text.trim();
	if (trimmed.length === 0 || trimmed.length > 2048) {
		return undefined;
	}
	const devAzure = /^https?:\/\/dev\.azure\.com\/([^/\s?#]+)/i.exec(trimmed);
	if (devAzure) {
		return devAzure[1].toLowerCase();
	}
	const legacy = /^https?:\/\/([a-z0-9][a-z0-9-]*)\.visualstudio\.com\b/i.exec(trimmed);
	if (legacy) {
		return legacy[1].toLowerCase();
	}
	return undefined;
}

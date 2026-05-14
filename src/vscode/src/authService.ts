import * as vscode from 'vscode';
import { AzureDevOpsAuthenticationProvider } from './authProvider';
import { LoggingService } from './LoggingService';

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

	constructor(
		private readonly context: vscode.ExtensionContext,
		private readonly patProvider: AzureDevOpsAuthenticationProvider,
		private readonly logger: LoggingService,
	) {
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
		await this.setContext(session);
		this._onDidChangeSession.fire(session);
		this.logger.logInfo('PAT sign-in completed');
		return session;
	}

	async signOut(): Promise<void> {
		const kind = this.getStoredKind();
		this.logger.logInfo(`signOut invoked (kind=${kind ?? '<none>'})`);
		if (kind === 'pat') {
			await this.patProvider.removeSession(AzureDevOpsAuthenticationProvider.id);
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
		await this.context.secrets.delete('AzureDevOpsPAT');
		await this.context.globalState.update(SIGN_IN_KIND_KEY, undefined);
		await this.context.globalState.update(MS_TENANT_KEY, undefined);
		await this.context.globalState.update(MS_TENANT_NAME_KEY, undefined);
		this.currentSession = undefined;
		await this.setContext(undefined);
		this._onDidChangeSession.fire(undefined);
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
	getHeaders(): AdoAuthHeaders {
		if (!this.currentSession) {
			throw new Error('Not signed in to Azure DevOps.');
		}
		if (this.currentSession.kind === 'microsoft') {
			return {
				authorization: `Bearer ${this.currentSession.accessToken}`,
				'content-type': 'application/json',
			};
		}
		const basic = Buffer.from(`:${this.currentSession.accessToken}`).toString('base64');
		return {
			authorization: `Basic ${basic}`,
			'content-type': 'application/json',
		};
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

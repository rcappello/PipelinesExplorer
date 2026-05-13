import * as vscode from 'vscode';
import { AzureDevOpsAuthenticationProvider } from './authProvider';
import { LoggingService } from './LoggingService';

/**
 * Azure DevOps "well-known" application id used as scope when requesting
 * a Microsoft Entra ID (AAD) token to call ADO REST APIs.
 */
const ADO_RESOURCE_ID = '499b84ac-1321-427f-aa17-267ca6975798';
const ADO_SCOPES = [`${ADO_RESOURCE_ID}/.default`];

const SIGN_IN_KIND_KEY = 'pipelinesexplorer.signInKind';
const CONTEXT_SIGNED_IN = 'pipelinesexplorer.signedIn';
const CONTEXT_SIGN_IN_KIND = 'pipelinesexplorer.signInKind';

export type SignInKind = 'microsoft' | 'pat';

export interface AdoAuthHeaders {
	authorization: string;
	'content-type'?: string;
}

export interface AdoSession {
	kind: SignInKind;
	accessToken: string;
	/** Display label for the account (best-effort). */
	accountLabel: string;
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
		this.currentSession = undefined;
		await this.setContext(undefined);
		this._onDidChangeSession.fire(undefined);
	}

	private async confirmReplaceIfSignedIn(label: string): Promise<boolean> {
		if (!this.currentSession) {
			return true;
		}
		const pick = await vscode.window.showWarningMessage(
			`Already signed in as ${this.currentSession.accountLabel} (${this.currentSession.kind}). Replace with a new ${label} sign-in?`,
			{ modal: true },
			'Replace',
		);
		if (pick !== 'Replace') {
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

		if (kind === 'microsoft') {
			raw = await vscode.authentication.getSession('microsoft', ADO_SCOPES, { createIfNone });
		} else {
			raw = await vscode.authentication.getSession(
				AzureDevOpsAuthenticationProvider.id, [], { createIfNone },
			);
		}

		if (!raw) {
			this.currentSession = undefined;
			await this.setContext(undefined);
			this._onDidChangeSession.fire(undefined);
			return undefined;
		}

		const session: AdoSession = {
			kind,
			accessToken: raw.accessToken,
			accountLabel: raw.account?.label ?? (kind === 'pat' ? 'Personal Access Token' : 'Microsoft Account'),
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

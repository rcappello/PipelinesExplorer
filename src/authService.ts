import * as vscode from 'vscode';
import { AzureDevOpsAuthenticationProvider } from './authProvider';

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

	constructor(private readonly context: vscode.ExtensionContext) {
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
		if (!kind) {
			await this.setContext(undefined);
			return;
		}
		try {
			await this.acquireSession(kind, /*createIfNone*/ false);
		} catch {
			await this.setContext(undefined);
		}
	}

	async signInWithMicrosoft(): Promise<AdoSession | undefined> {
		return this.acquireSession('microsoft', true);
	}

	async signInWithPat(): Promise<AdoSession | undefined> {
		return this.acquireSession('pat', true);
	}

	async signOut(): Promise<void> {
		const kind = this.getStoredKind();
		if (kind === 'pat') {
			const session = await vscode.authentication.getSession(
				AzureDevOpsAuthenticationProvider.id, [], { createIfNone: false },
			);
			if (session) {
				// Our custom provider exposes the underlying SecretStorage delete via removeSession.
				// We don't have a direct handle here, so we fall back to triggering it via the provider.
				// The cleanest way is to look up the provider instance through the extension context;
				// instead we simply clear the secret using the same key indirectly: the provider
				// listens to SecretStorage changes and emits the proper event.
				await this.context.secrets.delete('AzureDevOpsPAT');
			}
		}
		// We deliberately DO NOT sign the user out from the global Microsoft provider:
		// other extensions may rely on it. We only forget our local choice.
		await this.context.globalState.update(SIGN_IN_KIND_KEY, undefined);
		this.currentSession = undefined;
		await this.setContext(undefined);
		this._onDidChangeSession.fire(undefined);
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

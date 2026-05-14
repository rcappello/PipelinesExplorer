import * as vscode from 'vscode';
import { AuthService } from './authService';
import { LoggingService } from './LoggingService';

export class AdoUnauthorizedError extends Error {
	constructor(public readonly status: number, message: string) {
		super(message);
		this.name = 'AdoUnauthorizedError';
	}
}

export interface AdoProfile {
	id: string;
	displayName: string;
}

export interface AdoOrganization {
	accountId: string;
	accountName: string;
	accountUri: string;
}

export interface AdoProject {
	id: string;
	name: string;
	description?: string;
	url: string;
	state: string;
}

export interface AdoPipeline {
	id: number;
	name: string;
	folder: string;
	url: string;
	revision?: number;
}

export interface AdoPipelineDetail extends AdoPipeline {
	configuration?: {
		type?: string;
		path?: string;
		repository?: {
			id?: string;
			type?: string;
			fullName?: string;
			name?: string;
		};
	};
}

export interface AdoRepository {
	id: string;
	name: string;
	url?: string;
	webUrl?: string;
	defaultBranch?: string;
	project?: { id: string; name: string };
}

interface AdoListResponse<T> {
	count: number;
	value: T[];
}

interface AccountsResponse {
	count: number;
	value: Array<{ accountId: string; accountName: string; accountUri?: string }>;
}

/**
 * Thin REST wrapper around the Azure DevOps APIs we need.
 * Uses the AuthService to attach the right Authorization header
 * (Bearer for Microsoft sessions, Basic for PAT sessions).
 */
export class AdoClient {
	constructor(
		private readonly auth: AuthService,
		private readonly logger: LoggingService,
	) { }

	async getProfile(): Promise<AdoProfile> {
		const url = 'https://app.vssps.visualstudio.com/_apis/profile/profiles/me?api-version=7.1';
		return this.getJson<AdoProfile>(url);
	}

	async listOrganizations(memberId: string): Promise<AdoOrganization[]> {
		const url = `https://app.vssps.visualstudio.com/_apis/accounts?api-version=7.1&memberId=${encodeURIComponent(memberId)}`;
		const res = await this.getJson<AccountsResponse>(url);
		return res.value.map(a => ({
			accountId: a.accountId,
			accountName: a.accountName,
			accountUri: a.accountUri ?? `https://dev.azure.com/${a.accountName}`,
		}));
	}

	async listProjects(organizationName: string): Promise<AdoProject[]> {
		const url = `https://dev.azure.com/${encodeURIComponent(organizationName)}/_apis/projects?api-version=7.1&stateFilter=wellFormed&$top=1000`;
		const res = await this.getJson<AdoListResponse<AdoProject>>(url);
		return res.value;
	}

	async listPipelines(organizationName: string, projectName: string): Promise<AdoPipeline[]> {
		const url = `https://dev.azure.com/${encodeURIComponent(organizationName)}/${encodeURIComponent(projectName)}/_apis/pipelines?api-version=7.1&$top=1000`;
		const res = await this.getJson<AdoListResponse<AdoPipeline>>(url);
		return res.value;
	}

	async getPipeline(organizationName: string, projectName: string, pipelineId: number): Promise<AdoPipelineDetail> {
		const url = `https://dev.azure.com/${encodeURIComponent(organizationName)}/${encodeURIComponent(projectName)}/_apis/pipelines/${pipelineId}?api-version=7.1`;
		return this.getJson<AdoPipelineDetail>(url);
	}

	/** Look up a Git repository by id. Returns undefined on 404. */
	async getRepository(
		organizationName: string,
		projectName: string,
		repositoryId: string,
	): Promise<AdoRepository | undefined> {
		const url = `https://dev.azure.com/${encodeURIComponent(organizationName)}/${encodeURIComponent(projectName)}/_apis/git/repositories/${encodeURIComponent(repositoryId)}?api-version=7.1`;
		try {
			return await this.getJson<AdoRepository>(url);
		} catch (err) {
			if (err instanceof Error && /\b404\b/.test(err.message)) {
				return undefined;
			}
			throw err;
		}
	}

	/**
	 * Fetch the raw text content of a file from a Git repository hosted in Azure DevOps.
	 * Returns undefined if the file is missing (404) or the repo is not a TfsGit repo.
	 * When `branch` is provided, the file is fetched from that branch; otherwise the
	 * repository's default branch is used.
	 */
	async getFileContent(
		organizationName: string,
		projectName: string,
		repositoryId: string,
		path: string,
		branch?: string,
	): Promise<string | undefined> {
		const normalized = path.startsWith('/') ? path : `/${path}`;
		let url = `https://dev.azure.com/${encodeURIComponent(organizationName)}/${encodeURIComponent(projectName)}/_apis/git/repositories/${encodeURIComponent(repositoryId)}/items?path=${encodeURIComponent(normalized)}&api-version=7.1&includeContent=true&$format=text`;
		if (branch) {
			url += `&versionDescriptor.versionType=branch&versionDescriptor.version=${encodeURIComponent(branch)}`;
		}
		return this.getText(url);
	}

	/**
	 * List heads (branches) of a Git repository. Returns short branch names
	 * (without the `refs/heads/` prefix).
	 */
	async listBranches(
		organizationName: string,
		projectName: string,
		repositoryId: string,
	): Promise<string[]> {
		const url = `https://dev.azure.com/${encodeURIComponent(organizationName)}/${encodeURIComponent(projectName)}/_apis/git/repositories/${encodeURIComponent(repositoryId)}/refs?filter=heads/&api-version=7.1&$top=1000`;
		const res = await this.getJson<AdoListResponse<{ name: string }>>(url);
		return res.value
			.map(r => r.name.replace(/^refs\/heads\//, ''))
			.sort((a, b) => a.localeCompare(b));
	}

	private async getText(url: string): Promise<string | undefined> {
		const headers = this.auth.getHeaders();
		this.logger.logDebug(`GET ${url}`);
		const response = await fetch(url, { headers: headers as unknown as Record<string, string> });
		if (response.status === 404) {
			return undefined;
		}
		if (!response.ok) {
			const body = await safeReadBody(response);
			const msg = `ADO REST call failed: ${response.status} ${response.statusText} for ${url}${body ? ` :: ${body}` : ''}`;
			this.logger.logError(msg);
			if (response.status === 401 || response.status === 403) {
				throw new AdoUnauthorizedError(
					response.status,
					vscode.l10n.t('Azure DevOps rejected the credentials ({0}). The stored token may be expired or revoked.', response.status),
				);
			}
			throw new Error(msg);
		}
		return await response.text();
	}

	private async getJson<T>(url: string): Promise<T> {
		const headers = this.auth.getHeaders();
		this.logger.logDebug(`GET ${url}`);
		const response = await fetch(url, { headers: headers as unknown as Record<string, string> });

		if (!response.ok) {
			const body = await safeReadBody(response);
			const msg = `ADO REST call failed: ${response.status} ${response.statusText} for ${url}${body ? ` :: ${body}` : ''}`;
			this.logger.logError(msg);
			if (response.status === 401 || response.status === 403) {
				throw new AdoUnauthorizedError(
					response.status,
					vscode.l10n.t('Azure DevOps rejected the credentials ({0}). The stored token may be expired or revoked.', response.status),
				);
			}
			throw new Error(msg);
		}

		// ADO returns HTML on redirect-to-login when auth fails silently. Detect it.
		const contentType = response.headers.get('content-type') ?? '';
		if (!contentType.includes('application/json')) {
			throw new AdoUnauthorizedError(
				401,
				vscode.l10n.t('Unexpected non-JSON response from {0}. Authentication may have expired.', url),
			);
		}

		return await response.json() as T;
	}
}

async function safeReadBody(response: Response): Promise<string | undefined> {
	try {
		const text = await response.text();
		return text.length > 500 ? `${text.slice(0, 500)}…` : text;
	} catch {
		return undefined;
	}
}

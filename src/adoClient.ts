import { AuthService } from './authService';
import { LoggingService } from './LoggingService';

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

	private async getJson<T>(url: string): Promise<T> {
		const headers = this.auth.getHeaders();
		this.logger.logDebug(`GET ${url}`);
		const response = await fetch(url, { headers: headers as unknown as Record<string, string> });

		if (!response.ok) {
			const body = await safeReadBody(response);
			const msg = `ADO REST call failed: ${response.status} ${response.statusText} for ${url}${body ? ` :: ${body}` : ''}`;
			this.logger.logError(msg);
			throw new Error(msg);
		}

		// ADO returns HTML on redirect-to-login when auth fails silently. Detect it.
		const contentType = response.headers.get('content-type') ?? '';
		if (!contentType.includes('application/json')) {
			throw new Error(`Unexpected non-JSON response from ${url}. Authentication may have expired.`);
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

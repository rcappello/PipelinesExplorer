import * as vscode from 'vscode';
import { LoggingService } from './LoggingService';
import { RepoLinkKey } from './workspaceLinkService';

const STORAGE_KEY = 'pipelinesexplorer.repoBranches.v1';

/**
 * Per-repository branch override. When set, the extension reads YAML
 * (pipeline + templates + scripts) from the chosen branch instead of the
 * repository's default branch on Azure DevOps.
 */
export class RepoBranchService {
	private readonly _onDidChange = new vscode.EventEmitter<void>();
	readonly onDidChange = this._onDidChange.event;

	constructor(
		private readonly context: vscode.ExtensionContext,
		private readonly logger: LoggingService,
	) {}

	private encodeKey(k: RepoLinkKey): string {
		return `${k.orgAccountId}::${k.projectId}::${k.repoKey}`;
	}

	private readAll(): Record<string, string> {
		return this.context.globalState.get<Record<string, string>>(STORAGE_KEY, {});
	}

	private async writeAll(value: Record<string, string>): Promise<void> {
		await this.context.globalState.update(STORAGE_KEY, value);
		this._onDidChange.fire();
	}

	get(key: RepoLinkKey): string | undefined {
		return this.readAll()[this.encodeKey(key)];
	}

	async set(key: RepoLinkKey, branch: string): Promise<void> {
		const all = this.readAll();
		all[this.encodeKey(key)] = branch;
		await this.writeAll(all);
		this.logger.logInfo(`Branch override ${this.encodeKey(key)} -> ${branch}`);
	}

	async clear(key: RepoLinkKey): Promise<void> {
		const all = this.readAll();
		if (delete all[this.encodeKey(key)]) {
			await this.writeAll(all);
			this.logger.logInfo(`Branch override cleared for ${this.encodeKey(key)}`);
		}
	}
}

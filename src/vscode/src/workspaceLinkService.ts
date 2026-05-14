import * as vscode from 'vscode';
import { LoggingService } from './LoggingService';

const STORAGE_KEY = 'pipelinesexplorer.repoLinks.v1';

export interface RepoLinkKey {
	orgAccountId: string;
	projectId: string;
	repoKey: string;
}

/**
 * Persists a mapping from (organization, project, repository) to a local
 * workspace folder path. Used to resolve template / script references to
 * files on disk when the user double-clicks a tree item.
 */
export class WorkspaceLinkService {
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

	async set(key: RepoLinkKey, fsPath: string): Promise<void> {
		const all = this.readAll();
		all[this.encodeKey(key)] = fsPath;
		await this.writeAll(all);
		this.logger.logInfo(`Linked ${this.encodeKey(key)} -> ${fsPath}`);
	}

	async remove(key: RepoLinkKey): Promise<void> {
		const all = this.readAll();
		if (delete all[this.encodeKey(key)]) {
			await this.writeAll(all);
			this.logger.logInfo(`Unlinked ${this.encodeKey(key)}`);
		}
	}

	/** Look up by repoKey alone — useful when a template references a repo we
	 *  haven't seen as a tree node (cross-project resource reference). */
	findAnyByRepoKey(repoKey: string): string | undefined {
		const all = this.readAll();
		for (const [k, v] of Object.entries(all)) {
			if (k.endsWith(`::${repoKey}`)) {
				return v;
			}
		}
		return undefined;
	}
}

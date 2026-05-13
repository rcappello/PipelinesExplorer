import * as vscode from 'vscode';
import * as fs from 'fs';
import * as path from 'path';
import { LoggingService } from './LoggingService';
import { WorkspaceLinkService, RepoLinkKey } from './workspaceLinkService';

/** Strip Azure Pipelines variables that resolve to the repo root at runtime. */
function stripPipelineVariables(p: string): string {
	return p
		.replace(/\$\(\s*System\.DefaultWorkingDirectory\s*\)/gi, '')
		.replace(/\$\(\s*Build\.SourcesDirectory\s*\)/gi, '')
		.replace(/\$\(\s*Pipeline\.Workspace\s*\)/gi, '')
		.replace(/\$\(\s*Agent\.BuildDirectory\s*\)/gi, '')
		.replace(/\\/g, '/')
		.replace(/^\/+/, '')
		.trim();
}

/** Generate a small set of candidate filesystem paths for a relative reference
 *  inside a repository checkout. Returns the first that exists, or undefined. */
function resolveCandidate(rootFsPath: string, ref: string): string | undefined {
	const cleaned = stripPipelineVariables(ref);
	if (!cleaned) {
		return undefined;
	}
	const candidates = new Set<string>();
	candidates.add(path.resolve(rootFsPath, cleaned));
	// Also try treating the ref as repo-absolute even if it had no leading slash.
	const noLeadingDots = cleaned.replace(/^(?:\.\.\/)+/, '');
	candidates.add(path.resolve(rootFsPath, noLeadingDots));

	for (const c of candidates) {
		try {
			if (fs.existsSync(c) && fs.statSync(c).isFile()) {
				return c;
			}
		} catch {
			// ignore
		}
	}
	return undefined;
}

export interface OpenTarget {
	repoLinkKey: RepoLinkKey;
	/** Pipeline-style reference (may include $(System.DefaultWorkingDirectory) or be relative). */
	relativePath: string;
	/** Optional cross-repo reference name (for templates with `@repo`). */
	repositoryAlias?: string;
	/** Web URL fallback (e.g. dev.azure.com link), if known. */
	webUrl?: string;
	/** Friendly label for messages. */
	displayName: string;
}

export class OpenItemService {
	constructor(
		private readonly links: WorkspaceLinkService,
		private readonly logger: LoggingService,
	) {}

	async open(target: OpenTarget): Promise<void> {
		const linkedRoot =
			(target.repositoryAlias ? this.links.findAnyByRepoKey(target.repositoryAlias) : undefined) ??
			this.links.get(target.repoLinkKey);

		if (!linkedRoot) {
			await this.promptToLink(target);
			return;
		}

		const resolved = resolveCandidate(linkedRoot, target.relativePath);
		if (resolved) {
			const doc = await vscode.workspace.openTextDocument(vscode.Uri.file(resolved));
			await vscode.window.showTextDocument(doc, { preview: true });
			return;
		}

		this.logger.logWarning(
			`Could not find ${target.relativePath} under ${linkedRoot} for ${target.displayName}`,
		);
		const pick = await vscode.window.showWarningMessage(
			`File not found in linked workspace: ${target.relativePath}`,
			...(target.webUrl ? ['Open in Browser'] : []),
			'Re-link Workspace',
		);
		if (pick === 'Open in Browser' && target.webUrl) {
			await vscode.env.openExternal(vscode.Uri.parse(target.webUrl));
		} else if (pick === 'Re-link Workspace') {
			await this.promptToLink(target);
		}
	}

	private async promptToLink(target: OpenTarget): Promise<void> {
		const folders = vscode.workspace.workspaceFolders ?? [];
		if (folders.length === 0) {
			const choice = await vscode.window.showInformationMessage(
				`No workspace folder is open. Open the local clone of the repository to enable file navigation.`,
				...(target.webUrl ? ['Open in Browser'] : []),
			);
			if (choice === 'Open in Browser' && target.webUrl) {
				await vscode.env.openExternal(vscode.Uri.parse(target.webUrl));
			}
			return;
		}
		const items: (vscode.QuickPickItem & { fsPath?: string; browse?: boolean })[] = folders.map(f => ({
			label: f.name,
			description: f.uri.fsPath,
			fsPath: f.uri.fsPath,
		}));
		items.push({ label: '$(folder-opened) Browse…', browse: true });
		if (target.webUrl) {
			items.push({ label: '$(globe) Open in Browser instead', description: target.webUrl });
		}

		const pick = await vscode.window.showQuickPick(items, {
			title: `Link a workspace folder to repository "${target.repoLinkKey.repoKey}"`,
			placeHolder: 'Choose the local clone of this repository',
		});
		if (!pick) {
			return;
		}
		if (pick.browse) {
			const picked = await vscode.window.showOpenDialog({
				canSelectFiles: false,
				canSelectFolders: true,
				canSelectMany: false,
				openLabel: 'Link folder',
			});
			if (!picked || picked.length === 0) {
				return;
			}
			await this.links.set(target.repoLinkKey, picked[0].fsPath);
		} else if (pick.fsPath) {
			await this.links.set(target.repoLinkKey, pick.fsPath);
		} else if (target.webUrl) {
			await vscode.env.openExternal(vscode.Uri.parse(target.webUrl));
			return;
		} else {
			return;
		}

		// Try opening again now that we have a link.
		await this.open(target);
	}
}

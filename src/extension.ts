// The module 'vscode' contains the VS Code extensibility API
// Import the module and reference it with the alias vscode in your code below
import * as fs from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';
import { AdoClient } from './adoClient';
import { AuthService } from './authService';
import { AzureDevOpsAuthenticationProvider } from './authProvider';
import { LoggingService } from './LoggingService';
import {
	PipelineNode,
	PipelinesTreeProvider,
	RepositoryNode,
	ScriptItemNode,
	TemplateItemNode,
} from './pipelinesTreeProvider';
import { OpenItemService, OpenTarget } from './openItemService';
import { RepoBranchService } from './repoBranchService';
import { WorkspaceLinkService } from './workspaceLinkService';

const extensionName = process.env.EXTENSION_NAME || 'dev.pipelinesexplorer';
const extensionVersion = process.env.EXTENSION_VERSION || '0.0.0';

// This method is called when your extension is activated.
export async function activate(context: vscode.ExtensionContext): Promise<void> {
	const logger = new LoggingService();
	logger.setOutputLevel('DEBUG');
	logger.show();
	logger.logInfo(`Extension Name: ${extensionName}.`);
	logger.logInfo(`Extension Version: ${extensionVersion}.`);
	logger.logInfo('Pipelines Explorer activated.');

	// Register our PAT-based authentication provider so that VS Code can use it
	// via the standard `vscode.authentication.getSession` API.
	const patProvider = new AzureDevOpsAuthenticationProvider(context.secrets);
	context.subscriptions.push(patProvider);
	context.subscriptions.push(vscode.authentication.registerAuthenticationProvider(
		AzureDevOpsAuthenticationProvider.id,
		'Azure DevOps PAT',
		patProvider,
	));

	const auth = new AuthService(context, patProvider, logger);
	context.subscriptions.push(auth);

	const client = new AdoClient(auth, logger);

	const links = new WorkspaceLinkService(context, logger);
	const branches = new RepoBranchService(context, logger);
	const opener = new OpenItemService(links, logger);

	const tree = new PipelinesTreeProvider(client, auth, logger, links, branches);
	context.subscriptions.push(
		vscode.window.registerTreeDataProvider('pipelinesTree', tree),
	);

	context.subscriptions.push(
		vscode.commands.registerCommand('pipelinesexplorer.signInWithMicrosoft', async () => {
			try {
				const session = await auth.signInWithMicrosoft();
				if (session) {
					vscode.window.showInformationMessage(`Signed in as ${session.accountLabel}.`);
				}
			} catch (err) {
				logger.logError('Microsoft sign-in failed', err);
				vscode.window.showErrorMessage(
					`Microsoft sign-in failed: ${err instanceof Error ? err.message : String(err)}`,
				);
			}
		}),
		vscode.commands.registerCommand('pipelinesexplorer.signInWithPat', async () => {
			try {
				const session = await auth.signInWithPat();
				if (session) {
					vscode.window.showInformationMessage(`Signed in with PAT (${session.accountLabel}).`);
				}
			} catch (err) {
				logger.logError('PAT sign-in failed', err);
				vscode.window.showErrorMessage(
					`PAT sign-in failed: ${err instanceof Error ? err.message : String(err)}`,
				);
			}
		}),
		vscode.commands.registerCommand('pipelinesexplorer.signOut', async () => {
			await auth.signOut();
			vscode.window.showInformationMessage('Signed out of Azure DevOps.');
		}),
		vscode.commands.registerCommand('pipelinesexplorer.reset', async () => {
			const pick = await vscode.window.showWarningMessage(
				'This will delete the stored Personal Access Token and forget the chosen sign-in method. Continue?',
				{ modal: true },
				'Reset',
			);
			if (pick !== 'Reset') {
				return;
			}
			await auth.reset();
			vscode.window.showInformationMessage('Pipelines Explorer credentials cleared.');
		}),
		vscode.commands.registerCommand('pipelinesexplorer.refresh', () => tree.refresh()),
		vscode.commands.registerCommand('pipelinesexplorer.showLogs', () => logger.show()),

		vscode.commands.registerCommand('pipelinesexplorer.linkWorkspace', async (node: RepositoryNode) => {
			if (!node || node.kind !== 'repository') {
				vscode.window.showWarningMessage('Run this command from the context menu of a repository node.');
				return;
			}
			const folders = vscode.workspace.workspaceFolders ?? [];
			const items: (vscode.QuickPickItem & { fsPath?: string; browse?: boolean })[] = folders.map(f => ({
				label: f.name,
				description: f.uri.fsPath,
				fsPath: f.uri.fsPath,
			}));
			items.push({ label: '$(folder-opened) Browse…', browse: true });
			const pick = await vscode.window.showQuickPick(items, {
				title: `Link a workspace folder to "${node.repoLabel}"`,
				placeHolder: 'Choose the local clone of this repository',
			});
			if (!pick) {
				return;
			}
			let fsPath = pick.fsPath;
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
				fsPath = picked[0].fsPath;
			}
			if (!fsPath) {
				return;
			}
			const repoKey = {
				orgAccountId: node.organization.accountId,
				projectId: node.project.id,
				repoKey: node.repoKey,
			};
			await links.set(repoKey, fsPath);
			vscode.window.showInformationMessage(`Linked "${node.repoLabel}" → ${fsPath}`);
			// Auto-detect the current branch of the local clone and offer to use it
			// as the branch override for this repo.
			const detected = await detectLocalBranch(fsPath);
			if (detected) {
				const current = branches.get(repoKey);
				if (detected !== current) {
					const choice = await vscode.window.showInformationMessage(
						`The linked clone of "${node.repoLabel}" is on branch "${detected}". ` +
						`Use this branch when reading YAML from Azure DevOps?`,
						'Use this branch',
						'Keep default branch',
					);
					if (choice === 'Use this branch') {
						await branches.set(repoKey, detected);
					}
				}
			}
		}),
		vscode.commands.registerCommand('pipelinesexplorer.unlinkWorkspace', async (node: RepositoryNode) => {
			if (!node || node.kind !== 'repository') {
				return;
			}
			await links.remove({
				orgAccountId: node.organization.accountId,
				projectId: node.project.id,
				repoKey: node.repoKey,
			});
			vscode.window.showInformationMessage(`Unlinked "${node.repoLabel}".`);
		}),
		vscode.commands.registerCommand('pipelinesexplorer.selectBranch', async (node: RepositoryNode) => {
			if (!node || node.kind !== 'repository') {
				vscode.window.showWarningMessage('Run this command from the context menu of a repository node.');
				return;
			}
			const key = {
				orgAccountId: node.organization.accountId,
				projectId: node.project.id,
				repoKey: node.repoKey,
			};
			const current = branches.get(key);
			let branchList: string[] = [];
			try {
				branchList = await vscode.window.withProgress(
					{ location: vscode.ProgressLocation.Notification, title: `Loading branches for ${node.repoLabel}…` },
					() => client.listBranches(node.organization.accountName, node.project.name, node.repoKey),
				);
			} catch (err) {
				logger.logError(`Failed to list branches for ${node.repoLabel}`, err);
				vscode.window.showErrorMessage(
					`Could not load branches for "${node.repoLabel}": ${err instanceof Error ? err.message : String(err)}`,
				);
				return;
			}
			const defaultItem: vscode.QuickPickItem & { branch?: string; clear?: boolean } = {
				label: '$(repo) Use default branch',
				description: current ? `currently overridden to "${current}"` : 'currently in use',
				clear: true,
			};
			const items: Array<vscode.QuickPickItem & { branch?: string; clear?: boolean }> = [defaultItem];
			for (const b of branchList) {
				items.push({
					label: `$(git-branch) ${b}`,
					description: b === current ? 'current override' : undefined,
					branch: b,
				});
			}
			const pick = await vscode.window.showQuickPick(items, {
				title: `Select branch for "${node.repoLabel}"`,
				placeHolder: 'Pipelines Explorer will read YAML from this branch',
				matchOnDescription: true,
			});
			if (!pick) {
				return;
			}
			if (pick.clear) {
				await branches.clear(key);
				vscode.window.showInformationMessage(`"${node.repoLabel}" now uses the default branch.`);
			} else if (pick.branch) {
				await branches.set(key, pick.branch);
				vscode.window.showInformationMessage(`"${node.repoLabel}" set to branch "${pick.branch}".`);
			}
		}),
		vscode.commands.registerCommand('pipelinesexplorer.openItem',
			async (node: PipelineNode | TemplateItemNode | ScriptItemNode) => {
				const target = buildOpenTarget(node);
				if (!target) {
					vscode.window.showInformationMessage('Nothing to open for this item.');
					return;
				}
				target.branch = branches.get(target.repoLinkKey);
				try {
					await opener.open(target);
				} catch (err) {
					logger.logError('Open item failed', err);
					vscode.window.showErrorMessage(
						`Failed to open: ${err instanceof Error ? err.message : String(err)}`,
					);
				}
			}),
	);

	// Kick off silent restore in background AFTER commands are registered, so the
	// UI stays responsive even if the auth provider takes a while to resolve
	// (e.g. SecretStorage unlocking on first activation).
	void auth.initialize().catch(err => {
		logger.logError('Silent restore failed', err);
	});
}

// This method is called when your extension is deactivated
export function deactivate(): void { /* nothing to clean up */ }

function buildOpenTarget(
	node: PipelineNode | TemplateItemNode | ScriptItemNode,
): OpenTarget | undefined {
	if (node.kind === 'pipeline') {
		const path = node.detail?.configuration?.path;
		if (!path) { return undefined; }
		return {
			repoLinkKey: {
				orgAccountId: node.organization.accountId,
				projectId: node.project.id,
				repoKey: node.repoKey,
			},
			relativePath: path,
			displayName: node.pipeline.name,
		};
	}
	if (node.kind === 'templateItem') {
		// Prefer the fully resolved repo-absolute path for same-repo templates so
		// that relative `../` segments are normalised away.
		const sameRepo = !node.ref.repository && !!node.containingRepoId;
		const relativePath = sameRepo
			? node.resolvedPath.replace(/^\/+/, '')
			: node.ref.path;
		return {
			repoLinkKey: {
				orgAccountId: node.organization.accountId,
				projectId: node.project.id,
				repoKey: node.pipelineRepoKey,
			},
			relativePath,
			repositoryAlias: node.ref.repository,
			displayName: node.ref.path,
		};
	}
	if (node.kind === 'scriptItem') {
		const grandparent = node.parent.parent;
		const ctx = grandparent.kind === 'pipeline'
			? {
				orgId: grandparent.organization.accountId,
				projId: grandparent.project.id,
				repoKey: grandparent.repoKey,
			}
			: {
				orgId: grandparent.organization.accountId,
				projId: grandparent.project.id,
				repoKey: grandparent.pipelineRepoKey,
			};
		if (node.ref.filePath) {
			return {
				repoLinkKey: {
					orgAccountId: ctx.orgId,
					projectId: ctx.projId,
					repoKey: ctx.repoKey,
				},
				relativePath: node.ref.filePath,
				displayName: node.ref.filePath,
			};
		}
		// Inline script: open the parent YAML at the task line.
		if (node.ref.inline && node.ref.line) {
			const parentYaml = node.parent.analysis.rootPath;
			if (!parentYaml) { return undefined; }
			return {
				repoLinkKey: {
					orgAccountId: ctx.orgId,
					projectId: ctx.projId,
					repoKey: ctx.repoKey,
				},
				relativePath: parentYaml,
				selectionLine: node.ref.line,
				displayName: `${parentYaml}:${node.ref.line}`,
			};
		}
		return undefined;
	}
	return undefined;
}

/**
 * Best-effort detection of the current branch of a local Git working copy by
 * reading `.git/HEAD`. Handles the worktree indirection where `.git` is a file
 * containing `gitdir: <path>`. Returns undefined if the folder is not a Git
 * working copy or the HEAD is detached.
 */
async function detectLocalBranch(folderPath: string): Promise<string | undefined> {
	try {
		const gitEntry = path.join(folderPath, '.git');
		let gitDir = gitEntry;
		const stat = await fs.promises.stat(gitEntry).catch(() => undefined);
		if (!stat) { return undefined; }
		if (stat.isFile()) {
			const content = await fs.promises.readFile(gitEntry, 'utf8');
			const match = content.match(/^gitdir:\s*(.+)\s*$/m);
			if (!match) { return undefined; }
			const target = match[1].trim();
			gitDir = path.isAbsolute(target) ? target : path.resolve(folderPath, target);
		}
		const headPath = path.join(gitDir, 'HEAD');
		const head = (await fs.promises.readFile(headPath, 'utf8')).trim();
		const refMatch = head.match(/^ref:\s*refs\/heads\/(.+)$/);
		return refMatch ? refMatch[1].trim() : undefined;
	} catch {
		return undefined;
	}
}

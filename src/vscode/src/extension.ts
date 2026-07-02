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
		vscode.l10n.t('Azure DevOps PAT'),
		patProvider,
	));

	const auth = new AuthService(context, patProvider, logger);
	context.subscriptions.push(auth);

	const client = new AdoClient(auth, logger);

	const links = new WorkspaceLinkService(context, logger);
	const branches = new RepoBranchService(context, logger);
	const opener = new OpenItemService(links, logger);

	const tree = new PipelinesTreeProvider(client, auth, logger, links, branches);
	const treeView = vscode.window.createTreeView('pipelinesTree', {
		treeDataProvider: tree,
		showCollapseAll: true,
	});
	tree.setTreeView(treeView);
	context.subscriptions.push(treeView);

	// Reveal matched pipelines (up to FILTER_REVEAL_CAP) after a filter scan
	// completes, so the user immediately sees the results without hand-expanding.
	context.subscriptions.push(tree.onDidCompleteFilterScan(async nodes => {
		for (const n of nodes) {
			try {
				await treeView.reveal(n, { expand: true, select: false, focus: false });
			} catch (err) {
				logger.logWarning(`Failed to reveal filtered node: ${err instanceof Error ? err.message : String(err)}`);
			}
		}
	}));

	context.subscriptions.push(
		vscode.commands.registerCommand('pipelinesexplorer.signInWithMicrosoft', async () => {
			try {
				const session = await auth.signInWithMicrosoft();
				if (session) {
					vscode.window.showInformationMessage(vscode.l10n.t('Signed in as {0}.', session.accountLabel));
				}
			} catch (err) {
				logger.logError('Microsoft sign-in failed', err);
				vscode.window.showErrorMessage(
					vscode.l10n.t('Microsoft sign-in failed: {0}', err instanceof Error ? err.message : String(err)),
				);
			}
		}),
		vscode.commands.registerCommand('pipelinesexplorer.selectTenant', async () => {
			try {
				const current = auth.getStoredTenant();
				const tenants = await vscode.window.withProgress(
					{ location: { viewId: 'pipelinesTree' } },
					() => auth.listAvailableTenants(),
				);
				if (tenants.length === 0) {
					vscode.window.showWarningMessage(vscode.l10n.t('No Microsoft Entra tenants are available for this account.'));
					return;
				}
				const items: (vscode.QuickPickItem & { tenantId?: string; tenantName?: string })[] = [
					{
						label: vscode.l10n.t('$(account) Default tenant'),
						description: current ? undefined : vscode.l10n.t('(current)'),
						tenantId: undefined,
					},
					{ label: '', kind: vscode.QuickPickItemKind.Separator },
					...tenants
						.sort((a, b) => a.displayName.localeCompare(b.displayName))
						.map(t => ({
							label: `$(organization) ${t.displayName}`,
							description: t.tenantId === current ? vscode.l10n.t('(current)') : t.defaultDomain,
							detail: t.tenantId,
							tenantId: t.tenantId,
							tenantName: t.displayName,
						})),
				];
				const pick = await vscode.window.showQuickPick(items, {
					title: vscode.l10n.t('Select Microsoft Entra tenant'),
					placeHolder: vscode.l10n.t('Pick the tenant whose Azure DevOps organisations you want to browse'),
					ignoreFocusOut: true,
				});
				if (!pick) {
					return;
				}
				const session = await auth.switchTenant(pick.tenantId, pick.tenantName);
				if (session) {
					if (session.tenantId) {
						vscode.window.showInformationMessage(
							vscode.l10n.t('Signed in as {0} on tenant {1}.', session.accountLabel, pick.tenantName ?? session.tenantId),
						);
					} else {
						vscode.window.showInformationMessage(vscode.l10n.t('Signed in as {0}.', session.accountLabel));
					}
				}
			} catch (err) {
				logger.logError('Tenant selection failed', err);
				vscode.window.showErrorMessage(
					vscode.l10n.t('Tenant selection failed: {0}', err instanceof Error ? err.message : String(err)),
				);
			}
		}),
		vscode.commands.registerCommand('pipelinesexplorer.signInWithPat', async () => {
			try {
				const session = await auth.signInWithPat();
				if (session) {
					vscode.window.showInformationMessage(vscode.l10n.t('Signed in with PAT ({0}).', session.accountLabel));
				}
			} catch (err) {
				logger.logError('PAT sign-in failed', err);
				vscode.window.showErrorMessage(
					vscode.l10n.t('PAT sign-in failed: {0}', err instanceof Error ? err.message : String(err)),
				);
			}
		}),
		vscode.commands.registerCommand('pipelinesexplorer.signOut', async () => {
			await auth.signOut();
			vscode.window.showInformationMessage(vscode.l10n.t('Signed out of Azure DevOps.'));
		}),
		vscode.commands.registerCommand('pipelinesexplorer.reset', async () => {
			const resetLabel = vscode.l10n.t('Reset');
			const pick = await vscode.window.showWarningMessage(
				vscode.l10n.t('This will delete the stored Personal Access Token and forget the chosen sign-in method. Continue?'),
				{ modal: true },
				resetLabel,
			);
			if (pick !== resetLabel) {
				return;
			}
			await auth.reset();
			vscode.window.showInformationMessage(vscode.l10n.t('Pipelines Explorer credentials cleared.'));
		}),
		vscode.commands.registerCommand('pipelinesexplorer.refresh', () => tree.refresh()),
		vscode.commands.registerCommand('pipelinesexplorer.showLogs', () => logger.show()),

		vscode.commands.registerCommand('pipelinesexplorer.filter', async () => {
			const current = tree.getCurrentFilter();
			const value = await vscode.window.showInputBox({
				title: vscode.l10n.t('Filter Pipelines Explorer'),
				prompt: vscode.l10n.t('Filter pipelines, templates and scripts by name'),
				placeHolder: vscode.l10n.t('Type part of a name (empty to clear)'),
				value: current ?? '',
				ignoreFocusOut: true,
			});
			if (value === undefined) {
				return; // user cancelled — leave current filter untouched
			}
			tree.setFilter(value.trim() || undefined);
		}),
		vscode.commands.registerCommand('pipelinesexplorer.clearFilter', () => tree.setFilter(undefined)),

		vscode.commands.registerCommand('pipelinesexplorer.linkWorkspace', async (node: RepositoryNode) => {
			if (!node || node.kind !== 'repository') {
				vscode.window.showWarningMessage(vscode.l10n.t('Run this command from the context menu of a repository node.'));
				return;
			}
			const folders = vscode.workspace.workspaceFolders ?? [];
			const items: (vscode.QuickPickItem & { fsPath?: string; browse?: boolean })[] = folders.map(f => ({
				label: f.name,
				description: f.uri.fsPath,
				fsPath: f.uri.fsPath,
			}));
			items.push({ label: vscode.l10n.t('$(folder-opened) Browse…'), browse: true });
			const pick = await vscode.window.showQuickPick(items, {
				title: vscode.l10n.t('Link a workspace folder to "{0}"', node.repoLabel),
				placeHolder: vscode.l10n.t('Choose the local clone of this repository'),
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
					openLabel: vscode.l10n.t('Link folder'),
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
			vscode.window.showInformationMessage(vscode.l10n.t('Linked "{0}" → {1}', node.repoLabel, fsPath));
			// Auto-detect the current branch of the local clone and offer to use it
			// as the branch override for this repo.
			const detected = await detectLocalBranch(fsPath);
			if (detected) {
				const current = branches.get(repoKey);
				if (detected !== current) {
					const useThisBranch = vscode.l10n.t('Use this branch');
					const choice = await vscode.window.showInformationMessage(
						vscode.l10n.t('The linked clone of "{0}" is on branch "{1}". Use this branch when reading YAML from Azure DevOps?', node.repoLabel, detected),
						useThisBranch,
						vscode.l10n.t('Keep default branch'),
					);
					if (choice === useThisBranch) {
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
			vscode.window.showInformationMessage(vscode.l10n.t('Unlinked "{0}".', node.repoLabel));
		}),
		vscode.commands.registerCommand('pipelinesexplorer.selectBranch', async (node: RepositoryNode) => {
			if (!node || node.kind !== 'repository') {
				vscode.window.showWarningMessage(vscode.l10n.t('Run this command from the context menu of a repository node.'));
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
					{ location: vscode.ProgressLocation.Notification, title: vscode.l10n.t('Loading branches for {0}…', node.repoLabel) },
					() => client.listBranches(node.organization.accountName, node.project.name, node.repoKey),
				);
			} catch (err) {
				logger.logError(`Failed to list branches for ${node.repoLabel}`, err);
				vscode.window.showErrorMessage(
					vscode.l10n.t('Could not load branches for "{0}": {1}', node.repoLabel, err instanceof Error ? err.message : String(err)),
				);
				return;
			}
			const defaultItem: vscode.QuickPickItem & { branch?: string; clear?: boolean } = {
				label: vscode.l10n.t('$(repo) Use default branch'),
				description: current ? vscode.l10n.t('currently overridden to "{0}"', current) : vscode.l10n.t('currently in use'),
				clear: true,
			};
			const items: Array<vscode.QuickPickItem & { branch?: string; clear?: boolean }> = [defaultItem];
			for (const b of branchList) {
				items.push({
					label: `$(git-branch) ${b}`,
					description: b === current ? vscode.l10n.t('current override') : undefined,
					branch: b,
				});
			}
			const pick = await vscode.window.showQuickPick(items, {
				title: vscode.l10n.t('Select branch for "{0}"', node.repoLabel),
				placeHolder: vscode.l10n.t('Pipelines Explorer will read YAML from this branch'),
				matchOnDescription: true,
			});
			if (!pick) {
				return;
			}
			if (pick.clear) {
				await branches.clear(key);
				vscode.window.showInformationMessage(vscode.l10n.t('"{0}" now uses the default branch.', node.repoLabel));
			} else if (pick.branch) {
				await branches.set(key, pick.branch);
				vscode.window.showInformationMessage(vscode.l10n.t('"{0}" set to branch "{1}".', node.repoLabel, pick.branch));
			}
		}),
		vscode.commands.registerCommand('pipelinesexplorer.openItem',
			async (node: PipelineNode | TemplateItemNode | ScriptItemNode) => {
				const target = buildOpenTarget(node);
				if (!target) {
					vscode.window.showInformationMessage(vscode.l10n.t('Nothing to open for this item.'));
					return;
				}
				target.branch = branches.get(target.repoLinkKey);
				try {
					await opener.open(target);
				} catch (err) {
					logger.logError('Open item failed', err);
					vscode.window.showErrorMessage(
						vscode.l10n.t('Failed to open: {0}', err instanceof Error ? err.message : String(err)),
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

// The module 'vscode' contains the VS Code extensibility API
// Import the module and reference it with the alias vscode in your code below
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
	const opener = new OpenItemService(links, logger);

	const tree = new PipelinesTreeProvider(client, auth, logger, links);
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
			await links.set(
				{
					orgAccountId: node.organization.accountId,
					projectId: node.project.id,
					repoKey: node.repoKey,
				},
				fsPath,
			);
			vscode.window.showInformationMessage(`Linked "${node.repoLabel}" → ${fsPath}`);
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
		vscode.commands.registerCommand('pipelinesexplorer.openItem',
			async (node: PipelineNode | TemplateItemNode | ScriptItemNode) => {
				const target = buildOpenTarget(node);
				if (!target) {
					vscode.window.showInformationMessage('Nothing to open for this item.');
					return;
				}
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



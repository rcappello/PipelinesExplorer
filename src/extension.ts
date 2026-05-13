// The module 'vscode' contains the VS Code extensibility API
// Import the module and reference it with the alias vscode in your code below
import * as vscode from 'vscode';
import { AdoClient } from './adoClient';
import { AuthService } from './authService';
import { AzureDevOpsAuthenticationProvider } from './authProvider';
import { LoggingService } from './LoggingService';
import { PipelinesTreeProvider } from './pipelinesTreeProvider';

const extensionName = process.env.EXTENSION_NAME || 'dev.pipelinesexplorer';
const extensionVersion = process.env.EXTENSION_VERSION || '0.0.0';

// This method is called when your extension is activated.
export async function activate(context: vscode.ExtensionContext): Promise<void> {
	const logger = new LoggingService();
	logger.logInfo(`Extension Name: ${extensionName}.`);
	logger.logInfo(`Extension Version: ${extensionVersion}.`);

	// Register our PAT-based authentication provider so that VS Code can use it
	// via the standard `vscode.authentication.getSession` API.
	const patProvider = new AzureDevOpsAuthenticationProvider(context.secrets);
	context.subscriptions.push(patProvider);
	context.subscriptions.push(vscode.authentication.registerAuthenticationProvider(
		AzureDevOpsAuthenticationProvider.id,
		'Azure DevOps PAT',
		patProvider,
	));

	const auth = new AuthService(context);
	context.subscriptions.push(auth);

	const client = new AdoClient(auth, logger);

	const tree = new PipelinesTreeProvider(client, auth, logger);
	context.subscriptions.push(
		vscode.window.registerTreeDataProvider('pipelinesTree', tree),
	);

	// Try to silently restore the previously chosen session.
	await auth.initialize();

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
		vscode.commands.registerCommand('pipelinesexplorer.refresh', () => tree.refresh()),
	);
}

// This method is called when your extension is deactivated
export function deactivate(): void { /* nothing to clean up */ }



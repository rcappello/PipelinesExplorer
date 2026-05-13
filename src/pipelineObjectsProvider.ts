import * as vscode from 'vscode';
import * as path from 'path';

export class PipelineObjectsProvider implements vscode.TreeDataProvider<PipelineObject> {

	private _onDidChangeTreeData: vscode.EventEmitter<PipelineObject | undefined | void> =
		new vscode.EventEmitter<PipelineObject | undefined | void>();
	readonly onDidChangeTreeData: vscode.Event<PipelineObject | undefined | void> =
		this._onDidChangeTreeData.event;

	refresh(): void {
		this._onDidChangeTreeData.fire();
	}

	getTreeItem(element: PipelineObject): vscode.TreeItem | Thenable<vscode.TreeItem> {
		return element;
	}

	getChildren(_element?: PipelineObject | undefined): vscode.ProviderResult<PipelineObject[]> {
		// TODO: implement loading of Azure DevOps organizations / projects / pipelines.
		return Promise.resolve([]);
	}

	getParent?(_element: PipelineObject): vscode.ProviderResult<PipelineObject> {
		return Promise.resolve(undefined);
	}
}

export class PipelineObject extends vscode.TreeItem {

	constructor(
		public readonly label: string,
		private readonly objectType: string,
		public readonly collapsibleState: vscode.TreeItemCollapsibleState,
		public readonly command?: vscode.Command,
	) {
		super(label, collapsibleState);

		this.tooltip = `${this.label}-${this.objectType}`;
		this.description = this.objectType;
	}

	iconPath = {
		light: vscode.Uri.file(path.join(__filename, '..', '..', 'resources', 'light', 'dependency.svg')),
		dark: vscode.Uri.file(path.join(__filename, '..', '..', 'resources', 'dark', 'dependency.svg')),
	};

	contextValue = 'dependency';
}

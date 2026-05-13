import * as vscode from 'vscode';
import { AdoClient, AdoOrganization, AdoPipeline, AdoProject } from './adoClient';
import { AuthService } from './authService';
import { LoggingService } from './LoggingService';

type Node = OrganizationNode | ProjectNode | PipelineNode;

export class OrganizationNode extends vscode.TreeItem {
	readonly kind = 'organization' as const;
	constructor(public readonly organization: AdoOrganization) {
		super(organization.accountName, vscode.TreeItemCollapsibleState.Collapsed);
		this.id = `org:${organization.accountId}`;
		this.contextValue = 'pipelinesexplorer.organization';
		this.iconPath = new vscode.ThemeIcon('organization');
		this.tooltip = organization.accountUri;
	}
}

export class ProjectNode extends vscode.TreeItem {
	readonly kind = 'project' as const;
	constructor(
		public readonly organization: AdoOrganization,
		public readonly project: AdoProject,
	) {
		super(project.name, vscode.TreeItemCollapsibleState.Collapsed);
		this.id = `proj:${organization.accountId}:${project.id}`;
		this.contextValue = 'pipelinesexplorer.project';
		this.iconPath = new vscode.ThemeIcon('project');
		this.description = project.description;
		this.tooltip = project.description ?? project.name;
	}
}

export class PipelineNode extends vscode.TreeItem {
	readonly kind = 'pipeline' as const;
	constructor(
		public readonly organization: AdoOrganization,
		public readonly project: AdoProject,
		public readonly pipeline: AdoPipeline,
	) {
		super(pipeline.name, vscode.TreeItemCollapsibleState.None);
		this.id = `pipe:${organization.accountId}:${project.id}:${pipeline.id}`;
		this.contextValue = 'pipelinesexplorer.pipeline';
		this.iconPath = new vscode.ThemeIcon('rocket');
		this.description = pipeline.folder && pipeline.folder !== '\\' ? pipeline.folder : undefined;
		this.tooltip = `${pipeline.folder}\\${pipeline.name}`.replace(/^\\+/, '');
	}
}

export class PipelinesTreeProvider implements vscode.TreeDataProvider<Node> {
	private readonly _onDidChangeTreeData = new vscode.EventEmitter<Node | undefined | void>();
	readonly onDidChangeTreeData: vscode.Event<Node | undefined | void> = this._onDidChangeTreeData.event;

	constructor(
		private readonly client: AdoClient,
		private readonly auth: AuthService,
		private readonly logger: LoggingService,
	) {
		this.auth.onDidChangeSession(() => this.refresh());
	}

	refresh(): void {
		this._onDidChangeTreeData.fire();
	}

	getTreeItem(element: Node): vscode.TreeItem {
		return element;
	}

	async getChildren(element?: Node): Promise<Node[]> {
		if (!this.auth.session) {
			return [];
		}

		try {
			if (!element) {
				const profile = await this.client.getProfile();
				const orgs = await this.client.listOrganizations(profile.id);
				return orgs
					.sort((a, b) => a.accountName.localeCompare(b.accountName))
					.map(o => new OrganizationNode(o));
			}

			if (element.kind === 'organization') {
				const projects = await this.client.listProjects(element.organization.accountName);
				return projects
					.sort((a, b) => a.name.localeCompare(b.name))
					.map(p => new ProjectNode(element.organization, p));
			}

			if (element.kind === 'project') {
				const pipelines = await this.client.listPipelines(
					element.organization.accountName,
					element.project.name,
				);
				return pipelines
					.sort((a, b) => a.name.localeCompare(b.name))
					.map(p => new PipelineNode(element.organization, element.project, p));
			}

			return [];
		} catch (err) {
			this.logger.logError('Failed to load tree children', err);
			vscode.window.showErrorMessage(
				`Pipelines Explorer: ${err instanceof Error ? err.message : String(err)}`,
			);
			return [];
		}
	}
}

import * as vscode from 'vscode';
import { AdoClient, AdoOrganization, AdoPipeline, AdoPipelineDetail, AdoProject, AdoUnauthorizedError } from './adoClient';
import { AuthService } from './authService';
import { LoggingService } from './LoggingService';
import { PipelineAnalysis, PipelineYamlAnalyzer, PowerShellRef, TemplateRef } from './pipelineYamlAnalyzer';
import { WorkspaceLinkService } from './workspaceLinkService';

type Node =
	| OrganizationNode
	| ProjectNode
	| RepositoryNode
	| PipelineNode
	| GroupNode
	| TemplateItemNode
	| ScriptItemNode
	| InfoNode;

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
		public readonly repoKey: string,
		public readonly repoLabel: string,
		public readonly detail?: AdoPipelineDetail,
	) {
		super(pipeline.name, vscode.TreeItemCollapsibleState.Collapsed);
		this.id = `pipe:${organization.accountId}:${project.id}:${pipeline.id}`;
		this.contextValue = 'pipelinesexplorer.pipeline';
		this.iconPath = new vscode.ThemeIcon('rocket');
		this.description = pipeline.folder && pipeline.folder !== '\\' ? pipeline.folder : undefined;
		this.tooltip = `${pipeline.folder}\\${pipeline.name}`.replace(/^\\+/, '');
		const rootPath = detail?.configuration?.path;
		if (rootPath) {
			this.command = {
				command: 'pipelinesexplorer.openItem',
				title: 'Open Pipeline YAML',
				arguments: [this],
			};
		}
	}

	/** Repo id of the pipeline source (TfsGit only). */
	get repoId(): string | undefined {
		return this.detail?.configuration?.repository?.id;
	}
	/** Directory of the root YAML inside the repo (e.g. `/solutions/foo/.ci`). */
	get yamlDir(): string {
		return dirOfRepoPath(this.detail?.configuration?.path ?? '/');
	}
}

/** Repository grouping under a project. Pipelines that don't expose a repository
 *  are bucketed under a synthetic "(unknown repository)" group. */
export class RepositoryNode extends vscode.TreeItem {
	readonly kind = 'repository' as const;
	constructor(
		public readonly organization: AdoOrganization,
		public readonly project: AdoProject,
		public readonly repoKey: string,
		public readonly repoLabel: string,
		public readonly repoType: string | undefined,
		public readonly pipelines: Array<{ pipeline: AdoPipeline; detail?: AdoPipelineDetail }>,
		public readonly linkedFolder?: string,
	) {
		super(repoLabel, vscode.TreeItemCollapsibleState.Collapsed);
		this.id = `repo:${organization.accountId}:${project.id}:${repoKey}`;
		this.contextValue = linkedFolder
			? 'pipelinesexplorer.repository.linked'
			: 'pipelinesexplorer.repository';
		this.iconPath = new vscode.ThemeIcon(linkedFolder ? 'repo-clone' : 'repo');
		const pieces = [`${pipelines.length}`];
		if (repoType) { pieces.push(repoType); }
		if (linkedFolder) { pieces.push('linked'); }
		this.description = pieces.join(' · ');
		this.tooltip = linkedFolder
			? `${repoLabel}${repoType ? ` (${repoType})` : ''}\nLinked: ${linkedFolder}`
			: (repoType ? `${repoLabel} (${repoType})` : repoLabel);
	}
}

type GroupKind = 'templates' | 'scripts';

export class GroupNode extends vscode.TreeItem {
	readonly kind = 'group' as const;
	constructor(
		public readonly parent: PipelineNode | TemplateItemNode,
		public readonly group: GroupKind,
		public readonly analysis: PipelineAnalysis,
	) {
		super(group === 'templates' ? 'Templates' : 'PowerShell scripts',
			vscode.TreeItemCollapsibleState.Collapsed);
		this.id = `${parent.id}:group:${group}`;
		this.contextValue = `pipelinesexplorer.group.${group}`;
		this.iconPath = new vscode.ThemeIcon(group === 'templates' ? 'files' : 'terminal-powershell');
		const count = group === 'templates' ? analysis.templates.length : analysis.scripts.length;
		this.description = String(count);
	}
}

export class TemplateItemNode extends vscode.TreeItem {
	readonly kind = 'templateItem' as const;
	constructor(
		public readonly parent: GroupNode,
		public readonly ref: TemplateRef,
		public readonly organization: AdoOrganization,
		public readonly project: AdoProject,
		public readonly pipelineRepoKey: string,
		/** Repo id where the parent YAML lives. */
		public readonly containingRepoId: string | undefined,
		/** Directory of the parent YAML inside the repo (e.g. `/solutions/foo`). */
		public readonly containingDir: string,
	) {
		const sameRepo = !ref.repository && !!containingRepoId;
		super(
			basename(ref.path),
			sameRepo ? vscode.TreeItemCollapsibleState.Collapsed : vscode.TreeItemCollapsibleState.None,
		);
		this.id = `${parent.id}:tpl:${ref.raw}`;
		this.iconPath = new vscode.ThemeIcon('file-code');
		this.description = ref.repository ? `@${ref.repository}` : undefined;
		this.tooltip = ref.repository ? `${ref.path} @${ref.repository}` : ref.path;
		this.contextValue = 'pipelinesexplorer.template';
		this.command = {
			command: 'pipelinesexplorer.openItem',
			title: 'Open Template',
			arguments: [this],
		};
	}

	/** Repo-absolute resolved path of this template (only meaningful for same-repo). */
	get resolvedPath(): string {
		return resolveRepoPath(this.containingDir, this.ref.path);
	}
	get resolvedDir(): string {
		return dirOfRepoPath(this.resolvedPath);
	}
}

export class ScriptItemNode extends vscode.TreeItem {
	readonly kind = 'scriptItem' as const;
	constructor(public readonly parent: GroupNode, public readonly ref: PowerShellRef) {
		super(
			ref.filePath ? basename(ref.filePath) : (ref.inline ? '(inline script)' : '(unknown source)'),
			vscode.TreeItemCollapsibleState.None,
		);
		this.id = `${parent.id}:ps:${ref.task}:${ref.filePath ?? (ref.inline ? 'inline' : 'unknown')}`;
		this.iconPath = new vscode.ThemeIcon(ref.filePath ? 'file' : 'note');
		this.description = ref.task;
		this.tooltip = ref.filePath
			? `${ref.task} → ${ref.filePath}`
			: `${ref.task} (${ref.inline ? `inline${ref.line ? ` @ line ${ref.line}` : ''}` : 'unknown'})`;
		this.contextValue = 'pipelinesexplorer.script';
		if (ref.filePath || (ref.inline && ref.line)) {
			this.command = {
				command: 'pipelinesexplorer.openItem',
				title: ref.filePath ? 'Open Script' : 'Open Inline Script Location',
				arguments: [this],
			};
		}
	}
}

function basename(p: string): string {
	const clean = p.replace(/\\/g, '/').replace(/\/+$/, '');
	const i = clean.lastIndexOf('/');
	return i >= 0 ? clean.slice(i + 1) : clean;
}

/** Resolve a (possibly relative or repo-absolute) ref path against a repo dir. */
function resolveRepoPath(baseDir: string, ref: string): string {
	const cleaned = ref.replace(/\\/g, '/').trim();
	const combined = cleaned.startsWith('/') ? cleaned : `${baseDir}/${cleaned}`;
	const parts = combined.split('/').filter(s => s.length > 0);
	const out: string[] = [];
	for (const seg of parts) {
		if (seg === '.') { continue; }
		if (seg === '..') { out.pop(); continue; }
		out.push(seg);
	}
	return '/' + out.join('/');
}

function dirOfRepoPath(p: string): string {
	const clean = p.replace(/\\/g, '/');
	const i = clean.lastIndexOf('/');
	return i <= 0 ? '' : clean.slice(0, i);
}

/** Placeholder leaf for empty / warning / loading states. */
export class InfoNode extends vscode.TreeItem {
	readonly kind = 'info' as const;
	constructor(parentId: string, message: string, icon = 'info') {
		super(message, vscode.TreeItemCollapsibleState.None);
		this.id = `${parentId}:info:${message}`;
		this.iconPath = new vscode.ThemeIcon(icon);
		this.contextValue = 'pipelinesexplorer.info';
	}
}

export class PipelinesTreeProvider implements vscode.TreeDataProvider<Node> {
	private readonly _onDidChangeTreeData = new vscode.EventEmitter<Node | undefined | void>();
	readonly onDidChangeTreeData: vscode.Event<Node | undefined | void> = this._onDidChangeTreeData.event;

	private readonly analyzer: PipelineYamlAnalyzer;
	/** Cache of analyses keyed by pipeline node id, to keep getChildren cheap. */
	private readonly analysisCache = new Map<string, Promise<PipelineAnalysis>>();
	/** Cache of resolved Git repository names by repository id. */
	private readonly repoNameCache = new Map<string, string>();

	constructor(
		private readonly client: AdoClient,
		private readonly auth: AuthService,
		private readonly logger: LoggingService,
		private readonly links: WorkspaceLinkService,
	) {
		this.analyzer = new PipelineYamlAnalyzer(client, logger);
		this.auth.onDidChangeSession(() => this.refresh());
		this.links.onDidChange(() => this.refresh());
	}

	refresh(): void {
		this.analysisCache.clear();
		this.repoNameCache.clear();
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
				if (pipelines.length === 0) {
					return [new InfoNode(element.id!, 'No pipelines in this project')];
				}
				return await this.groupPipelinesByRepository(element.organization, element.project, pipelines);
			}

			if (element.kind === 'repository') {
				return element.pipelines
					.sort((a, b) => a.pipeline.name.localeCompare(b.pipeline.name))
					.map(p => new PipelineNode(
						element.organization,
						element.project,
						p.pipeline,
						element.repoKey,
						element.repoLabel,
						p.detail,
					));
			}

			if (element.kind === 'pipeline') {
				const analysis = await this.getAnalysis(element);
				const nodes: Node[] = [];
				if (analysis.warning) {
					nodes.push(new InfoNode(element.id!, analysis.warning, 'warning'));
				}
				const tplGroup = new GroupNode(element, 'templates', analysis);
				const psGroup = new GroupNode(element, 'scripts', analysis);
				nodes.push(tplGroup, psGroup);
				return nodes;
			}

			if (element.kind === 'group') {
				const parent = element.parent;
				const ctx = parent.kind === 'pipeline'
					? {
						organization: parent.organization,
						project: parent.project,
						pipelineRepoKey: parent.repoKey,
						repoId: parent.repoId,
						baseDir: parent.yamlDir,
					}
					: {
						organization: parent.organization,
						project: parent.project,
						pipelineRepoKey: parent.pipelineRepoKey,
						repoId: parent.containingRepoId,
						baseDir: parent.resolvedDir,
					};

				if (element.group === 'templates') {
					if (element.analysis.templates.length === 0) {
						return [new InfoNode(element.id!, 'No templates referenced')];
					}
					return element.analysis.templates.map(t => new TemplateItemNode(
						element, t,
						ctx.organization, ctx.project, ctx.pipelineRepoKey,
						ctx.repoId, ctx.baseDir,
					));
				}
				if (element.analysis.scripts.length === 0) {
					return [new InfoNode(element.id!, 'No PowerShell scripts referenced')];
				}
				return element.analysis.scripts.map(s => new ScriptItemNode(element, s));
			}

			if (element.kind === 'templateItem') {
				// Same-repo templates only (cross-repo are leaves).
				if (element.ref.repository || !element.containingRepoId) {
					return [];
				}
				const analysis = await this.getTemplateAnalysis(element);
				const nodes: Node[] = [];
				if (analysis.warning) {
					nodes.push(new InfoNode(element.id!, analysis.warning, 'warning'));
				}
				if (analysis.templates.length > 0) {
					nodes.push(new GroupNode(element, 'templates', analysis));
				}
				if (analysis.scripts.length > 0) {
					nodes.push(new GroupNode(element, 'scripts', analysis));
				}
				return nodes;
			}

			return [];
		} catch (err) {
			if (err instanceof AdoUnauthorizedError) {
				void this.handleUnauthorized(err);
				return [];
			}
			this.logger.logError('Failed to load tree children', err);
			vscode.window.showErrorMessage(
				`Pipelines Explorer: ${err instanceof Error ? err.message : String(err)}`,
			);
			return [];
		}
	}

	private unauthorizedHandled = false;
	private async handleUnauthorized(err: AdoUnauthorizedError): Promise<void> {
		if (this.unauthorizedHandled) {
			return;
		}
		this.unauthorizedHandled = true;
		this.logger.logWarning(`Auto-reset triggered: ${err.message}`);
		try {
			await this.auth.reset();
		} catch (resetErr) {
			this.logger.logError('Auto-reset failed', resetErr);
		}
		const pick = await vscode.window.showWarningMessage(
			`Pipelines Explorer: ${err.message} You have been signed out.`,
			'Sign in with Microsoft',
			'Sign in with PAT',
		);
		this.unauthorizedHandled = false;
		if (pick === 'Sign in with Microsoft') {
			await vscode.commands.executeCommand('pipelinesexplorer.signInWithMicrosoft');
		} else if (pick === 'Sign in with PAT') {
			await vscode.commands.executeCommand('pipelinesexplorer.signInWithPat');
		}
	}

	private getAnalysis(node: PipelineNode): Promise<PipelineAnalysis> {
		const cached = this.analysisCache.get(node.id!);
		if (cached) {
			return cached;
		}
		const promise = this.analyzer.analyze(
			node.organization.accountName,
			node.project.name,
			node.pipeline.id,
			node.detail,
		);
		this.analysisCache.set(node.id!, promise);
		return promise;
	}

	private getTemplateAnalysis(node: TemplateItemNode): Promise<PipelineAnalysis> {
		const cached = this.analysisCache.get(node.id!);
		if (cached) {
			return cached;
		}
		const promise = this.analyzer.analyzeFile(
			node.organization.accountName,
			node.project.name,
			node.containingRepoId!,
			node.resolvedPath,
		);
		this.analysisCache.set(node.id!, promise);
		return promise;
	}

	/**
	 * Fetch pipeline details in parallel (capped concurrency) and bucket pipelines
	 * by their source repository. Pipelines whose detail call fails or that have
	 * no repository fall into the synthetic "(unknown repository)" bucket.
	 */
	private async groupPipelinesByRepository(
		org: AdoOrganization,
		project: AdoProject,
		pipelines: AdoPipeline[],
	): Promise<RepositoryNode[]> {
		const entries = await mapWithConcurrency(pipelines, 8, async pipeline => {
			try {
				const detail = await this.client.getPipeline(org.accountName, project.name, pipeline.id);
				return { pipeline, detail };
			} catch (err) {
				this.logger.logError(`Failed to fetch detail for pipeline ${pipeline.id}`, err);
				return { pipeline, detail: undefined };
			}
		});

		// Resolve names of TfsGit repositories that the pipelines API didn't include.
		const missing = new Set<string>();
		for (const e of entries) {
			const repo = e.detail?.configuration?.repository;
			if (
				repo?.id &&
				!repo.fullName && !repo.name &&
				(!repo.type || repo.type.toLowerCase() === 'azurereposgit')
			) {
				missing.add(repo.id);
			}
		}
		const resolved = await mapWithConcurrency(
			Array.from(missing), 8,
			async id => {
				const cached = this.repoNameCache.get(id);
				if (cached !== undefined) {
					return { id, name: cached };
				}
				try {
					const r = await this.client.getRepository(org.accountName, project.name, id);
					const name = r?.name ?? '(unknown repository)';
					this.repoNameCache.set(id, name);
					return { id, name };
				} catch (err) {
					this.logger.logError(`Failed to resolve repository ${id}`, err);
					return { id, name: '(unknown repository)' };
				}
			},
		);
		const nameById = new Map<string, string>(resolved.map(r => [r.id, r.name]));

		const buckets = new Map<string, { label: string; type?: string; items: typeof entries }>();
		for (const entry of entries) {
			const repo = entry.detail?.configuration?.repository;
			const repoKey = repo?.id ?? repo?.fullName ?? repo?.name ?? '__unknown__';
			const label =
				repo?.fullName ??
				repo?.name ??
				(repo?.id ? nameById.get(repo.id) : undefined) ??
				'(unknown repository)';
			let bucket = buckets.get(repoKey);
			if (!bucket) {
				bucket = { label, type: repo?.type, items: [] };
				buckets.set(repoKey, bucket);
			}
			bucket.items.push(entry);
		}

		return Array.from(buckets.entries())
			.map(([key, b]) => {
				const linked = this.links.get({
					orgAccountId: org.accountId,
					projectId: project.id,
					repoKey: key,
				});
				return new RepositoryNode(org, project, key, b.label, b.type, b.items, linked);
			})
			.sort((a, b) => a.repoLabel.localeCompare(b.repoLabel));
	}
}

async function mapWithConcurrency<T, R>(
	items: T[],
	concurrency: number,
	worker: (item: T) => Promise<R>,
): Promise<R[]> {
	const results: R[] = new Array(items.length);
	let next = 0;
	const runners = Array.from({ length: Math.min(concurrency, items.length) }, async () => {
		while (true) {
			const i = next++;
			if (i >= items.length) {
				return;
			}
			results[i] = await worker(items[i]);
		}
	});
	await Promise.all(runners);
	return results;
}

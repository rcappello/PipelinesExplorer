import * as vscode from 'vscode';
import { AdoClient, AdoOrganization, AdoPipeline, AdoPipelineDetail, AdoProject, AdoUnauthorizedError } from './adoClient';
import { AuthService } from './authService';
import { LoggingService } from './LoggingService';
import { PipelineAnalysis, PipelineYamlAnalyzer, ScriptKind, ScriptRef, TemplateRef } from './pipelineYamlAnalyzer';
import { RepoBranchService } from './repoBranchService';
import { WorkspaceLinkService } from './workspaceLinkService';

type Node =
	| ConnectionInfoNode
	| OrganizationNode
	| ProjectNode
	| RepositoryNode
	| PipelineNode
	| GroupNode
	| TemplateItemNode
	| ScriptItemNode
	| InfoNode;

/**
 * Header row shown at the top of the tree summarising the active sign-in
 * (kind + account + tenant). Clicking the row opens the tenant picker when
 * Microsoft sign-in is in use, otherwise it is a no-op informational item.
 */
export class ConnectionInfoNode extends vscode.TreeItem {
	readonly kind = 'connection-info' as const;
	constructor(label: string, tooltip: string, isMicrosoft: boolean) {
		super(label, vscode.TreeItemCollapsibleState.None);
		this.id = 'connection-info';
		this.tooltip = tooltip;
		this.iconPath = new vscode.ThemeIcon(isMicrosoft ? 'account' : 'key');
		this.contextValue = 'pipelinesexplorer.connectionInfo';
		if (isMicrosoft) {
			this.command = {
				command: 'pipelinesexplorer.selectTenant',
				title: vscode.l10n.t('Select Microsoft Entra tenant'),
			};
		}
		this.accessibilityInformation = { label: tooltip };
	}
}

export class OrganizationNode extends vscode.TreeItem {
	readonly kind = 'organization' as const;
	constructor(public readonly organization: AdoOrganization) {
		super(organization.accountName, vscode.TreeItemCollapsibleState.Collapsed);
		this.id = `org:${organization.accountId}`;
		this.contextValue = 'pipelinesexplorer.organization';
		this.iconPath = new vscode.ThemeIcon('organization');
		this.tooltip = organization.accountUri;
		this.accessibilityInformation = {
			label: vscode.l10n.t('Organization {0}', organization.accountName),
		};
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
		this.accessibilityInformation = {
			label: vscode.l10n.t('Project {0}', project.name),
		};
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
				title: vscode.l10n.t('Open Pipeline YAML'),
				arguments: [this],
			};
		}
		this.accessibilityInformation = {
			label: vscode.l10n.t('Pipeline {0}', pipeline.name),
		};
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
		public readonly branchOverride?: string,
	) {
		super(repoLabel, vscode.TreeItemCollapsibleState.Collapsed);
		this.id = `repo:${organization.accountId}:${project.id}:${repoKey}`;
		this.contextValue = linkedFolder
			? 'pipelinesexplorer.repository.linked'
			: 'pipelinesexplorer.repository';
		this.iconPath = new vscode.ThemeIcon(linkedFolder ? 'repo-clone' : 'repo');
		const pieces = [`${pipelines.length}`];
		if (repoType) { pieces.push(repoType); }
		if (linkedFolder) { pieces.push(vscode.l10n.t('linked')); }
		if (branchOverride) { pieces.push(vscode.l10n.t('branch: {0}', branchOverride)); }
		this.description = pieces.join(' · ');
		const tooltipLines = [repoType ? `${repoLabel} (${repoType})` : repoLabel];
		if (linkedFolder) { tooltipLines.push(vscode.l10n.t('Linked: {0}', linkedFolder)); }
		tooltipLines.push(branchOverride
			? vscode.l10n.t('Reading YAML from branch: {0}', branchOverride)
			: vscode.l10n.t('Reading YAML from default branch'));
		this.tooltip = tooltipLines.join('\n');
		let a11yLabel: string;
		if (linkedFolder && branchOverride) {
			a11yLabel = vscode.l10n.t('Repository {0}, {1} pipelines, linked to local folder, branch override {2}', repoLabel, pipelines.length, branchOverride);
		} else if (linkedFolder) {
			a11yLabel = vscode.l10n.t('Repository {0}, {1} pipelines, linked to local folder', repoLabel, pipelines.length);
		} else if (branchOverride) {
			a11yLabel = vscode.l10n.t('Repository {0}, {1} pipelines, branch override {2}', repoLabel, pipelines.length, branchOverride);
		} else {
			a11yLabel = vscode.l10n.t('Repository {0}, {1} pipelines', repoLabel, pipelines.length);
		}
		this.accessibilityInformation = { label: a11yLabel };
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
		super(group === 'templates' ? vscode.l10n.t('Templates') : vscode.l10n.t('Scripts'),
			vscode.TreeItemCollapsibleState.Collapsed);
		this.id = `${parent.id}:group:${group}`;
		this.contextValue = `pipelinesexplorer.group.${group}`;
		this.iconPath = new vscode.ThemeIcon(group === 'templates' ? 'files' : 'terminal');
		const count = group === 'templates' ? analysis.templates.length : analysis.scripts.length;
		this.description = String(count);
		const groupLabel = group === 'templates' ? vscode.l10n.t('Templates') : vscode.l10n.t('Scripts');
		this.accessibilityInformation = {
			label: vscode.l10n.t('Group {0}, {1} items', groupLabel, count),
		};
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
			title: vscode.l10n.t('Open Template'),
			arguments: [this],
		};
		this.accessibilityInformation = {
			label: vscode.l10n.t('Template {0}', ref.path),
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
	constructor(public readonly parent: GroupNode, public readonly ref: ScriptRef) {
		const itemLabel = ref.filePath
			? basename(ref.filePath)
			: (ref.inline ? vscode.l10n.t('(inline script)') : vscode.l10n.t('(unknown source)'));
		super(itemLabel, vscode.TreeItemCollapsibleState.None);
		// Include line in the id so that multiple inline scripts of the same task type
		// (e.g. several `PowerShell@2` `targetType: inline` blocks in the same YAML)
		// get distinct ids — otherwise VS Code conflates them in the TreeView and
		// every visual entry routes clicks to a single backing node.
		const idTail = ref.filePath
			?? (ref.inline ? `inline@${ref.line ?? '?'}` : 'unknown');
		this.id = `${parent.id}:script:${ref.task}:${ref.kind}:${idTail}`;
		this.iconPath = new vscode.ThemeIcon(iconForScriptKind(ref.kind));
		this.description = ref.task;
		this.tooltip = ref.filePath
			? `${ref.task} → ${ref.filePath}`
			: `${ref.task} (${ref.inline ? `inline${ref.line ? ` @ line ${ref.line}` : ''}` : 'unknown'})`;
		this.contextValue = 'pipelinesexplorer.script';
		if (ref.filePath || (ref.inline && ref.line)) {
			this.command = {
				command: 'pipelinesexplorer.openItem',
				title: ref.filePath ? vscode.l10n.t('Open Script') : vscode.l10n.t('Open Inline Script Location'),
				arguments: [this],
			};
		}
		this.accessibilityInformation = {
			label: vscode.l10n.t('Script {0}', itemLabel),
		};
	}
}

function basename(p: string): string {
	const clean = p.replace(/\\/g, '/').replace(/\/+$/, '');
	const i = clean.lastIndexOf('/');
	return i >= 0 ? clean.slice(i + 1) : clean;
}

/** Pick a VS Code codicon id for a given script kind. */
function iconForScriptKind(kind: ScriptKind): string {
	switch (kind) {
		case 'powershell': return 'terminal-powershell';
		case 'bash': return 'terminal-bash';
		case 'cmd': return 'terminal-cmd';
		case 'python': return 'snake';
		case 'azurecli': return 'azure';
		default: return 'terminal';
	}
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
		this.accessibilityInformation = {
			label: vscode.l10n.t('Information: {0}', message),
		};
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
		private readonly branches: RepoBranchService,
	) {
		this.analyzer = new PipelineYamlAnalyzer(client, logger);
		this.auth.onDidChangeSession(() => this.refresh());
		this.links.onDidChange(() => this.refresh());
		this.branches.onDidChange(() => this.refresh());
	}

	refresh(): void {
		this.analysisCache.clear();
		this.repoNameCache.clear();
		this._onDidChangeTreeData.fire();
	}

	getTreeItem(element: Node): vscode.TreeItem {
		return element;
	}

	private buildConnectionInfoNode(): ConnectionInfoNode | undefined {
		const session = this.auth.session;
		if (!session) {
			return undefined;
		}
		if (session.kind === 'pat') {
			const label = vscode.l10n.t('Personal Access Token');
			const tooltip = vscode.l10n.t('Connected as {0} via Personal Access Token', session.accountLabel);
			return new ConnectionInfoNode(label, tooltip, false);
		}
		const tenantName = this.auth.getStoredTenantName();
		const tenantDisplay = tenantName ?? (session.tenantId && this.auth.getStoredTenant() ? session.tenantId : vscode.l10n.t('Default tenant'));
		const label = vscode.l10n.t('Microsoft Entra · {0}', tenantDisplay);
		const tooltipBase = vscode.l10n.t('Connected as {0} — tenant: {1}', session.accountLabel, session.tenantId ?? vscode.l10n.t('Default tenant'));
		const tooltip = vscode.l10n.t('{0} — click to switch tenant', tooltipBase);
		return new ConnectionInfoNode(label, tooltip, true);
	}

	async getChildren(element?: Node): Promise<Node[]> {
		if (!this.auth.session) {
			return [];
		}

		try {
			if (!element) {
				const profile = await this.client.getProfile();
				const orgs = await this.client.listOrganizations(profile.id);
				const header = this.buildConnectionInfoNode();
				if (orgs.length === 0) {
					const empty = new InfoNode('no-orgs', vscode.l10n.t('No Azure DevOps organizations found for this tenant'));
					return header ? [header, empty] : [empty];
				}
				const orgNodes = orgs
					.sort((a, b) => a.accountName.localeCompare(b.accountName))
					.map(o => new OrganizationNode(o));
				return header ? [header, ...orgNodes] : orgNodes;
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
					return [new InfoNode(element.id!, vscode.l10n.t('No pipelines in this project'))];
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
						return [new InfoNode(element.id!, vscode.l10n.t('No templates referenced'))];
					}
					return element.analysis.templates.map(t => new TemplateItemNode(
						element, t,
						ctx.organization, ctx.project, ctx.pipelineRepoKey,
						ctx.repoId, ctx.baseDir,
					));
				}
				if (element.analysis.scripts.length === 0) {
					return [new InfoNode(element.id!, vscode.l10n.t('No scripts referenced'))];
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
				vscode.l10n.t('Pipelines Explorer: {0}', err instanceof Error ? err.message : String(err)),
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
		const signInMs = vscode.l10n.t('Sign in with Microsoft');
		const signInPat = vscode.l10n.t('Sign in with PAT');
		const pick = await vscode.window.showWarningMessage(
			vscode.l10n.t('Pipelines Explorer: {0} You have been signed out.', err.message),
			signInMs,
			signInPat,
		);
		this.unauthorizedHandled = false;
		if (pick === signInMs) {
			await vscode.commands.executeCommand('pipelinesexplorer.signInWithMicrosoft');
		} else if (pick === signInPat) {
			await vscode.commands.executeCommand('pipelinesexplorer.signInWithPat');
		}
	}

	private getAnalysis(node: PipelineNode): Promise<PipelineAnalysis> {
		const branch = this.branches.get({
			orgAccountId: node.organization.accountId,
			projectId: node.project.id,
			repoKey: node.repoKey,
		});
		const cacheKey = `${node.id!}::${branch ?? ''}`;
		const cached = this.analysisCache.get(cacheKey);
		if (cached) {
			return cached;
		}
		this.logger.logInfo(`Analyzing pipeline "${node.pipeline.name}" (id=${node.pipeline.id}) on branch "${branch ?? '<default>'}"`);
		const promise = this.analyzer.analyze(
			node.organization.accountName,
			node.project.name,
			node.pipeline.id,
			node.detail,
			branch,
		);
		this.analysisCache.set(cacheKey, promise);
		return promise;
	}

	private getTemplateAnalysis(node: TemplateItemNode): Promise<PipelineAnalysis> {
		const branch = this.branches.get({
			orgAccountId: node.organization.accountId,
			projectId: node.project.id,
			repoKey: node.pipelineRepoKey,
		});
		const cacheKey = `${node.id!}::${branch ?? ''}`;
		const cached = this.analysisCache.get(cacheKey);
		if (cached) {
			return cached;
		}
		this.logger.logInfo(`Analyzing template "${node.resolvedPath}" on branch "${branch ?? '<default>'}"`);
		const promise = this.analyzer.analyzeFile(
			node.organization.accountName,
			node.project.name,
			node.containingRepoId!,
			node.resolvedPath,
			branch,
		);
		this.analysisCache.set(cacheKey, promise);
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
					const name = r?.name ?? vscode.l10n.t('(unknown repository)');
					this.repoNameCache.set(id, name);
					return { id, name };
				} catch (err) {
					this.logger.logError(`Failed to resolve repository ${id}`, err);
					return { id, name: vscode.l10n.t('(unknown repository)') };
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
				vscode.l10n.t('(unknown repository)');
			let bucket = buckets.get(repoKey);
			if (!bucket) {
				bucket = { label, type: repo?.type, items: [] };
				buckets.set(repoKey, bucket);
			}
			bucket.items.push(entry);
		}

		return Array.from(buckets.entries())
			.map(([key, b]) => {
				const keyObj = {
					orgAccountId: org.accountId,
					projectId: project.id,
					repoKey: key,
				};
				const linked = this.links.get(keyObj);
				const branchOverride = this.branches.get(keyObj);
				return new RepositoryNode(org, project, key, b.label, b.type, b.items, linked, branchOverride);
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

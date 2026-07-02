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
	| InfoNode
	| FilterStatusNode;

/** Runtime state of the tree filter feature (see plan 001). */
export type FilterState = 'idle' | 'scanning' | 'ready' | 'no-results' | 'capped';

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
	readonly totalCount: number;
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
		this.totalCount = group === 'templates' ? analysis.templates.length : analysis.scripts.length;
		this.description = String(this.totalCount);
		const groupLabel = group === 'templates' ? vscode.l10n.t('Templates') : vscode.l10n.t('Scripts');
		this.accessibilityInformation = {
			label: vscode.l10n.t('Group {0}, {1} items', groupLabel, this.totalCount),
		};
	}

	/**
	 * Update the group's `description` (and its a11y label) to reflect how
	 * many items are currently visible under the active filter. Pass
	 * `undefined` (or the total count) to reset to the plain total.
	 */
	updateFilteredCount(visibleCount: number | undefined): void {
		const groupLabel = this.group === 'templates' ? vscode.l10n.t('Templates') : vscode.l10n.t('Scripts');
		if (visibleCount === undefined || visibleCount === this.totalCount) {
			this.description = String(this.totalCount);
			this.accessibilityInformation = {
				label: vscode.l10n.t('Group {0}, {1} items', groupLabel, this.totalCount),
			};
		} else {
			this.description = `${visibleCount}/${this.totalCount}`;
			this.accessibilityInformation = {
				label: vscode.l10n.t('Group {0}, {1} of {2} items match filter', groupLabel, visibleCount, this.totalCount),
			};
		}
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

/**
 * Normalise a raw filter term entered by the user: trim + lowercase. Returns
 * `undefined` if the term is empty (so callers can treat "no filter" and
 * "empty filter" identically).
 */
export function normalizeFilterTerm(term: string | undefined): string | undefined {
	if (!term) { return undefined; }
	const t = term.trim();
	return t.length === 0 ? undefined : t.toLowerCase();
}

/** True if `haystack` contains `needle` (case-insensitive). */
export function matchesFilterTerm(haystack: string | undefined, needle: string | undefined): boolean {
	if (!needle || !haystack) { return false; }
	return haystack.toLowerCase().includes(needle);
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

/**
 * Virtual node shown at the top of the tree while a filter is active. Not
 * matched itself; only carries status text.
 */
export class FilterStatusNode extends vscode.TreeItem {
	readonly kind = 'filterStatus' as const;
	constructor(term: string, state: FilterState, matchCount: number, cappedAt: number | undefined) {
		let label: string;
		switch (state) {
			case 'scanning':
				label = vscode.l10n.t('Filter active: {0} — scanning…', term);
				break;
			case 'no-results':
				label = vscode.l10n.t('Filter active: {0} — no results', term);
				break;
			case 'capped':
				label = vscode.l10n.t('Filter active: {0} — {1} result(s) (scope capped at {2} pipelines)', term, matchCount, cappedAt ?? 0);
				break;
			default:
				label = vscode.l10n.t('Filter active: {0} — {1} result(s)', term, matchCount);
				break;
		}
		super(label, vscode.TreeItemCollapsibleState.None);
		this.id = `__filter-status__:${state}:${term}`;
		this.iconPath = new vscode.ThemeIcon(state === 'scanning' ? 'sync~spin' : 'filter');
		this.tooltip = vscode.l10n.t('Filter searches every organization, project and repository the signed-in identity can see. YAML analysis is capped at {0} pipelines.', PipelinesTreeProvider.FILTER_PIPELINE_CAP);
		this.contextValue = 'pipelinesexplorer.filterStatus';
		this.command = {
			command: 'pipelinesexplorer.filter',
			title: vscode.l10n.t('Edit filter'),
		};
		this.accessibilityInformation = { label };
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

	// ==== Filter state (see plan 001) ==========================================
	/** Maximum number of pipelines analyzed per filter scan (plan 001 §2). */
	static readonly FILTER_PIPELINE_CAP = 500;
	/** Maximum number of matched PipelineNodes revealed after a scan (plan 001 §8). */
	static readonly FILTER_REVEAL_CAP = 50;
	/**
	 * Maximum recursion depth followed by the filter scan when descending
	 * into same-repo nested templates. Guards against pathological template
	 * graphs on top of the per-file cycle check.
	 */
	static readonly FILTER_MAX_TEMPLATE_DEPTH = 10;
	private static readonly ROOT_CACHE_KEY = '__root__';

	private currentFilter: string | undefined;
	private filterState: FilterState = 'idle';
	/** Ids of nodes that match the current filter (leaf hits). */
	private readonly matchedIds = new Set<string>();
	/** Ids of nodes that must remain visible because they contain a match (ancestors + group nodes). */
	private readonly visibleIds = new Set<string>();
	private filterMatchCount = 0;
	private filterCappedAt: number | undefined;
	private filterScanCts: vscode.CancellationTokenSource | undefined;

	/** Cache of children keyed by parent id (or `ROOT_CACHE_KEY` for the root). */
	private readonly nodeChildrenCache = new Map<string, Node[]>();
	/** Reverse index child-id → parent-id, used to mark ancestors during scan. */
	private readonly parentByChildId = new Map<string, string>();

	private readonly _onDidCompleteFilterScan = new vscode.EventEmitter<PipelineNode[]>();
	/** Fires after a filter scan finishes, carrying up to `FILTER_REVEAL_CAP` matched pipelines. */
	readonly onDidCompleteFilterScan: vscode.Event<PipelineNode[]> = this._onDidCompleteFilterScan.event;

	private treeView: vscode.TreeView<Node> | undefined;

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

	/** Attach the TreeView so the provider can drive `reveal` after a filter scan. */
	setTreeView(view: vscode.TreeView<Node>): void {
		this.treeView = view;
	}

	refresh(): void {
		this.analysisCache.clear();
		this.repoNameCache.clear();
		this.nodeChildrenCache.clear();
		this.parentByChildId.clear();
		this.clearFilterState(/* fireChange */ false);
		this._onDidChangeTreeData.fire();
	}

	/** Current filter term (already lowercased) or `undefined`. */
	getCurrentFilter(): string | undefined {
		return this.currentFilter;
	}

	/**
	 * Apply or clear the tree filter. Any in-flight scan is cancelled.
	 * Passing an empty / whitespace-only string clears the filter.
	 */
	setFilter(term: string | undefined): void {
		const normalized = normalizeFilterTerm(term);
		this.filterScanCts?.cancel();
		this.filterScanCts = undefined;

		if (!normalized) {
			const wasActive = !!this.currentFilter;
			this.clearFilterState(/* fireChange */ false);
			if (wasActive) {
				this._onDidChangeTreeData.fire();
			}
			return;
		}

		this.currentFilter = normalized;
		this.matchedIds.clear();
		this.visibleIds.clear();
		this.filterMatchCount = 0;
		this.filterCappedAt = undefined;
		this.filterState = 'scanning';
		void vscode.commands.executeCommand('setContext', 'pipelinesexplorer.filterActive', true);
		this._onDidChangeTreeData.fire();

		const cts = new vscode.CancellationTokenSource();
		this.filterScanCts = cts;
		void this.runFilterScan(normalized, cts.token).catch(err => {
			if (!cts.token.isCancellationRequested) {
				this.logger.logError('Filter scan failed', err);
			}
		});
	}

	private clearFilterState(fireChange: boolean): void {
		this.currentFilter = undefined;
		this.filterState = 'idle';
		this.matchedIds.clear();
		this.visibleIds.clear();
		this.filterMatchCount = 0;
		this.filterCappedAt = undefined;
		void vscode.commands.executeCommand('setContext', 'pipelinesexplorer.filterActive', false);
		if (fireChange) {
			this._onDidChangeTreeData.fire();
		}
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
		const children = await this.fetchChildren(element);
		this.populateChildCache(element, children);

		this.applyGroupFilterCounts(children);

		if (!this.currentFilter) {
			return children;
		}

		if (!element) {
			return [this.buildFilterStatusNode(), ...children.filter(c => this.isVisibleUnderFilter(c))];
		}
		return children.filter(c => this.isVisibleUnderFilter(c));
	}

	/**
	 * Refresh the `description` on every {@link GroupNode} in `children` so it
	 * shows the filtered / total count (e.g. `4/7`) while a filter is active,
	 * or the plain total when the filter is cleared. Counts are derived from
	 * the same `matchedIds` / `visibleIds` sets that drive
	 * {@link isVisibleUnderFilter}, so the label always matches the number of
	 * items that will actually render under the group.
	 */
	private applyGroupFilterCounts(children: Node[]): void {
		for (const c of children) {
			if (c.kind !== 'group') { continue; }
			if (!this.currentFilter) {
				c.updateFilteredCount(undefined);
				continue;
			}
			const groupId = c.id!;
			let visible = 0;
			if (c.group === 'templates') {
				for (const t of c.analysis.templates) {
					const tplId = `${groupId}:tpl:${t.raw}`;
					if (this.matchedIds.has(tplId) || this.visibleIds.has(tplId)) { visible++; }
				}
			} else {
				for (const s of c.analysis.scripts) {
					const idTail = s.filePath ?? (s.inline ? `inline@${s.line ?? '?'}` : 'unknown');
					const sId = `${groupId}:script:${s.task}:${s.kind}:${idTail}`;
					if (this.matchedIds.has(sId) || this.visibleIds.has(sId)) { visible++; }
				}
			}
			c.updateFilteredCount(visible);
		}
	}

	private populateChildCache(element: Node | undefined, children: Node[]): void {
		const cacheKey = element?.id ?? PipelinesTreeProvider.ROOT_CACHE_KEY;
		this.nodeChildrenCache.set(cacheKey, children);
		for (const c of children) {
			if (c.id) {
				this.parentByChildId.set(c.id, cacheKey);
			}
		}
	}

	/**
	 * Returns the children of `element`, hitting `fetchChildren` (and
	 * populating `nodeChildrenCache` + `parentByChildId`) only on cache miss.
	 * Never applies the filter — used by the filter preload to force lazy
	 * subtrees to materialise without polluting the tree UI.
	 */
	private async ensureLoaded(element: Node | undefined): Promise<Node[]> {
		const cacheKey = element?.id ?? PipelinesTreeProvider.ROOT_CACHE_KEY;
		const cached = this.nodeChildrenCache.get(cacheKey);
		if (cached) {
			return cached;
		}
		const children = await this.fetchChildren(element);
		this.populateChildCache(element, children);
		return children;
	}

	/**
	 * True if `node` is either matched by the current filter or is an ancestor
	 * of a matched node. The connection header row is always kept.
	 */
	private isVisibleUnderFilter(node: Node): boolean {
		if (node.kind === 'connection-info' || node.kind === 'filterStatus') {
			return true;
		}
		if (!node.id) {
			return false;
		}
		return this.matchedIds.has(node.id) || this.visibleIds.has(node.id);
	}

	private buildFilterStatusNode(): FilterStatusNode {
		return new FilterStatusNode(
			this.currentFilter ?? '',
			this.filterState,
			this.filterMatchCount,
			this.filterCappedAt,
		);
	}

	/**
	 * Fetches children the same way the tree used to do before the filter
	 * feature landed. Called by `getChildren` and cached into
	 * `nodeChildrenCache`. Extracted so the filter branch can decorate the
	 * result without duplicating the fetch logic.
	 */
	private async fetchChildren(element?: Node): Promise<Node[]> {
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

	/**
	 * Enables `TreeView.reveal` for arbitrary nodes: VS Code needs the parent
	 * chain to reveal an element and does not track it on its own. We walk the
	 * cached child index built during `getChildren`.
	 */
	getParent(element: Node): Node | undefined {
		if (!element.id) { return undefined; }
		const parentId = this.parentByChildId.get(element.id);
		if (!parentId || parentId === PipelinesTreeProvider.ROOT_CACHE_KEY) {
			return undefined;
		}
		const siblings = this.nodeChildrenCache.get(this.parentByChildId.get(parentId) ?? PipelinesTreeProvider.ROOT_CACHE_KEY);
		return siblings?.find(n => n.id === parentId);
	}

	/**
	 * Forces every organization → project → repository → pipeline subtree
	 * reachable from the signed-in identity to materialise into the child
	 * cache, so that `collectLoadedPipelines` can see every pipeline the
	 * filter is expected to search. Silently swallows per-node failures (they
	 * are logged) so a single broken project does not abort the whole scan.
	 */
	private async preloadAllForFilter(token: vscode.CancellationToken): Promise<void> {
		const roots = await this.ensureLoaded(undefined).catch(err => {
			this.logger.logError('Filter preload: listing organizations failed', err);
			return [] as Node[];
		});
		if (token.isCancellationRequested) { return; }

		const orgs = roots.filter((n): n is OrganizationNode => n.kind === 'organization');

		await mapWithConcurrency(orgs, 4, async org => {
			if (token.isCancellationRequested) { return; }
			const projects = await this.ensureLoaded(org).catch(err => {
				this.logger.logError(`Filter preload: listing projects for ${org.organization.accountName} failed`, err);
				return [] as Node[];
			});
			if (token.isCancellationRequested) { return; }
			const projs = projects.filter((n): n is ProjectNode => n.kind === 'project');

			await mapWithConcurrency(projs, 4, async proj => {
				if (token.isCancellationRequested) { return; }
				const projectChildren = await this.ensureLoaded(proj).catch(err => {
					this.logger.logError(`Filter preload: listing pipelines for ${org.organization.accountName}/${proj.project.name} failed`, err);
					return [] as Node[];
				});
				if (token.isCancellationRequested) { return; }
				const repos = projectChildren.filter((n): n is RepositoryNode => n.kind === 'repository');
				for (const repo of repos) {
					if (token.isCancellationRequested) { return; }
					await this.ensureLoaded(repo).catch(err => {
						this.logger.logError(`Filter preload: listing pipelines for repo ${repo.repoLabel} failed`, err);
						return [] as Node[];
					});
				}
			});
		});
	}

	/**
	 * Walks every organization / project / repository the signed-in identity
	 * can see (auto-loading lazy subtrees on the fly), matches pipelines /
	 * templates / scripts against `term` and populates `matchedIds` +
	 * `visibleIds`. Fires `_onDidCompleteFilterScan` with up to
	 * `FILTER_REVEAL_CAP` matched pipeline nodes for the caller (extension.ts)
	 * to reveal.
	 */
	private async runFilterScan(term: string, token: vscode.CancellationToken): Promise<void> {
		await this.preloadAllForFilter(token);
		if (token.isCancellationRequested) { return; }

		const pipelines = this.collectLoadedPipelines();
		const cap = PipelinesTreeProvider.FILTER_PIPELINE_CAP;
		const capped = pipelines.length > cap;
		const scanTargets = capped ? pipelines.slice(0, cap) : pipelines;

		if (capped) {
			this.filterCappedAt = cap;
		}

		const matchedPipelines: PipelineNode[] = [];

		// Pass 1: match pipeline names + root YAML basename. This is synchronous
		// and gives the user immediate feedback for the cheapest case.
		for (const pipe of scanTargets) {
			if (token.isCancellationRequested) { return; }
			if (pipelineMatchesFilter(pipe, term)) {
				this.markMatch(pipe.id!, pipe);
				matchedPipelines.push(pipe);
			}
		}

		// Pass 2: fetch analysis for each pipeline (cached across scans) and
		// match template / script leaves, recursing into same-repo nested
		// templates. Concurrency mirrors the rest of the codebase (see
		// mapWithConcurrency, cap 8).
		await mapWithConcurrency(scanTargets, 8, async pipe => {
			if (token.isCancellationRequested) { return; }
			try {
				const found = await this.scanPipelineTree(pipe, term, token);
				if (found && !matchedPipelines.includes(pipe)) {
					matchedPipelines.push(pipe);
				}
			} catch (err) {
				this.logger.logError(`Filter scan: analyzing "${pipe.pipeline.name}" failed`, err);
			}
		});

		if (token.isCancellationRequested) { return; }

		this.filterMatchCount = this.matchedIds.size;
		if (this.filterMatchCount === 0) {
			this.filterState = 'no-results';
		} else if (capped) {
			this.filterState = 'capped';
		} else {
			this.filterState = 'ready';
		}
		this._onDidChangeTreeData.fire();
		this._onDidCompleteFilterScan.fire(matchedPipelines.slice(0, PipelinesTreeProvider.FILTER_REVEAL_CAP));
	}

	/**
	 * Scans a pipeline's root YAML plus all reachable same-repo templates
	 * (depth-limited, cycle-safe) and records matches. Returns `true` if any
	 * template or script under this pipeline matched the term.
	 * Cross-repo templates are skipped (the analyzer cannot resolve the target
	 * repository from an alias alone).
	 */
	private async scanPipelineTree(pipe: PipelineNode, term: string, token: vscode.CancellationToken): Promise<boolean> {
		const analysis = await this.getAnalysis(pipe);
		if (token.isCancellationRequested) { return false; }

		const pipeId = pipe.id!;
		const rootTplGroupId = `${pipeId}:group:templates`;
		const rootScriptGroupId = `${pipeId}:group:scripts`;
		const rootPath = pipe.detail?.configuration?.path;
		const rootDir = rootPath ? dirOfRepoPath(rootPath) : '';
		const repoId = pipe.detail?.configuration?.repository?.id;
		const branch = this.branches.get({
			orgAccountId: pipe.organization.accountId,
			projectId: pipe.project.id,
			repoKey: pipe.repoKey,
		});

		let anyMatch = false;

		// Scripts referenced directly by the pipeline root YAML.
		for (const s of analysis.scripts) {
			if (!s.filePath) { continue; }
			if (matchesFilterTerm(basename(s.filePath), term)) {
				const sId = `${rootScriptGroupId}:script:${s.task}:${s.kind}:${s.filePath}`;
				this.matchedIds.add(sId);
				this.visibleIds.add(rootScriptGroupId);
				anyMatch = true;
			}
		}

		interface Frame {
			tpls: TemplateRef[];
			parentId: string;         // id of the Templates group at this level
			ancestorIds: string[];    // ids to mark visible when any match is found in this frame or below
			containingDir: string;    // dir of the YAML whose `templates:` we are scanning
			depth: number;
		}

		const visitedFiles = new Set<string>();
		const queue: Frame[] = [{
			tpls: analysis.templates,
			parentId: rootTplGroupId,
			ancestorIds: [rootTplGroupId],
			containingDir: rootDir,
			depth: 0,
		}];

		while (queue.length > 0) {
			if (token.isCancellationRequested) { return anyMatch; }
			const frame = queue.shift()!;

			for (const t of frame.tpls) {
				const tplId = `${frame.parentId}:tpl:${t.raw}`;

				if (matchesFilterTerm(basename(t.path), term)) {
					this.matchedIds.add(tplId);
					for (const a of frame.ancestorIds) { this.visibleIds.add(a); }
					anyMatch = true;
				}

				// Recurse only into same-repo templates (cross-repo aliases
				// would need repository resolution that the analyzer doesn't
				// currently do). Enforce depth cap and cycle guard.
				if (t.repository || !repoId || frame.depth >= PipelinesTreeProvider.FILTER_MAX_TEMPLATE_DEPTH) {
					continue;
				}
				const resolvedPath = resolveRepoPath(frame.containingDir, t.path);
				const cycleKey = `${repoId}::${resolvedPath}::${branch ?? ''}`;
				if (visitedFiles.has(cycleKey)) { continue; }
				visitedFiles.add(cycleKey);

				let subAnalysis: PipelineAnalysis;
				try {
					subAnalysis = await this.analyzer.analyzeFile(
						pipe.organization.accountName,
						pipe.project.name,
						repoId,
						resolvedPath,
						branch,
					);
				} catch (err) {
					this.logger.logError(`Filter scan: analyzing template "${resolvedPath}" failed`, err);
					continue;
				}
				if (token.isCancellationRequested) { return anyMatch; }

				const subTplGroupId = `${tplId}:group:templates`;
				const subScriptGroupId = `${tplId}:group:scripts`;
				const nextAncestors = [...frame.ancestorIds, tplId, subTplGroupId];

				for (const s of subAnalysis.scripts) {
					if (!s.filePath) { continue; }
					if (matchesFilterTerm(basename(s.filePath), term)) {
						const sId = `${subScriptGroupId}:script:${s.task}:${s.kind}:${s.filePath}`;
						this.matchedIds.add(sId);
						this.visibleIds.add(subScriptGroupId);
						for (const a of frame.ancestorIds) { this.visibleIds.add(a); }
						this.visibleIds.add(tplId);
						anyMatch = true;
					}
				}

				if (subAnalysis.templates.length > 0) {
					queue.push({
						tpls: subAnalysis.templates,
						parentId: subTplGroupId,
						ancestorIds: nextAncestors,
						containingDir: dirOfRepoPath(resolvedPath),
						depth: frame.depth + 1,
					});
				}
			}
		}

		if (anyMatch && !this.matchedIds.has(pipeId)) {
			this.visibleIds.add(pipeId);
			this.markAncestors(pipeId);
		}
		return anyMatch;
	}

	/** Walks the cached child index and returns every loaded pipeline node. */
	private collectLoadedPipelines(): PipelineNode[] {
		const out: PipelineNode[] = [];
		const walk = (parentKey: string): void => {
			const children = this.nodeChildrenCache.get(parentKey);
			if (!children) { return; }
			for (const c of children) {
				if (c.kind === 'pipeline') {
					out.push(c);
				} else if (c.id && this.nodeChildrenCache.has(c.id)) {
					walk(c.id);
				}
			}
		};
		walk(PipelinesTreeProvider.ROOT_CACHE_KEY);
		return out;
	}

	/** Records a leaf match and marks all its ancestors as visible. */
	private markMatch(nodeId: string, _node: Node): void {
		this.matchedIds.add(nodeId);
		this.markAncestors(nodeId);
	}

	private markAncestors(nodeId: string): void {
		let cur = this.parentByChildId.get(nodeId);
		while (cur && cur !== PipelinesTreeProvider.ROOT_CACHE_KEY) {
			this.visibleIds.add(cur);
			cur = this.parentByChildId.get(cur);
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

/**
 * True if a `PipelineNode` matches the given (already lowercased) filter term
 * on either the logical pipeline name or the basename of its root YAML file.
 * Exported for unit testing.
 */
export function pipelineMatchesFilter(node: PipelineNode, term: string): boolean {
	if (matchesFilterTerm(node.pipeline.name, term)) {
		return true;
	}
	const rootPath = node.detail?.configuration?.path;
	if (rootPath && matchesFilterTerm(basename(rootPath), term)) {
		return true;
	}
	return false;
}

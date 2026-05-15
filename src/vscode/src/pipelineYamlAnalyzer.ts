import { isMap, LineCounter, parseDocument, visit, YAMLMap } from 'yaml';
import * as vscode from 'vscode';
import { AdoClient, AdoPipelineDetail } from './adoClient';
import { LoggingService } from './LoggingService';

export interface TemplateRef {
	/** Raw `template:` value as written in YAML (e.g. `steps/build.yml@templates`). */
	raw: string;
	/** Local path portion (no `@repo` suffix). */
	path: string;
	/** Optional repository alias if the template lives in another repo. */
	repository?: string;
}

/** Detected script flavour, used to pick the icon shown in the tree. */
export type ScriptKind = 'powershell' | 'bash' | 'cmd' | 'python' | 'azurecli' | 'unknown';

export interface ScriptRef {
	/** Task identifier (e.g. `PowerShell@2`) or shorthand keyword (`script`, `bash`, `pwsh`, `powershell`). */
	task: string;
	/** Path of the script file when the task references a file. */
	filePath?: string;
	/** True if the task uses an inline script (no external file). */
	inline: boolean;
	/** 1-based line number of the task in the source YAML (when known). */
	line?: number;
	/** Detected script kind, used to choose the icon. */
	kind: ScriptKind;
}

export interface PipelineAnalysis {
	templates: TemplateRef[];
	scripts: ScriptRef[];
	/** Path of the root pipeline YAML, useful for tooltip. */
	rootPath?: string;
	/** Non-fatal warning produced while loading or parsing the YAML. */
	warning?: string;
}

/** Map of fully-qualified ADO task ids (lowercased, no `@N` suffix) to their default kind. */
const TASK_KIND_MAP: Record<string, ScriptKind> = {
	'powershell': 'powershell',
	'azurepowershell': 'powershell',
	'powershellontargetmachines': 'powershell',
	'bash': 'bash',
	'shellscript': 'bash',
	'cmdline': 'cmd',
	'batchscript': 'cmd',
	'pythonscript': 'python',
	'azurecli': 'azurecli',
};

/** Shorthand step keys (alternative to `task:`) that execute a script. */
const SHORTHAND_KIND_MAP: Record<string, ScriptKind> = {
	'script': 'cmd',          // ADO maps `script:` to cmd on Windows / bash elsewhere
	'bash': 'bash',
	'pwsh': 'powershell',
	'powershell': 'powershell',
};

const TASK_NAME_RE = /^([A-Za-z][A-Za-z0-9]*)@\d+$/;

/**
 * Loads YAML for pipelines or individual templates and walks the AST to extract
 * referenced templates and PowerShell-style tasks. Same-repo templates can be
 * recursed into via {@link analyzeFile}.
 */
export class PipelineYamlAnalyzer {
	constructor(
		private readonly client: AdoClient,
		private readonly logger: LoggingService,
	) { }

	/**
	 * Analyse an arbitrary YAML file inside a TfsGit repository. Used to recurse
	 * into templates referenced from the root pipeline (or another template).
	 */
	async analyzeFile(
		organizationName: string,
		projectName: string,
		repositoryId: string,
		filePath: string,
		branch?: string,
	): Promise<PipelineAnalysis> {
		let yamlText: string | undefined;
		try {
			yamlText = await this.client.getFileContent(organizationName, projectName, repositoryId, filePath, branch);
		} catch (err) {
			this.logger.logError(`Failed to fetch YAML ${filePath}`, err);
			return { templates: [], scripts: [], rootPath: filePath, warning: vscode.l10n.t('Could not fetch YAML file.') };
		}
		if (!yamlText) {
			return { templates: [], scripts: [], rootPath: filePath, warning: vscode.l10n.t('YAML file not found in the repository.') };
		}
		try {
			const lineCounter = new LineCounter();
			const doc = parseDocument(yamlText, { keepSourceTokens: false, lineCounter });
			const templates: TemplateRef[] = [];
			const scripts: ScriptRef[] = [];
			walkAst(doc, lineCounter, templates, scripts);
			return { templates: dedupeTemplates(templates), scripts: dedupeScripts(scripts), rootPath: filePath };
		} catch (err) {
			this.logger.logError(`Failed to parse YAML ${filePath}`, err);
			return { templates: [], scripts: [], rootPath: filePath, warning: vscode.l10n.t('YAML parse error.') };
		}
	}

	async analyze(
		organizationName: string,
		projectName: string,
		pipelineId: number,
		preloadedDetail?: AdoPipelineDetail,
		branch?: string,
	): Promise<PipelineAnalysis> {
		let detail: AdoPipelineDetail;
		if (preloadedDetail) {
			detail = preloadedDetail;
		} else {
			try {
				detail = await this.client.getPipeline(organizationName, projectName, pipelineId);
			} catch (err) {
				this.logger.logError(`Failed to fetch pipeline ${pipelineId} definition`, err);
				return { templates: [], scripts: [], warning: vscode.l10n.t('Could not fetch pipeline definition.') };
			}
		}

		const cfg = detail.configuration;
		if (!cfg || (cfg.type && cfg.type.toLowerCase() !== 'yaml')) {
			return { templates: [], scripts: [], warning: vscode.l10n.t('Pipeline is not YAML-based.') };
		}
		const repoId = cfg.repository?.id;
		const yamlPath = cfg.path;
		if (!repoId || !yamlPath) {
			return { templates: [], scripts: [], warning: vscode.l10n.t('Pipeline definition has no repository / path.') };
		}
		if (cfg.repository?.type && cfg.repository.type.toLowerCase() !== 'azurereposgit') {
			return {
				templates: [], scripts: [], rootPath: yamlPath,
				warning: `Repository type "${cfg.repository.type}" is not supported yet.`,
			};
		}

		return this.analyzeFile(organizationName, projectName, repoId, yamlPath, branch);
	}
}

/**
 * Walk the YAML AST (rather than the JS object) so we can capture source
 * line numbers for every script-style task. The `visit` traversal naturally
 * descends into `extends:` so its inner `template:` pair is picked up by
 * the same Map handler. In addition to the canonical `- task: Foo@N` form
 * we also recognise the shorthand step keys (`script`, `bash`, `pwsh`,
 * `powershell`) that ADO accepts as an alternative.
 */
function walkAst(
	doc: ReturnType<typeof parseDocument>,
	lineCounter: LineCounter,
	templates: TemplateRef[],
	scripts: ScriptRef[],
): void {
	visit(doc, {
		Map(_key, node) {
			const tplVal = node.get('template');
			if (typeof tplVal === 'string') {
				templates.push(parseTemplateRef(tplVal));
			}

			const taskVal = node.get('task');
			if (typeof taskVal === 'string') {
				const taskKind = kindForTask(taskVal);
				if (taskKind !== undefined) {
					const inputsNode = node.get('inputs');
					const inputsJs = isMap(inputsNode)
						? (inputsNode as YAMLMap).toJSON()
						: inputsNode;
					const line = lineOf(node, lineCounter);
					scripts.push(parseTaskRef(taskVal, taskKind, inputsJs, line));
					return;
				}
			}

			// Shorthand step keys: a step is a map whose first/only meaningful key is
			// `script` / `bash` / `pwsh` / `powershell`. Treat them as inline scripts.
			const shorthand = findShorthandKey(node);
			if (shorthand) {
				const line = lineOf(node, lineCounter);
				scripts.push({
					task: shorthand.key,
					inline: true,
					line,
					kind: SHORTHAND_KIND_MAP[shorthand.key],
				});
			}
		},
	});
}

function lineOf(node: { range?: [number, number, number] | null }, lineCounter: LineCounter): number | undefined {
	const range = node.range;
	return range ? lineCounter.linePos(range[0]).line : undefined;
}

/**
 * Find the shorthand script key (if any) on a step mapping. The key must be
 * one of {@link SHORTHAND_KIND_MAP}; we only treat it as a script step if a
 * `task:` key is *not* present (those are handled separately above).
 */
function findShorthandKey(node: YAMLMap): { key: keyof typeof SHORTHAND_KIND_MAP } | undefined {
	let found: keyof typeof SHORTHAND_KIND_MAP | undefined;
	for (const item of node.items) {
		const k = (item.key as { value?: unknown } | null)?.value;
		if (typeof k !== 'string') { continue; }
		if (k === 'task' || k === 'template') { return undefined; }
		const lower = k.toLowerCase();
		if (lower in SHORTHAND_KIND_MAP && found === undefined) {
			found = lower as keyof typeof SHORTHAND_KIND_MAP;
		}
	}
	return found ? { key: found } : undefined;
}

/**
 * Resolve the {@link ScriptKind} for a fully-qualified task identifier
 * (e.g. `PowerShell@2`). Returns `undefined` if the task is not a known
 * script-running task.
 */
function kindForTask(task: string): ScriptKind | undefined {
	const m = TASK_NAME_RE.exec(task);
	if (!m) { return undefined; }
	const lower = m[1].toLowerCase();
	return TASK_KIND_MAP[lower];
}

function parseTemplateRef(raw: string): TemplateRef {
	const at = raw.lastIndexOf('@');
	if (at === -1) {
		return { raw, path: raw };
	}
	return { raw, path: raw.slice(0, at), repository: raw.slice(at + 1) };
}

function parseTaskRef(task: string, defaultKind: ScriptKind, inputs: unknown, line?: number): ScriptRef {
	if (!inputs || typeof inputs !== 'object') {
		return { task, inline: true, line, kind: defaultKind };
	}
	const i = inputs as Record<string, unknown>;
	// Normalise input keys (YAML is case-insensitive in practice for ADO tasks).
	const lower: Record<string, unknown> = {};
	for (const k of Object.keys(i)) {
		lower[k.toLowerCase()] = i[k];
	}

	// File-path style inputs across the supported tasks:
	//   PowerShell@2 / Bash@3 / PythonScript@0 → filePath / scriptPath
	//   AzurePowerShell@5 → ScriptPath
	//   ShellScript@2 / AzureCLI@2 → scriptPath
	//   BatchScript@1 → filename
	//   PowerShellOnTargetMachines@3 → ScriptPath
	const filePath =
		(typeof lower['filepath'] === 'string' && (lower['filepath'] as string)) ||
		(typeof lower['scriptpath'] === 'string' && (lower['scriptpath'] as string)) ||
		(typeof lower['filename'] === 'string' && (lower['filename'] as string)) ||
		undefined;

	// Refine kind for AzureCLI based on its `scriptType` (ps / pscore / bash / batch).
	let kind = defaultKind;
	if (defaultKind === 'azurecli') {
		const st = String(lower['scripttype'] ?? '').toLowerCase();
		if (st === 'ps' || st === 'pscore') { kind = 'powershell'; }
		else if (st === 'bash') { kind = 'bash'; }
		else if (st === 'batch') { kind = 'cmd'; }
		// otherwise keep the generic `azurecli` icon.
	}

	if (filePath) {
		return { task, filePath, inline: false, line, kind };
	}
	const targetType = String(
		lower['targettype'] ?? lower['scripttype'] ?? lower['scriptlocation'] ?? lower['scriptsource'] ?? '',
	).toLowerCase();
	const isInline =
		targetType === 'inline' ||
		targetType === 'inlinescript' ||
		!!lower['script'] ||
		!!lower['inline'] ||
		!!lower['inlinescript'];
	return { task, inline: isInline, line, kind };
}

function dedupeTemplates(items: TemplateRef[]): TemplateRef[] {
	const seen = new Set<string>();
	return items.filter(t => {
		if (seen.has(t.raw)) {
			return false;
		}
		seen.add(t.raw);
		return true;
	});
}

function dedupeScripts(items: ScriptRef[]): ScriptRef[] {
	const seen = new Set<string>();
	return items.filter(s => {
		const key = `${s.task}|${s.kind}|${s.filePath ?? (s.inline ? `<inline:${s.line ?? '?'}>` : '<unknown>')}`;
		if (seen.has(key)) {
			return false;
		}
		seen.add(key);
		return true;
	});
}

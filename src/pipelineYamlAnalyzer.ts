import { isMap, LineCounter, parseDocument, visit, YAMLMap } from 'yaml';
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

export interface PowerShellRef {
	/** Task identifier, e.g. `PowerShell@2`. */
	task: string;
	/** Path of the .ps1 file when the task references a script file. */
	filePath?: string;
	/** True if the task uses an inline script (no external file). */
	inline: boolean;
	/** 1-based line number of the task in the source YAML (when known). */
	line?: number;
}

export interface PipelineAnalysis {
	templates: TemplateRef[];
	scripts: PowerShellRef[];
	/** Path of the root pipeline YAML, useful for tooltip. */
	rootPath?: string;
	/** Non-fatal warning produced while loading or parsing the YAML. */
	warning?: string;
}

const POWERSHELL_TASK_RE = /^(PowerShell|AzurePowerShell|Powershell|AzureCLI)@\d+$/i;

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
			return { templates: [], scripts: [], rootPath: filePath, warning: 'Could not fetch YAML file.' };
		}
		if (!yamlText) {
			return { templates: [], scripts: [], rootPath: filePath, warning: 'YAML file not found in the repository.' };
		}
		try {
			const lineCounter = new LineCounter();
			const doc = parseDocument(yamlText, { keepSourceTokens: false, lineCounter });
			const templates: TemplateRef[] = [];
			const scripts: PowerShellRef[] = [];
			walkAst(doc, lineCounter, templates, scripts);
			return { templates: dedupeTemplates(templates), scripts: dedupeScripts(scripts), rootPath: filePath };
		} catch (err) {
			this.logger.logError(`Failed to parse YAML ${filePath}`, err);
			return { templates: [], scripts: [], rootPath: filePath, warning: 'YAML parse error.' };
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
				return { templates: [], scripts: [], warning: 'Could not fetch pipeline definition.' };
			}
		}

		const cfg = detail.configuration;
		if (!cfg || (cfg.type && cfg.type.toLowerCase() !== 'yaml')) {
			return { templates: [], scripts: [], warning: 'Pipeline is not YAML-based.' };
		}
		const repoId = cfg.repository?.id;
		const yamlPath = cfg.path;
		if (!repoId || !yamlPath) {
			return { templates: [], scripts: [], warning: 'Pipeline definition has no repository / path.' };
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
 * line numbers for every PowerShell-style task. The `visit` traversal
 * naturally descends into `extends:` so its inner `template:` pair is
 * picked up by the same Map handler.
 */
function walkAst(
	doc: ReturnType<typeof parseDocument>,
	lineCounter: LineCounter,
	templates: TemplateRef[],
	scripts: PowerShellRef[],
): void {
	visit(doc, {
		Map(_key, node) {
			const tplVal = node.get('template');
			if (typeof tplVal === 'string') {
				templates.push(parseTemplateRef(tplVal));
			}

			const taskVal = node.get('task');
			if (typeof taskVal === 'string' && POWERSHELL_TASK_RE.test(taskVal)) {
				const inputsNode = node.get('inputs');
				const inputsJs = isMap(inputsNode)
					? (inputsNode as YAMLMap).toJSON()
					: inputsNode;
				const range = node.range;
				const line = range ? lineCounter.linePos(range[0]).line : undefined;
				scripts.push(parseTaskRef(taskVal, inputsJs, line));
			}
		},
	});
}

function parseTemplateRef(raw: string): TemplateRef {
	const at = raw.lastIndexOf('@');
	if (at === -1) {
		return { raw, path: raw };
	}
	return { raw, path: raw.slice(0, at), repository: raw.slice(at + 1) };
}

function parseTaskRef(task: string, inputs: unknown, line?: number): PowerShellRef {
	if (!inputs || typeof inputs !== 'object') {
		return { task, inline: true, line };
	}
	const i = inputs as Record<string, unknown>;
	// Normalise input keys (YAML is case-insensitive in practice for ADO tasks).
	const lower: Record<string, unknown> = {};
	for (const k of Object.keys(i)) {
		lower[k.toLowerCase()] = i[k];
	}

	// PowerShell@2: targetType: filePath / inline; filePath: ...
	// AzurePowerShell@5: ScriptType: FilePath / InlineScript; ScriptPath: ...
	// AzureCLI@2: scriptType: ps / pscore; scriptLocation: scriptPath; scriptPath: ...
	const targetType = String(lower['targettype'] ?? lower['scripttype'] ?? lower['scriptlocation'] ?? '').toLowerCase();
	const filePath =
		(typeof lower['filepath'] === 'string' && (lower['filepath'] as string)) ||
		(typeof lower['scriptpath'] === 'string' && (lower['scriptpath'] as string)) ||
		undefined;

	if (filePath) {
		return { task, filePath, inline: false, line };
	}
	const isInline =
		targetType === 'inline' ||
		targetType === 'inlinescript' ||
		!!lower['script'] ||
		!!lower['inline'] ||
		!!lower['inlinescript'];
	return { task, inline: isInline, line };
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

function dedupeScripts(items: PowerShellRef[]): PowerShellRef[] {
	const seen = new Set<string>();
	return items.filter(s => {
		const key = `${s.task}|${s.filePath ?? (s.inline ? `<inline:${s.line ?? '?'}>` : '<unknown>')}`;
		if (seen.has(key)) {
			return false;
		}
		seen.add(key);
		return true;
	});
}

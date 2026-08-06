import * as assert from 'assert';

// You can import and use all API from the 'vscode' module
// as well as import your extension to test it
import * as vscode from 'vscode';
import {
	matchesFilterTerm,
	normalizeFilterTerm,
	pipelineMatchesFilter,
	PipelineNode,
} from '../pipelinesTreeProvider';
import type {
	AdoOrganization,
	AdoPipeline,
	AdoPipelineDetail,
	AdoProject,
} from '../adoClient';

suite('Extension Test Suite', () => {
	vscode.window.showInformationMessage('Start all tests.');

	test('Sample test', () => {
		assert.strictEqual(-1, [1, 2, 3].indexOf(5));
		assert.strictEqual(-1, [1, 2, 3].indexOf(0));
	});
});

suite('Filter helpers', () => {
	test('normalizeFilterTerm collapses empty and lowercases', () => {
		assert.strictEqual(normalizeFilterTerm(undefined), undefined);
		assert.strictEqual(normalizeFilterTerm(''), undefined);
		assert.strictEqual(normalizeFilterTerm('   '), undefined);
		assert.strictEqual(normalizeFilterTerm('Foo'), 'foo');
		assert.strictEqual(normalizeFilterTerm('  Build-CI '), 'build-ci');
	});

	test('matchesFilterTerm is case-insensitive substring match', () => {
		assert.strictEqual(matchesFilterTerm('Nightly Build', 'night'), true);
		assert.strictEqual(matchesFilterTerm('nightly build', 'BUILD'.toLowerCase()), true);
		assert.strictEqual(matchesFilterTerm('nightly', 'weekly'), false);
		assert.strictEqual(matchesFilterTerm(undefined, 'foo'), false);
		assert.strictEqual(matchesFilterTerm('foo', undefined), false);
		assert.strictEqual(matchesFilterTerm(undefined, undefined), false);
	});

	suite('pipelineMatchesFilter', () => {
		const org: AdoOrganization = { accountId: 'a', accountName: 'org', accountUri: 'https://dev.azure.com/org' };
		const project: AdoProject = { id: 'p1', name: 'proj', url: '', state: 'wellFormed' };

		function makeNode(pipelineName: string, path?: string): PipelineNode {
			const pipeline: AdoPipeline = { id: 1, name: pipelineName, folder: '\\', url: '' };
			const detail: AdoPipelineDetail | undefined = path
				? { ...pipeline, configuration: { type: 'yaml', path } }
				: undefined;
			return new PipelineNode(org, project, pipeline, 'repo:x', 'x', detail);
		}

		test('matches on pipeline name', () => {
			const node = makeNode('Nightly-Build');
			assert.strictEqual(pipelineMatchesFilter(node, 'night'), true);
			assert.strictEqual(pipelineMatchesFilter(node, 'weekly'), false);
		});

		test('matches on YAML basename when name does not match', () => {
			const node = makeNode('CI', '/pipelines/deploy-prod.yml');
			assert.strictEqual(pipelineMatchesFilter(node, 'deploy'), true);
			assert.strictEqual(pipelineMatchesFilter(node, 'prod.yml'), true);
			// Directory segments must not match the basename check.
			assert.strictEqual(pipelineMatchesFilter(node, 'pipelines'), false);
		});

		test('does not match when neither name nor basename contains term', () => {
			const node = makeNode('CI', '/pipelines/deploy-prod.yml');
			assert.strictEqual(pipelineMatchesFilter(node, 'weekly'), false);
		});

		test('handles missing YAML path gracefully', () => {
			const node = makeNode('CI');
			assert.strictEqual(pipelineMatchesFilter(node, 'ci'), true);
			assert.strictEqual(pipelineMatchesFilter(node, 'deploy'), false);
		});
	});
});

// -------------------------------------------------------------
// PatCredentialStore — plan 002 phase A, per-org PAT storage.
// -------------------------------------------------------------

import {
	GLOBAL_SECRET_KEY,
	PER_ORG_HISTORY_KEY,
	PER_ORG_HISTORY_LIMIT,
	PER_ORG_INDEX_KEY,
	PER_ORG_SECRET_PREFIX,
	PatCredentialStore,
	canonicalizeOrg,
} from '../patCredentialStore';
import { parseOrgFromUrl } from '../authService';

class FakeMemento implements vscode.Memento {
	private readonly map = new Map<string, unknown>();
	keys(): readonly string[] { return [...this.map.keys()]; }
	get<T>(key: string): T | undefined;
	get<T>(key: string, defaultValue: T): T;
	get(key: string, defaultValue?: unknown): unknown {
		return this.map.has(key) ? this.map.get(key) : defaultValue;
	}
	async update(key: string, value: unknown): Promise<void> {
		if (value === undefined) {
			this.map.delete(key);
		} else {
			this.map.set(key, value);
		}
	}
	setKeysForSync(_keys: readonly string[]): void { /* no-op */ }
}

class FakeSecretStorage implements vscode.SecretStorage {
	readonly map = new Map<string, string>();
	private readonly emitter = new vscode.EventEmitter<vscode.SecretStorageChangeEvent>();
	readonly onDidChange = this.emitter.event;

	async get(key: string): Promise<string | undefined> {
		return this.map.get(key);
	}
	async store(key: string, value: string): Promise<void> {
		this.map.set(key, value);
		this.emitter.fire({ key });
	}
	async delete(key: string): Promise<void> {
		this.map.delete(key);
		this.emitter.fire({ key });
	}
	async keys(): Promise<string[]> {
		return [...this.map.keys()];
	}
}

suite('PatCredentialStore', () => {
	test('canonicalizeOrg trims and lowercases', () => {
		assert.strictEqual(canonicalizeOrg(' Contoso '), 'contoso');
		assert.strictEqual(canonicalizeOrg('DBTEK'), 'dbtek');
		assert.strictEqual(canonicalizeOrg('already-lower'), 'already-lower');
	});

	test('savePerOrgPat writes to SecretStorage and updates the index', async () => {
		const secrets = new FakeSecretStorage();
		const memento = new FakeMemento();
		const store = new PatCredentialStore(secrets, memento);

		await store.savePerOrgPat('Contoso', 'pat-1');

		assert.strictEqual(secrets.map.get(PER_ORG_SECRET_PREFIX + 'contoso'), 'pat-1');
		assert.deepStrictEqual(memento.get<string[]>(PER_ORG_INDEX_KEY), ['contoso']);
	});

	test('savePerOrgPat is idempotent on the index but overwrites the value', async () => {
		const secrets = new FakeSecretStorage();
		const memento = new FakeMemento();
		const store = new PatCredentialStore(secrets, memento);

		await store.savePerOrgPat('contoso', 'pat-1');
		await store.savePerOrgPat('contoso', 'pat-2');

		assert.strictEqual(secrets.map.get(PER_ORG_SECRET_PREFIX + 'contoso'), 'pat-2');
		assert.deepStrictEqual(memento.get<string[]>(PER_ORG_INDEX_KEY), ['contoso']);
	});

	test('listPerOrgPats returns every stored entry sorted by org name', async () => {
		const store = new PatCredentialStore(new FakeSecretStorage(), new FakeMemento());
		await store.savePerOrgPat('fabrikam', 'pat-f');
		await store.savePerOrgPat('contoso', 'pat-c');
		await store.savePerOrgPat('adventureworks', 'pat-a');

		const entries = await store.listPerOrgPats();
		assert.deepStrictEqual(
			entries.map(e => e.org),
			['adventureworks', 'contoso', 'fabrikam'],
		);
		assert.deepStrictEqual(entries.find(e => e.org === 'contoso')?.pat, 'pat-c');
	});

	test('listPerOrgPats drops stale index entries whose secret is missing', async () => {
		const secrets = new FakeSecretStorage();
		const memento = new FakeMemento();
		const store = new PatCredentialStore(secrets, memento);

		await store.savePerOrgPat('contoso', 'pat-c');
		await store.savePerOrgPat('fabrikam', 'pat-f');
		// Simulate an out-of-band delete (e.g. user cleared credential store).
		secrets.map.delete(PER_ORG_SECRET_PREFIX + 'contoso');

		const entries = await store.listPerOrgPats();
		assert.deepStrictEqual(entries.map(e => e.org), ['fabrikam']);
		assert.deepStrictEqual(memento.get<string[]>(PER_ORG_INDEX_KEY), ['fabrikam']);
	});

	test('deletePerOrgPat removes only the requested org', async () => {
		const secrets = new FakeSecretStorage();
		const memento = new FakeMemento();
		const store = new PatCredentialStore(secrets, memento);

		await store.savePerOrgPat('contoso', 'pat-c');
		await store.savePerOrgPat('fabrikam', 'pat-f');
		await store.deletePerOrgPat('contoso');

		assert.strictEqual(secrets.map.has(PER_ORG_SECRET_PREFIX + 'contoso'), false);
		assert.strictEqual(secrets.map.get(PER_ORG_SECRET_PREFIX + 'fabrikam'), 'pat-f');
		assert.deepStrictEqual(memento.get<string[]>(PER_ORG_INDEX_KEY), ['fabrikam']);
	});

	test('clearAll wipes the global slot and every per-org slot', async () => {
		const secrets = new FakeSecretStorage();
		const memento = new FakeMemento();
		const store = new PatCredentialStore(secrets, memento);

		await store.setGlobalPat('global-pat');
		await store.savePerOrgPat('contoso', 'pat-c');
		await store.savePerOrgPat('fabrikam', 'pat-f');

		await store.clearAll();

		assert.strictEqual(secrets.map.size, 0);
		assert.strictEqual(memento.get(PER_ORG_INDEX_KEY), undefined);
	});

	test('global slot uses the historical AzureDevOpsPAT key', async () => {
		const secrets = new FakeSecretStorage();
		const store = new PatCredentialStore(secrets, new FakeMemento());
		await store.setGlobalPat('legacy-value');
		assert.strictEqual(secrets.map.get(GLOBAL_SECRET_KEY), 'legacy-value');
		assert.strictEqual(GLOBAL_SECRET_KEY, 'AzureDevOpsPAT');
	});

	// ----- history (plan 002 phase B.1) -----

	test('savePerOrgPat records the org in history, most-recent first', async () => {
		const memento = new FakeMemento();
		const store = new PatCredentialStore(new FakeSecretStorage(), memento);

		await store.savePerOrgPat('contoso', 'pat-c');
		await store.savePerOrgPat('fabrikam', 'pat-f');
		await store.savePerOrgPat('adventureworks', 'pat-a');

		assert.deepStrictEqual(store.getHistory(), ['adventureworks', 'fabrikam', 'contoso']);
		assert.deepStrictEqual(memento.get<string[]>(PER_ORG_HISTORY_KEY), ['adventureworks', 'fabrikam', 'contoso']);
	});

	test('history dedupes repeated saves and promotes the latest', async () => {
		const store = new PatCredentialStore(new FakeSecretStorage(), new FakeMemento());
		await store.savePerOrgPat('contoso', 'pat-1');
		await store.savePerOrgPat('fabrikam', 'pat-2');
		await store.savePerOrgPat('contoso', 'pat-3'); // re-add — should move to head
		assert.deepStrictEqual(store.getHistory(), ['contoso', 'fabrikam']);
	});

	test('history caps at PER_ORG_HISTORY_LIMIT entries', async () => {
		const store = new PatCredentialStore(new FakeSecretStorage(), new FakeMemento());
		for (let i = 0; i < PER_ORG_HISTORY_LIMIT + 5; i++) {
			await store.savePerOrgPat(`org-${i}`, `pat-${i}`);
		}
		const history = store.getHistory();
		assert.strictEqual(history.length, PER_ORG_HISTORY_LIMIT);
		// Most-recent first: last added is `org-24` when limit is 20.
		assert.strictEqual(history[0], `org-${PER_ORG_HISTORY_LIMIT + 4}`);
	});

	test('clearAllPerOrgPats (used by signOut) keeps the history', async () => {
		const store = new PatCredentialStore(new FakeSecretStorage(), new FakeMemento());
		await store.savePerOrgPat('contoso', 'pat-c');
		await store.savePerOrgPat('fabrikam', 'pat-f');

		await store.clearAllPerOrgPats();

		assert.deepStrictEqual(store.getHistory(), ['fabrikam', 'contoso']);
	});

	test('clearAll (used by Reset) also wipes the history', async () => {
		const store = new PatCredentialStore(new FakeSecretStorage(), new FakeMemento());
		await store.savePerOrgPat('contoso', 'pat-c');
		await store.clearAll();
		assert.deepStrictEqual(store.getHistory(), []);
	});
});

suite('parseOrgFromUrl', () => {
	test('extracts org from a dev.azure.com URL', () => {
		assert.strictEqual(parseOrgFromUrl('https://dev.azure.com/Contoso/'), 'contoso');
		assert.strictEqual(parseOrgFromUrl('https://dev.azure.com/contoso'), 'contoso');
		assert.strictEqual(parseOrgFromUrl('http://dev.azure.com/CONTOSO/foo/bar'), 'contoso');
		assert.strictEqual(parseOrgFromUrl('https://dev.azure.com/contoso/proj/_build?definitionId=42'), 'contoso');
	});

	test('extracts org from a legacy visualstudio.com URL', () => {
		assert.strictEqual(parseOrgFromUrl('https://Contoso.visualstudio.com/'), 'contoso');
		assert.strictEqual(parseOrgFromUrl('https://fabrikam-inc.visualstudio.com/DefaultCollection/'), 'fabrikam-inc');
	});

	test('trims surrounding whitespace', () => {
		assert.strictEqual(parseOrgFromUrl('   https://dev.azure.com/contoso\n\n'), 'contoso');
	});

	test('returns undefined on unrelated content', () => {
		assert.strictEqual(parseOrgFromUrl(undefined), undefined);
		assert.strictEqual(parseOrgFromUrl(''), undefined);
		assert.strictEqual(parseOrgFromUrl('not a url'), undefined);
		assert.strictEqual(parseOrgFromUrl('https://example.com/contoso'), undefined);
		assert.strictEqual(parseOrgFromUrl('ftp://dev.azure.com/contoso'), undefined);
	});

	test('rejects excessive input to avoid regex catastrophic paths', () => {
		const huge = 'https://dev.azure.com/' + 'a'.repeat(3000);
		assert.strictEqual(parseOrgFromUrl(huge), undefined);
	});
});

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

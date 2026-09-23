import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { runInNewContext } from 'node:vm';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import { senderOrigin } from '../src/policy.ts';

const buildUrl = new URL('../build.mjs', import.meta.url);
const buildSource = (await readFile(buildUrl, 'utf8')).replace(/^import .*;\r?\n/gm, '').replaceAll('import.meta.url', JSON.stringify(buildUrl.href));
const manifestSource = await readFile(new URL('../manifest.json', import.meta.url), 'utf8');
const production = 'https://cautious-memory-r38ze9j.pages.github.io';
const development = ['http://localhost:8080', 'http://127.0.0.1:8080', 'http://localhost:8081', 'http://127.0.0.1:8081'];

async function generated(dev) {
  const written = new Map();
  // Exercise package policy with compilation, file copies, and filesystem writes stubbed out.
  await runInNewContext(`(async () => { ${buildSource}\n})()`, {
    readFile: async file => { assert.equal(path.basename(file), 'manifest.json'); return manifestSource; },
    mkdir: async () => {}, cp: async () => {}, readdir: async () => [],
    writeFile: async (file, content) => { written.set(path.basename(file), content); },
    spawnSync: () => ({ status: 0 }), createRequire: () => ({ resolve: () => 'typescript-test-stub' }),
    fileURLToPath, path,
    process: { argv: ['node', fileURLToPath(buildUrl), ...(dev ? ['--dev'] : [])], execPath: 'node-test-stub', exit: () => assert.fail('Unexpected process exit') },
    console: { log() {} },
  });
  const environment = written.get('environment.js');
  assert.ok(environment.startsWith('export const allowedOrigins = '));
  const origins = JSON.parse(environment.slice('export const allowedOrigins = '.length).trim().replace(/;$/, ''));
  return { origins, manifest: JSON.parse(written.get('manifest.json')) };
}

test('development package permits exactly the two local testing ports while production excludes them', async () => {
  const prod = await generated(false);
  const dev = await generated(true);
  assert.deepEqual(prod.origins, [production]);
  assert.deepEqual(prod.manifest.externally_connectable.matches, [production + '/*']);
  assert.deepEqual(dev.origins, [production, ...development]);
  assert.deepEqual(dev.manifest.externally_connectable.matches, [production + '/*', 'http://localhost/*', 'http://127.0.0.1/*']);
  for (const origin of development) {
    assert.equal(senderOrigin(origin + '/actions', dev.origins), origin);
    assert.throws(() => senderOrigin(origin + '/actions', prod.origins), { code: 'FORBIDDEN_ORIGIN' });
  }
  for (const origin of ['http://localhost:8082', 'http://127.0.0.1:8082', 'http://localhost:3000', 'http://localhost', 'https://localhost:8081', 'http://localhost.evil.test:8081', 'http://127.0.0.2:8081']) assert.throws(() => senderOrigin(origin, dev.origins), { code: 'FORBIDDEN_ORIGIN' });
});

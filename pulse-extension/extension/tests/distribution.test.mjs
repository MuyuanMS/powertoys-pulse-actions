import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFile } from 'node:fs/promises';

test('unpacked browser identity matches default Native Host registration', async () => {
  const manifest = JSON.parse(await readFile(new URL('../manifest.json', import.meta.url), 'utf8'));
  const digest = createHash('sha256').update(Buffer.from(manifest.key, 'base64')).digest('hex').slice(0, 32);
  const id = [...digest].map(nibble => String.fromCharCode(97 + parseInt(nibble, 16))).join('');
  assert.equal(id, 'nlpkbkhlnocknpgkjnmhpahapffdhblo');
  const installer = await readFile(new URL('../../installer/Install.ps1', import.meta.url), 'utf8');
  assert.ok(installer.includes(`$ExtensionIds = @('${id}')`));
});

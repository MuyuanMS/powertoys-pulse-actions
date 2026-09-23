import { readFile, mkdir, cp, writeFile, readdir } from 'node:fs/promises';
import { spawnSync } from 'node:child_process';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const root = path.dirname(fileURLToPath(import.meta.url));
const dev = process.argv.includes('--dev');
if (process.argv.slice(2).some(arg => arg !== '--dev')) throw new Error('Only --dev is supported.');
const outDir = path.join(root, dev ? 'dist-dev' : 'dist');
const require = createRequire(import.meta.url);
const compile = spawnSync(process.execPath, [require.resolve('typescript/bin/tsc'), '-p', root, '--outDir', outDir], { stdio: 'inherit' });
if (compile.status !== 0) process.exit(compile.status ?? 1);
await mkdir(outDir, { recursive: true });
for (const name of await readdir(path.join(root, 'public'))) {
  await cp(path.join(root, 'public', name), path.join(outDir, name), { recursive: true });
}
const manifest = JSON.parse(await readFile(path.join(root, 'manifest.json'), 'utf8'));
const origins = ['https://cautious-memory-r38ze9j.pages.github.io'];
if (dev) {
  manifest.name += ' (Development)';
  manifest.externally_connectable.matches.push('http://localhost/*', 'http://127.0.0.1/*');
  origins.push('http://localhost:8080', 'http://127.0.0.1:8080', 'http://localhost:8081', 'http://127.0.0.1:8081');
}
await writeFile(path.join(outDir, 'manifest.json'), JSON.stringify(manifest, null, 2) + '\n');
await writeFile(path.join(outDir, 'environment.js'), `export const allowedOrigins = ${JSON.stringify(origins)};\n`);
console.log(`Extension package written to ${outDir}. Register its browser-specific extension ID with the Host installer.`);

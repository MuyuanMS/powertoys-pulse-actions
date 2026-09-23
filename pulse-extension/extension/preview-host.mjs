import { spawn } from 'node:child_process';
import { existsSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.dirname(fileURLToPath(import.meta.url));
const executable = path.resolve(root, '../host/bin/Release/net10.0/Pulse.Host.exe');
const dataRoot = path.resolve(root, '../.tmp/ui-preview-host');
export const diagnosticMethods = new Set(['github.accounts', 'agents.test.start', 'agents.test.get', 'agents.test.cancel', 'prompts.list', 'prompts.get', 'prompts.sync']);
let child;
let bytes = Buffer.alloc(0);
const pending = new Map();
const tests = new Set();
let closing = false;
let closePromise;
const idleWaiters = [];
function signalIdle() { if (!pending.size) for (const resolve of idleWaiters.splice(0)) resolve(); }
const error = (code, message) => ({ id: 'preview', protocolVersion: 1, ok: false, error: { code, message, guidance: 'Compile the local Host for final validation, then restart the UI preview.' } });

function failAll(result) {
  for (const callback of pending.values()) callback(result);
  pending.clear(); bytes = Buffer.alloc(0);
  signalIdle();
}
function connect() {
  if (child) return;
  if (!existsSync(executable)) throw new Error('The local Host development executable is not available.');
  const process = spawn(executable, ['--stdio', '--data-root', dataRoot], { windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] });
  child = process;
  process.stderr.resume(); // Native diagnostics are not passed verbatim to a web page.
  process.stdin.on('error', () => {});
  process.on('error', () => { if (child === process) { child = undefined; failAll(error('PREVIEW_HOST_UNAVAILABLE', 'The local diagnostic Host could not start.')); } });
  process.on('exit', () => { if (child === process) { child = undefined; failAll(error('HOST_DISCONNECTED', 'The local diagnostic Host exited. Refresh the test status.')); } });
  process.stdout.on('data', chunk => {
    if (child !== process) return;
    bytes = Buffer.concat([bytes, chunk]);
    while (bytes.length >= 4) {
      const size = bytes.readUInt32LE();
      if (!size || size > 900 * 1024) { process.kill(); failAll(error('INVALID_FRAME', 'The diagnostic Host returned an invalid frame.')); return; }
      if (bytes.length < size + 4) return;
      let response;
      try { response = JSON.parse(bytes.subarray(4, size + 4).toString('utf8')); }
      catch { process.kill(); failAll(error('INVALID_FRAME', 'The diagnostic Host returned invalid JSON.')); return; }
      bytes = bytes.subarray(size + 4);
      const callback = pending.get(response.id); pending.delete(response.id);
      if (response.ok && response.data?.testId) {
        if (response.data.state === 'running') tests.add(response.data.testId); else tests.delete(response.data.testId);
      }
      callback?.(response);
      signalIdle();
    }
  });
}

export async function requestDiagnostic(type, payload, cleanup = false) {
  if (closing && !cleanup) return error('HOST_DISCONNECTED', 'The local preview is closing.');
  if (!diagnosticMethods.has(type)) return error('FORBIDDEN_METHOD', 'The preview only allows agent tests, gh account detection, and local prompt synchronization.');
  if (!cleanup && pending.size >= 8) return error('CLIENT_BUSY', 'Local requests are in progress. Try again shortly.');
  try { connect(); } catch (failure) { return error('PREVIEW_HOST_UNAVAILABLE', failure.message); }
  const id = crypto.randomUUID();
  const body = Buffer.from(JSON.stringify({ id, protocolVersion: 1, type, payload }));
  if (body.length > 4096) return error('INPUT_TOO_LARGE', 'The local request exceeds the input limit.');
  const header = Buffer.alloc(4); header.writeUInt32LE(body.length);
  return new Promise(resolve => {
    pending.set(id, resolve);
    child.stdin.write(Buffer.concat([header, body]), failure => {
      if (failure && pending.delete(id)) { resolve(error('HOST_DISCONNECTED', 'The request could not be sent to the local Host.')); signalIdle(); }
    });
  });
}

export function closeDiagnostics() {
  if (closePromise) return closePromise;
  closing = true;
  return closePromise = (async () => {
    if (pending.size) await new Promise(resolve => idleWaiters.push(resolve));
    await Promise.allSettled([...tests].map(testId => requestDiagnostic('agents.test.cancel', { testId }, true)));
    child?.stdin.end();
  })();
}

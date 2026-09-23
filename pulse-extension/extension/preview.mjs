import { createServer } from 'node:http';
import { readFile, access } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { requestDiagnostic, closeDiagnostics } from './preview-host.mjs';

// Serve source UI assets plus locally compiled JavaScript. No release package is created.
// Only explicit agent tests, gh login detection, and bundled prompt reads use the local Host.
const root = path.dirname(fileURLToPath(import.meta.url));
const port = Number(process.env.PULSE_PREVIEW_PORT || 4186);
if (!Number.isInteger(port) || port < 1024 || port > 65535) throw new Error('Invalid preview port.');
const javascriptRoot = path.resolve(root, '../.tmp/extension-ui');
await access(path.join(javascriptRoot, 'popup.js')).catch(() => {
  throw new Error('Compile preview JavaScript to .tmp/extension-ui during final validation before starting the preview.');
});
const types = { '.html': 'text/html; charset=utf-8', '.js': 'text/javascript; charset=utf-8', '.css': 'text/css; charset=utf-8', '.png': 'image/png', '.svg': 'image/svg+xml' };
const server = createServer(async (request, response) => {
  response.setHeader('Cache-Control', 'no-store');
  response.setHeader('X-Content-Type-Options', 'nosniff');
  response.setHeader('Referrer-Policy', 'no-referrer');
  if (![`127.0.0.1:${port}`, `localhost:${port}`].includes(request.headers.host)) {
    response.writeHead(403).end('Preview is available on localhost only.'); return;
  }
  const url = new URL(request.url, `http://127.0.0.1:${port}`);
  if (url.pathname === '/__pulse/diagnostics' && request.method === 'POST') {
    if (![`http://127.0.0.1:${port}`, `http://localhost:${port}`].includes(request.headers.origin) || !request.headers['content-type']?.startsWith('application/json')) {
      response.writeHead(403).end('Same-origin JSON requests only.'); return;
    }
    try {
      const chunks = []; let size = 0;
      for await (const chunk of request) { size += chunk.length; if (size > 4096) { response.writeHead(413).end(); return; } chunks.push(chunk); }
      const message = JSON.parse(Buffer.concat(chunks).toString('utf8'));
      const result = await requestDiagnostic(message.type, message.payload ?? {});
      response.writeHead(200, { 'Content-Type': 'application/json; charset=utf-8' }).end(JSON.stringify(result));
    } catch { response.writeHead(400).end('Invalid diagnostic request.'); }
    return;
  }
  if (request.method !== 'GET') { response.writeHead(405).end(); return; }
  if (url.pathname === '/') { response.writeHead(302, { Location: '/popup.html?expanded=1' }).end(); return; }
  if (url.pathname === '/favicon.ico') { response.writeHead(204).end(); return; }
  const bridge = url.pathname === '/__preview/bridge.js';
  const name = url.pathname.slice(1);
  if (!bridge && (!/^[a-zA-Z0-9_.-]+$/.test(name) || !types[path.extname(name)])) {
    response.writeHead(404).end('Not found'); return;
  }
  try {
    const sizingHarness = name === 'popup-sizing.html' || name === 'popup-sizing.js';
    let filename = bridge ? path.join(root, 'preview', 'bridge.js') : sizingHarness ? path.join(root, 'preview', name) : path.join(javascriptRoot, name);
    // Static source assets can be previewed while editing without running a build.
    if (!bridge && !sizingHarness && path.extname(name) !== '.js') {
      const source = path.join(root, 'public', name);
      try { await access(source); filename = source; } catch { /* Return the normal missing-asset response. */ }
    }
    let content = await readFile(filename);
    if (path.extname(filename) === '.html') {
      content = Buffer.from(content.toString('utf8').replace('<script type="module"', '<script src="/__preview/bridge.js"></script>\n<script type="module"'));
    }
    response.writeHead(200, { 'Content-Type': types[path.extname(filename)] }); response.end(content);
  } catch {
    response.writeHead(404, { 'Content-Type': 'text/plain; charset=utf-8' }).end('Preview asset is not ready.');
  }
}).listen(port, '127.0.0.1', () => {
  console.log(`Pulse extension UI preview: http://127.0.0.1:${port}/popup.html?expanded=1`);
  console.log('Task data is simulated. Agent tests, gh account detection, and bundled prompt reads use the local Host.');
});
for (const signal of ['SIGINT', 'SIGTERM']) process.on(signal, () => {
  server.close(); void closeDiagnostics().finally(() => process.exit(0));
});

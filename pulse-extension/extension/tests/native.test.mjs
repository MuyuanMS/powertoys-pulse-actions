import test from 'node:test';
import assert from 'node:assert/strict';
import { NativeClient } from '../src/native.ts';

test('disconnect rejects uncertain requests without changing tasks or resending mutations', async t => {
  const timers = [];
  t.mock.method(globalThis, 'setTimeout', callback => { timers.push(callback); return timers.length; });
  t.mock.method(globalThis, 'clearTimeout', () => {});
  const posted = [];
  let disconnect;
  let receive;
  let connections = 0;
  globalThis.chrome = { runtime: { connectNative: () => {
    connections++;
    return { onMessage: { addListener: listener => { receive = listener; } }, onDisconnect: { addListener: listener => { disconnect = listener; } }, postMessage: message => posted.push(message), disconnect() {} };
  } } };
  const client = new NativeClient();
  const request = client.request('operations.submit', { runId: 'run-1', operationId: 'op-1' });
  const rejected = assert.rejects(request, { code: 'HOST_DISCONNECTED' });
  disconnect(); await rejected;
  assert.equal(client.connection.state, 'disconnected');
  assert.equal(posted.length, 1);
  timers.at(-1)(); // The reconnect timer sends only hello.
  assert.equal(connections, 2);
  assert.deepEqual(posted.map(message => message.type), ['operations.submit', 'hello']);
  const hello = posted.at(-1);
  receive({ id: hello.id, protocolVersion: 1, ok: true, data: {} });
  await Promise.resolve();
  assert.equal(client.connection.state, 'connected');
});

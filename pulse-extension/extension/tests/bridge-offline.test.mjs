import test from 'node:test';
import assert from 'node:assert/strict';

test('extension discovery and settings remain usable when the native Host is missing', async t => {
  t.mock.method(globalThis, 'setTimeout', () => 1);
  t.mock.method(globalThis, 'clearTimeout', () => {});
  let externalListener;
  let optionsOpened = 0;
  let hostAttempts = 0;
  const origin = 'https://cautious-memory-r38ze9j.pages.github.io';
  globalThis.chrome = {
    runtime: {
      id: 'a'.repeat(32), getURL: path => `chrome-extension://${'a'.repeat(32)}/${path}`, getManifest: () => ({ version: '0.1.0' }),
      onMessageExternal: { addListener: listener => { externalListener = listener; } }, onMessage: { addListener() {} },
      onStartup: { addListener() {} }, onInstalled: { addListener() {} },
      connectNative: () => { hostAttempts++; throw new Error('Native host is not registered'); },
      openOptionsPage: async () => { optionsOpened++; },
    },
    storage: { local: { get: async () => ({}) } },
    action: { setBadgeText: async () => {}, setBadgeBackgroundColor: async () => {}, setTitle: async () => {} },
    alarms: { create: async () => {}, onAlarm: { addListener() {} } },
  };
  await import('../src/background.ts');
  const send = (type, payload = {}) => new Promise(resolve => externalListener({ protocolVersion: 1, type, payload }, { url: origin + '/', origin }, resolve));
  const attemptsBefore = hostAttempts;
  assert.equal((await send('bridge.hello')).data.extensionVersion, '0.1.0');
  assert.equal((await send('ui.openSettings')).data.opened, true);
  assert.equal(optionsOpened, 1); assert.equal(hostAttempts, attemptsBefore);
  const host = await send('actions.check', { actionKind: 'issue-fix' });
  assert.equal(host.ok, false); assert.equal(host.error.code, 'HOST_UNAVAILABLE');
});

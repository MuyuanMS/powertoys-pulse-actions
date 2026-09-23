import test from 'node:test';
import assert from 'node:assert/strict';
import { cliOptions, selectedInstallation } from '../src/cli-selection.ts';

const first = {
  path: 'C:\\Users\\test\\npm\\codex.exe', resolvedPath: 'C:\\Users\\test\\npm\\node_modules\\codex\\codex.exe',
  aliases: ['C:\\Users\\test\\npm\\codex.exe', 'C:\\Users\\test\\Links\\codex.exe'],
  available: true, version: '0.153.4', source: 'npm', sources: ['PATH', 'npm'],
};
const second = { path: 'D:\\Tools\\Codex\\codex.exe', available: true, version: '0.153.4', source: 'Custom location' };

test('CLI choices retain a blank selection and list every distinct installation without auto-selecting', () => {
  const options = cliOptions([first, second], '');
  assert.deepEqual(options[0], { value: '', label: 'Choose installation', disabled: false });
  assert.equal(selectedInstallation([first, second], ''), undefined);
  assert.deepEqual(options.slice(1).map(option => option.value), [first.resolvedPath, second.path]);
  assert.ok(options.slice(1).every(option => !option.disabled));
  assert.notEqual(options[1].label, options[2].label, 'same-version installations remain distinguishable by their complete paths');
  assert.ok(options[1].label.includes(first.resolvedPath));
  assert.ok(options[2].label.includes(second.path));
  assert.match(options[1].label, /0\.153\.4.*PATH, npm/);
  assert.match(options[2].label, /0\.153\.4.*Custom location/);
});

test('missing saved selection stays visible and disabled without falling back to the first detected installation', () => {
  const saved = 'E:\\Removed\\codex.exe';
  const options = cliOptions([first, second], saved);
  assert.equal(selectedInstallation([first, second], saved), undefined);
  assert.deepEqual(options.at(-1), { value: saved, label: `${saved} · Not detected`, disabled: true, missing: true });
  assert.equal(options.filter(option => option.value === saved).length, 1);
  assert.equal(cliOptions([], '')[0].value, '');
  assert.equal(cliOptions([], saved).at(-1).value, saved);
});

test('saved observed, canonical, and alias paths match Windows casing and separators while preserving their exact values', () => {
  for (const saved of [first.path.toUpperCase(), first.resolvedPath.toLowerCase(), first.aliases[1].replaceAll('\\', '/').toUpperCase()]) {
    assert.equal(selectedInstallation([first, second], saved), first);
    const options = cliOptions([first, second], saved);
    assert.equal(options.length, 3);
    assert.equal(options[1].value, saved, 'rendering settings does not silently canonicalize a saved path');
    assert.equal(options[1].missing, undefined);
    assert.equal(options[2].value, second.path);
  }
  const aliasCollision = { ...second, aliases: [first.path] };
  assert.equal(selectedInstallation([aliasCollision, first], first.path), first, 'an exact observed path wins over another candidate alias');
});

test('every detected installation remains selectable regardless of version or compatibility probe metadata', () => {
  const historical = { ...second, version: '0.145.0', available: false, capabilities: { model: false, reasoningEffort: false }, error: { code: 'CLI_INCOMPATIBLE', message: 'Required exec interface is missing.', guidance: 'Install a compatible release.' } };
  const current = { ...first, version: '0.154.0' };
  const desktop = { path: 'C:\\Program Files\\Codex Desktop\\codex.exe', version: '0.153.4', available: true, source: 'Desktop' };
  const saved = second.path.toUpperCase();
  const installations = [historical, current, desktop];
  const options = cliOptions(installations, saved);
  assert.equal(selectedInstallation(installations, saved), historical);
  assert.equal(options[1].value, saved);
  assert.equal(options[1].missing, undefined);
  assert.deepEqual(options.slice(1).map(option => option.disabled), [false, false, false]);
  assert.deepEqual(options.slice(1).map(option => option.label), [
    `0.145.0 · Custom location · ${historical.path}`,
    `0.154.0 · PATH, npm · ${current.resolvedPath}`,
    `0.153.4 · Desktop · ${desktop.path}`,
  ]);
  assert.doesNotMatch(JSON.stringify(options), /Unavailable|CLI_INCOMPATIBLE|Required exec|compatible release|capabilities/);
});

test('canonical duplicates collapse while distinct paths and selected aliases are preserved', () => {
  const alias = { ...first, path: first.aliases[1], resolvedPath: first.resolvedPath.toUpperCase() };
  const saved = alias.path;
  const options = cliOptions([first, alias, second], saved);
  assert.equal(options.length, 3);
  assert.equal(options[1].value, saved);
  assert.equal(options[2].value, second.path);
  assert.equal(selectedInstallation([first, alias, second], saved), alias);
});

test('unknown versions remain selectable and labels never dump probe details or compatibility errors', () => {
  const probe = {
    ...second, available: false, version: null, source: undefined,
    environment: { token: 'DO_NOT_DISPLAY_ENVIRONMENT' }, stdout: 'DO_NOT_DISPLAY_STDOUT',
    error: { code: 'CLI_UNAVAILABLE', message: 'Version could not be detected.', diagnostic: 'DO_NOT_DISPLAY_DIAGNOSTIC' },
  };
  const options = cliOptions([probe], probe.path);
  assert.equal(options[1].disabled, false);
  assert.equal(options[1].label, `Version unknown · Detected locally · ${probe.path}`);
  assert.doesNotMatch(JSON.stringify(options), /DO_NOT_DISPLAY|CLI_UNAVAILABLE|Version could not be detected|Unavailable/);
  assert.deepEqual(Object.keys(options[1]), ['value', 'label', 'disabled']);
  const stringError = cliOptions([{ ...probe, error: 'Cannot\nread executable.' }], '')[1];
  assert.equal(stringError.disabled, false);
  assert.equal(stringError.label, options[1].label);
});

const byId = id => document.getElementById(id);
const key = 'pulse-example-v1';
let saved;
try { saved = JSON.parse(localStorage.getItem(key) || 'null') || {}; } catch { saved = {}; }
let extensionId = saved.extensionId || '';
let acceptedRunId = saved.runId;
let pendingTask = saved.task;
let sequence = 0;
let polling = false;
let submitting = false;
byId('extension-id').value = extensionId;
function persist() { localStorage.setItem(key, JSON.stringify({ extensionId, runId: acceptedRunId, task: pendingTask })); }
function showError(error) { byId('error').textContent = error instanceof Error ? error.message : String(error); byId('error').hidden = false; }
function clearError() { byId('error').hidden = true; }
function send(type, payload = {}) {
  extensionId = byId('extension-id').value.trim();
  if (!/^[a-p]{32}$/.test(extensionId)) return Promise.reject(new Error('Enter the correct 32-character extension ID for this browser.'));
  if (!globalThis.chrome?.runtime?.sendMessage) return Promise.reject(new Error('The extension messaging API is unavailable. Install the extension and open this page from an allowed origin, or http://localhost:8080 / http://127.0.0.1:8080 with the development extension. You can still copy the prompt.'));
  persist();
  return new Promise((resolve, reject) => {
    const timeout = setTimeout(() => reject(new Error('The response is unconfirmed. Keep the original requestId and query the existing task or retry the same request.')), 65000);
    chrome.runtime.sendMessage(extensionId, { protocolVersion: 1, type, payload }, response => {
      clearTimeout(timeout);
      const error = chrome.runtime.lastError;
      if (error) return reject(new Error(`Cannot connect to the extension: ${error.message}. Check that the extension is installed, enabled, and allows this origin. You can still copy the prompt.`));
      if (!response) return reject(new Error('The extension did not return a response.'));
      if (!response.ok) return reject(new Error(`${response.error?.message || 'Request failed'} ${response.error?.guidance || ''} [${response.error?.code || 'UNKNOWN'}]`));
      resolve(response.data);
    });
  });
}
function createTask() {
  const targetType = byId('target-type').value;
  const task = { requestId: crypto.randomUUID(), actionId: byId('action-id').value.trim(), actionKind: byId('action-kind').value, repository: byId('repository').value.trim(), prompt: byId('prompt').value };
  if (targetType) task.target = { type: targetType, number: Number(byId('target-number').value) };
  const sha = byId('head-sha').value.trim(); if (sha) task.expectedHeadSha = sha;
  return task;
}
function reflect() {
  byId('submit').disabled = submitting || Boolean(acceptedRunId);
  byId('new-run').disabled = submitting;
  byId('submit').textContent = acceptedRunId ? 'Accepted · Following original run' : pendingTask ? 'Retry same request' : 'Run locally';
}
async function refresh() {
  if (!acceptedRunId || polling) return;
  polling = true;
  try {
    const run = await send('tasks.get', { runId: acceptedRunId });
    byId('status').textContent = JSON.stringify(run, null, 2);
    const events = await send('tasks.events', { runId: acceptedRunId, afterSequence: sequence, limit: 100 });
    for (const event of events.events) {
      if (event.sequence <= sequence) continue;
      byId('events').textContent += `[${event.time}] #${event.sequence} ${event.type}\n${event.text || ''}\n\n`;
      sequence = event.sequence;
    }
    sequence = Math.max(sequence, events.nextSequence);
    if (byId('events').textContent.length > 200000) byId('events').textContent = '[Showing recent activity; available records are retained by the Host]\n' + byId('events').textContent.slice(-180000);
    byId('message').textContent = `${acceptedRunId} · ${run.status.state}${events.truncated ? ' · Some Host events were truncated' : ''}. Open the extension task panel for next steps.`;
    clearError();
  } catch (error) { showError(error); }
  finally { polling = false; }
}
byId('detect').addEventListener('click', () => { void send('hello').then(data => { byId('capabilities').textContent = JSON.stringify(data, null, 2); clearError(); }).catch(showError); });
byId('task-form').addEventListener('submit', event => {
  event.preventDefault();
  if (submitting || acceptedRunId) return;
  pendingTask ??= createTask(); persist(); submitting = true; reflect();
  void send('tasks.submit', { task: pendingTask }).then(run => {
    acceptedRunId = run.runId; persist(); byId('status').textContent = JSON.stringify(run, null, 2); clearError(); void refresh();
  }).catch(showError).finally(() => { submitting = false; reflect(); });
});
byId('copy').addEventListener('click', () => { void navigator.clipboard.writeText(byId('prompt').value).then(() => { byId('message').textContent = 'Prompt copied.'; }).catch(showError); });
byId('refresh').addEventListener('click', () => { if (!acceptedRunId) showError(new Error('No known runId. If the first response is unconfirmed, retry the saved request.')); else void refresh(); });
byId('new-run').addEventListener('click', () => {
  if (submitting) return;
  acceptedRunId = undefined; pendingTask = undefined; sequence = 0; persist(); reflect();
  byId('status').textContent = 'The new run has not been submitted. The original run remains available in the extension.'; byId('events').textContent = ''; clearError();
});
if (pendingTask) {
  byId('repository').value = pendingTask.repository; byId('action-id').value = pendingTask.actionId; byId('action-kind').value = pendingTask.actionKind;
  byId('prompt').value = pendingTask.prompt; byId('target-type').value = pendingTask.target?.type || ''; byId('target-number').value = pendingTask.target?.number || ''; byId('head-sha').value = pendingTask.expectedHeadSha || '';
}
reflect();
if (acceptedRunId) void refresh();
setInterval(() => { void refresh(); }, 3000);

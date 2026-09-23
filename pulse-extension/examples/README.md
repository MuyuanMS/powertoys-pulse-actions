# Minimal integration page

This example does not modify the PowerToys Pulse website. Install and register the local Host first, then configure the default agent, PR and issue prompts, PowerToys main repository, and worktree root in extension Settings. Use the actual extension ID from the current Chrome or Edge installation.

After generating and loading `extension/dist-dev` for final acceptance, serve this directory from the repository root:

```powershell
python -m http.server 8080 --bind 127.0.0.1 --directory examples
```

Open `http://127.0.0.1:8080`. This server serves the development web page; it is not the Native Host. The production extension rejects localhost. The page can also be served from the allowed Pulse origin.

1. Enter the extension ID and select Detect extension and Host.
2. Save the agent, compatible prompts, and PowerToys folders in extension Settings.
3. Enter the action and target. PR review / PR E2E require the current full 40-character PR HEAD SHA.
4. Select Run locally. Preserve the same requestId after an unconfirmed response. Once accepted, poll by runId without resubmitting the prompt.
5. Close and reopen the browser to inspect recovery, logs, finished status, and unread results in the extension. This page saves the original request and runId locally for recovery.
6. Select Start a new run to create another run intentionally. Use extension pages for cancellation, handled flags, raw stdout/stderr, prompt configuration, and GitHub actions.

Example request:

```js
chrome.runtime.sendMessage(extensionId, {
  protocolVersion: 1,
  type: 'tasks.submit',
  payload: {
    task: {
      requestId: crypto.randomUUID(),
      actionId: 'issue:7:reproduction',
      actionKind: 'reproduction-setup',
      repository: 'microsoft/PowerToys',
      target: { type: 'issue', number: 7 },
      prompt: 'Inspect relevant code and test entry points using read-only access. Do not build.'
    }
  }
}, response => {
  if (chrome.runtime.lastError) console.error(chrome.runtime.lastError.message);
  else console.log(response);
});
```

Recovery uses `tasks.get` with `{runId}` and `tasks.events` with `{runId,afterSequence:0,limit:100}`. Deduplicate events by sequence and continue using nextSequence. Render output as text. GitHub writes require an explicit action from an extension page.

import type { Connection, NativeRequest, Reply } from './types.js';
import { ProtocolError, asError } from './policy.js';

interface Pending { resolve: (value: unknown) => void; reject: (error: Error) => void; timer: ReturnType<typeof setTimeout> }
export class NativeClient {
  private port?: chrome.runtime.Port;
  private pending = new Map<string, Pending>();
  private retry?: ReturnType<typeof setTimeout>;
  private retryDelay = 1_000;
  private current: Connection = { state: 'disconnected' };
  onConnectionChange: (connection: Connection) => void = () => {};
  get connection(): Connection { return this.current; }

  connect(): void {
    if (this.port) return;
    if (this.retry) clearTimeout(this.retry);
    this.retry = undefined;
    this.setConnection({ state: 'connecting', lastConnectedAt: this.current.lastConnectedAt });
    try {
      const port = chrome.runtime.connectNative('com.powertoys.pulse');
      this.port = port;
      port.onMessage.addListener((message: Reply) => {
        if (!message || message.protocolVersion !== 1 || typeof message.id !== 'string' || typeof message.ok !== 'boolean') {
          this.disconnect(new ProtocolError('PROTOCOL_MISMATCH', 'The Host returned an incompatible message.', 'Install the Host version that matches this extension.'));
          return;
        }
        this.retryDelay = 1_000;
        if (this.current.state !== 'connected') this.setConnection({ state: 'connected', lastConnectedAt: new Date().toISOString() });
        const pending = this.pending.get(message.id);
        if (!pending) return;
        clearTimeout(pending.timer); this.pending.delete(message.id);
        if (message.ok) pending.resolve(message.data);
        else pending.reject(new ProtocolError(message.error?.code ?? 'HOST_ERROR', message.error?.message ?? 'The Host returned an error.', message.error?.guidance));
      });
      port.onDisconnect.addListener(() => {
        const detail = chrome.runtime.lastError?.message;
        if (this.port !== port) return;
        this.disconnect(new ProtocolError('HOST_DISCONNECTED', detail || 'The local Host connection was closed.', 'Check the Host installation and extension ID registration for this browser. Accepted tasks continue locally; reconnect to read their status.'));
      });
    } catch (error) { this.disconnect(error instanceof Error ? error : new Error(String(error))); }
  }

  request<T>(type: string, payload: unknown = {}): Promise<T> {
    if (this.pending.size >= 64) return Promise.reject(new ProtocolError('CLIENT_BUSY', 'The limit of 64 pending Host requests has been reached.', 'Wait for current requests to finish before refreshing or retrying.'));
    this.connect();
    const port = this.port;
    if (!port) return Promise.reject(new ProtocolError('HOST_UNAVAILABLE', 'Cannot connect to the local Host.', 'Install or repair the Native Messaging Host, then retry.'));
    const id = crypto.randomUUID();
    const request: NativeRequest = { id, protocolVersion: 1, type, payload };
    if (new TextEncoder().encode(JSON.stringify(request)).length > 256 * 1024) return Promise.reject(new ProtocolError('MESSAGE_TOO_LARGE', 'The request exceeds the Host limit of 256 KiB.', 'Reduce the prompt, context, or number of selected suggestions.'));
    return new Promise<T>((resolve, reject) => {
      // A lost response is never retried automatically, especially for a GitHub write.
      const timer = setTimeout(() => {
        this.pending.delete(id);
        reject(new ProtocolError('RESPONSE_UNKNOWN', 'The Host response was not received in time. The operation outcome is unconfirmed.', 'Reload the task or GitHub operation records before retrying an unconfirmed operation.'));
      }, type.startsWith('operations.') || type.startsWith('webActions.') ? 120_000 : 60_000);
      this.pending.set(id, { resolve: value => resolve(value as T), reject, timer });
      try { port.postMessage(request); } catch (error) {
        clearTimeout(timer); this.pending.delete(id);
        reject(error); this.disconnect(error instanceof Error ? error : new Error(String(error)));
      }
    });
  }

  private setConnection(connection: Connection): void { this.current = connection; this.onConnectionChange(connection); }
  private disconnect(error: Error): void {
    const port = this.port; this.port = undefined;
    try { port?.disconnect(); } catch { /* Already disconnected. */ }
    for (const pending of this.pending.values()) { clearTimeout(pending.timer); pending.reject(error); }
    this.pending.clear();
    this.setConnection({ state: 'disconnected', lastConnectedAt: this.current.lastConnectedAt, error: asError(error) });
    if (!this.retry) this.retry = setTimeout(() => {
      this.retry = undefined;
      void this.request('hello').catch(() => {});
    }, this.retryDelay);
    this.retryDelay = Math.min(this.retryDelay * 2, 60_000);
  }
}

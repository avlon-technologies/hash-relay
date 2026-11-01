// Typed client for the HashRelay HTTP API (/api/v1). Mirrors HashRelay.Contracts.
import { appVersion, Pairing, platform, TenantInfo } from './config';

export type CredentialInfo = { id: string; label: string; kind: string; scopes: string[]; hint: string };
export type MeResponse = { tenant: TenantInfo; credential: CredentialInfo; serverTime: string; serverVersion: string };
export type EndpointInfo = { endpointId: string; name: string; platform: string; online: boolean; lastSeenAt: string; subscriptions: string[]; pushProvider: string | null };
export type SubscriptionsResponse = { endpointId: string; tags: string[] };
export type DeviceCodeResponse = { deviceCode: string; userCode: string; verificationUrl: string; verificationUrlComplete: string; expiresInSeconds: number; intervalSeconds: number };
export type DeviceTokenResponse = { status: 'pending' | 'approved' | 'denied' | 'expired'; token: string | null; tenant: TenantInfo | null; credential: CredentialInfo | null };
export type PublishRequest = { tags: string[]; title: string; body?: string; url?: string; priority?: 'low' | 'normal' | 'high'; markdown?: string };
export type PublishResult = { notificationId: string; acceptedAtUtc: string; tags: string[]; title: string; matchedEndpoints: number; deliveredLive: number; queued: number; pushed: number; duplicate: boolean; detailUrl: string | null };
/** Server-verified publisher attribution; `name` is null (and `type` 'unknown') for notifications stored before attribution existed. */
export type PublisherInfo = { id: string | null; name: string | null; type: 'agent' | 'application' | 'service' | 'human' | 'unknown'; channel: string | null };
export type EndpointDeliveryInfo = {
  deliveryId: string; status: 'queued' | 'sent' | 'pushed' | 'acked' | 'expired'; notificationId: string; tags: string[]; title: string; body: string | null;
  url: string | null; priority: string; publishedAtUtc: string; imageUrl: string | null; detailUrl: string | null; rich: boolean; ackedAtUtc: string | null;
  publisher?: PublisherInfo | null;
};
/** Server → client hub payload (NotificationReceived). */
export type NotificationMessage = {
  notificationId: string; tenantId: string; tags: string[]; title: string; body: string | null; publishedAtUtc: string; deliveryId: string; url: string | null;
  priority: string; tenantName: string; replayed: boolean; imageUrl: string | null; detailUrl: string | null; rich: boolean; publisher?: PublisherInfo | null;
};
export type PushRegistration = { provider: string; token: string } | null;

export class ApiError extends Error {
  constructor(public status: number, public code: string, message: string, public details?: Record<string, string[]>) {
    super(message);
  }
  /** Field-level problems joined for display. */
  get detail(): string {
    if (!this.details) return this.message;
    const lines = Object.entries(this.details).map(([k, v]) => `${k}: ${v.join(' ')}`);
    return lines.length ? lines.join('\n') : this.message;
  }
}

type Init = { method?: 'GET' | 'POST' | 'PUT' | 'DELETE'; token?: string | null; body?: unknown; timeoutMs?: number };

async function call<T>(baseUrl: string, path: string, init: Init = {}): Promise<T> {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), init.timeoutMs ?? 20_000);
  try {
    const headers: Record<string, string> = { Accept: 'application/json', 'User-Agent': `hashrelay-mobile/${appVersion} (${platform})` };
    if (init.token) headers.Authorization = `Bearer ${init.token}`;
    if (init.body !== undefined) headers['Content-Type'] = 'application/json';
    const res = await fetch(baseUrl + path, { method: init.method ?? 'GET', headers, body: init.body === undefined ? undefined : JSON.stringify(init.body), signal: controller.signal });
    const text = await res.text();
    if (!res.ok) {
      let code = 'unexpected';
      let message = `${res.status} ${res.statusText || ''}`.trim();
      let details: Record<string, string[]> | undefined;
      try {
        const err = JSON.parse(text);
        code = err.code ?? code; message = err.message ?? message; details = err.details;
      } catch { /* not an API error envelope */ }
      throw new ApiError(res.status, code, message, details);
    }
    return (text ? JSON.parse(text) : undefined) as T;
  } catch (e) {
    if (e instanceof ApiError) throw e;
    const msg = e instanceof Error && e.name === 'AbortError' ? 'The server did not respond in time.' : `Could not reach ${baseUrl}: ${(e as Error).message}`;
    throw new ApiError(0, 'network', msg);
  } finally {
    clearTimeout(timer);
  }
}

const q = (s: string) => encodeURIComponent(s);

export const api = {
  // ---- pairing (anonymous) ----
  deviceCode: (serverUrl: string, endpointId: string, deviceName: string) =>
    call<DeviceCodeResponse>(serverUrl, '/api/v1/device/code', { method: 'POST', body: { endpointId, deviceName, platform, agentVersion: appVersion, client: 'mobile' } }),
  deviceToken: (serverUrl: string, deviceCode: string) =>
    call<DeviceTokenResponse>(serverUrl, '/api/v1/device/token', { method: 'POST', body: { deviceCode } }),

  // ---- paired device ----
  me: (p: Pairing) => call<MeResponse>(p.serverUrl, '/api/v1/me', { token: p.token }),
  registerEndpoint: (p: Pairing, push: PushRegistration | { provider: string; token: '' }) =>
    call<EndpointInfo>(p.serverUrl, `/api/v1/endpoints/${q(p.endpointId)}`, {
      method: 'PUT', token: p.token,
      body: { endpointId: p.endpointId, platform, name: p.deviceName, agentVersion: appVersion, pushProvider: push?.provider ?? null, pushToken: push?.token ?? null },
    }),
  endpoint: (p: Pairing) => call<EndpointInfo>(p.serverUrl, `/api/v1/endpoints/${q(p.endpointId)}`, { token: p.token }),
  subscriptions: (p: Pairing) => call<SubscriptionsResponse>(p.serverUrl, `/api/v1/endpoints/${q(p.endpointId)}/subscriptions`, { token: p.token }),
  subscribe: (p: Pairing, tags: string[]) =>
    call<SubscriptionsResponse>(p.serverUrl, `/api/v1/endpoints/${q(p.endpointId)}/subscriptions`, { method: 'POST', token: p.token, body: { tags } }),
  unsubscribe: (p: Pairing, tags: string[]) =>
    call<SubscriptionsResponse>(p.serverUrl, `/api/v1/endpoints/${q(p.endpointId)}/subscriptions?` + tags.map((t) => 'tags=' + q(t)).join('&'), { method: 'DELETE', token: p.token }),
  publish: (p: Pairing, req: PublishRequest) => call<PublishResult>(p.serverUrl, '/api/v1/notifications', { method: 'POST', token: p.token, body: req }),
  deliveries: (p: Pairing, limit = 100) => call<EndpointDeliveryInfo[]>(p.serverUrl, `/api/v1/endpoints/${q(p.endpointId)}/deliveries?limit=${limit}`, { token: p.token }),
  ack: (p: Pairing, deliveryId: string) => call<void>(p.serverUrl, `/api/v1/endpoints/${q(p.endpointId)}/deliveries/${q(deliveryId)}/ack`, { method: 'POST', token: p.token }),
};

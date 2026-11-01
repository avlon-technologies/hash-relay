// Application state: identity, pairing, subscriptions, inbox, push registration and the foreground hub connection.
import React, { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState } from 'react';
import { AppState, AppStateStatus, Linking } from 'react-native';
import { api, ApiError, DeviceCodeResponse, EndpointDeliveryInfo, NotificationMessage, PublishRequest, PublishResult } from './api';
import { Identity, loadIdentity, loadPairing, normalizeServerUrl, Pairing, saveIdentity, savePairing } from './config';
import {
  ackFromPushData, ensureNotificationPermission, getPushToken, onForegroundPush, onLocalNotificationOpened, onPushOpened, onPushTokenRefresh,
  PushState, showLocalNotification,
} from './push';
import { ConnectionState, Realtime } from './realtime';

export type PairingProgress = { phase: 'requesting' } | { phase: 'waiting'; code: DeviceCodeResponse; serverUrl: string } | { phase: 'error'; message: string };
export type Tab = 'subscriptions' | 'inbox' | 'publish' | 'settings';

type Store = {
  ready: boolean;
  identity: Identity | null;
  pairing: Pairing | null;
  pairingProgress: PairingProgress | null;
  subscriptions: string[];
  inbox: EndpointDeliveryInfo[];
  connection: ConnectionState;
  connectionDetail: string | undefined;
  push: PushState;
  notificationsAllowed: boolean | null;
  lastError: string | null;
  tab: Tab;
  setTab: (tab: Tab) => void;
  renameDevice: (name: string) => Promise<void>;
  beginPairing: (serverUrl: string) => Promise<void>;
  cancelPairing: () => void;
  unpair: () => Promise<void>;
  refreshSubscriptions: () => Promise<void>;
  subscribe: (tags: string[]) => Promise<void>;
  unsubscribe: (tag: string) => Promise<void>;
  publish: (req: PublishRequest) => Promise<PublishResult>;
  refreshInbox: () => Promise<void>;
  ack: (deliveryId: string) => Promise<void>;
  open: (item: { url: string | null; detailUrl: string | null; rich: boolean }) => Promise<void>;
  registerPush: () => Promise<void>;
  clearError: () => void;
};

const StoreContext = createContext<Store | null>(null);

export function useStore(): Store {
  const s = useContext(StoreContext);
  if (!s) throw new Error('StoreProvider missing');
  return s;
}

const fromLive = (m: NotificationMessage): EndpointDeliveryInfo => ({
  deliveryId: m.deliveryId, status: 'sent', notificationId: m.notificationId, tags: m.tags, title: m.title, body: m.body, url: m.url,
  priority: m.priority, publishedAtUtc: m.publishedAtUtc, imageUrl: m.imageUrl, detailUrl: m.detailUrl, rich: m.rich, ackedAtUtc: null, publisher: m.publisher ?? null,
});

export function StoreProvider({ children }: { children: React.ReactNode }) {
  const [ready, setReady] = useState(false);
  const [identity, setIdentity] = useState<Identity | null>(null);
  const [pairing, setPairing] = useState<Pairing | null>(null);
  const [pairingProgress, setPairingProgress] = useState<PairingProgress | null>(null);
  const [subscriptions, setSubscriptions] = useState<string[]>([]);
  const [inbox, setInbox] = useState<EndpointDeliveryInfo[]>([]);
  const [connection, setConnection] = useState<ConnectionState>('disconnected');
  const [connectionDetail, setConnectionDetail] = useState<string | undefined>();
  const [push, setPush] = useState<PushState>({ provider: null, token: null, reason: 'not requested' });
  const [notificationsAllowed, setNotificationsAllowed] = useState<boolean | null>(null);
  const [lastError, setLastError] = useState<string | null>(null);
  const [tab, setTab] = useState<Tab>('subscriptions');

  const realtime = useRef<Realtime | null>(null);
  const pairingRef = useRef<Pairing | null>(null);
  const pollCancel = useRef<{ cancelled: boolean } | null>(null);
  pairingRef.current = pairing;

  const fail = useCallback((e: unknown) => {
    const msg = e instanceof ApiError ? e.detail : (e as Error)?.message ?? String(e);
    setLastError(msg);
  }, []);

  // ---- startup ----
  useEffect(() => {
    (async () => {
      try {
        const [id, p] = await Promise.all([loadIdentity(), loadPairing()]);
        setIdentity(id);
        setPairing(p);
      } finally {
        setReady(true);
      }
    })();
  }, []);

  // ---- inbox / subscriptions ----
  const refreshInbox = useCallback(async () => {
    const p = pairingRef.current;
    if (!p) return;
    try { setInbox(await api.deliveries(p)); } catch (e) { fail(e); }
  }, [fail]);

  const refreshSubscriptions = useCallback(async () => {
    const p = pairingRef.current;
    if (!p) return;
    try { setSubscriptions((await api.subscriptions(p)).tags); } catch (e) { fail(e); }
  }, [fail]);

  const markAcked = useCallback((deliveryId: string) => {
    setInbox((list) => list.map((d) => (d.deliveryId === deliveryId && d.status !== 'acked' ? { ...d, status: 'acked', ackedAtUtc: new Date().toISOString() } : d)));
  }, []);

  const ack = useCallback(async (deliveryId: string) => {
    const p = pairingRef.current;
    if (!p) return;
    markAcked(deliveryId);
    if (await realtime.current?.ack(deliveryId)) return;
    try { await api.ack(p, deliveryId); } catch { /* shown on next refresh */ }
  }, [markAcked]);

  // ---- push registration (token → server) ----
  const registerPushWith = useCallback(async (p: Pairing, state: PushState) => {
    try {
      await api.registerEndpoint(p, state.provider ? { provider: state.provider, token: state.token } : null);
    } catch (e) { fail(e); }
  }, [fail]);

  const registerPush = useCallback(async () => {
    const p = pairingRef.current;
    const allowed = await ensureNotificationPermission();
    setNotificationsAllowed(allowed);
    const state = await getPushToken();
    setPush(state);
    realtime.current?.setPush(state);
    if (p) await registerPushWith(p, state);
  }, [registerPushWith]);

  // ---- realtime connection lifecycle: connected while the app is in the foreground ----
  useEffect(() => {
    if (!ready) return;
    if (!pairing) { void realtime.current?.stop(); realtime.current = null; return; }
    const rt = new Realtime(pairing, {
      onState: (s, detail) => { setConnection(s); setConnectionDetail(detail); },
      onHello: (h) => { setSubscriptions(h.subscriptions); void refreshInbox(); },
      onNotification: (m) => {
        setInbox((list) => (list.some((d) => d.deliveryId === m.deliveryId) ? list : [fromLive(m), ...list]));
        if (!m.replayed) void showLocalNotification(m.title, m.body ?? '', { deliveryId: m.deliveryId, url: m.url, detailUrl: m.detailUrl, rich: m.rich });
        // Live deliveries are acknowledged on receipt, as the desktop agent does once the toast is shown.
        void rt.ack(m.deliveryId).then((ok) => { if (ok) markAcked(m.deliveryId); });
      },
      onSubscriptionsChanged: (tags) => setSubscriptions(tags),
      onCredentialRevoked: async (reason) => {
        setLastError(`This device was removed from the workspace (${reason}). Pair it again to continue.`);
        await savePairing(null);
        setPairing(null);
      },
    });
    realtime.current = rt;
    void rt.start();
    void refreshInbox();
    void registerPush();

    const sub = AppState.addEventListener('change', (s: AppStateStatus) => {
      if (s === 'active') { void rt.start(); void refreshInbox(); }
      else if (s === 'background') { void rt.stop(); }
    });
    return () => { sub.remove(); void rt.stop(); if (realtime.current === rt) realtime.current = null; };
    // registerPush/refreshInbox are stable callbacks; this effect is keyed on the pairing only.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [ready, pairing]);

  // ---- push events ----
  useEffect(() => {
    const offRefresh = onPushTokenRefresh((token) => {
      const state: PushState = { provider: 'fcm', token };
      setPush(state);
      realtime.current?.setPush(state);
      const p = pairingRef.current;
      if (p) void registerPushWith(p, state);
    });
    const offForeground = onForegroundPush((title, body, data) => {
      void showLocalNotification(title, body, { ...(data ?? {}) });
      void ackFromPushData(data).then((id) => { if (id) markAcked(id); });
      void refreshInbox();
    });
    const openFromData = (data: Record<string, unknown> | undefined) => {
      setTab('inbox');
      void ackFromPushData(data as never).then((id) => { if (id) markAcked(id); });
      void refreshInbox();
    };
    const offOpened = onPushOpened((data) => openFromData(data as Record<string, unknown>));
    const offLocal = onLocalNotificationOpened((data) => openFromData(data));
    return () => { offRefresh(); offForeground(); offOpened(); offLocal(); };
  }, [markAcked, refreshInbox, registerPushWith]);

  // ---- pairing flow ----
  const cancelPairing = useCallback(() => {
    if (pollCancel.current) pollCancel.current.cancelled = true;
    pollCancel.current = null;
    setPairingProgress(null);
  }, []);

  const beginPairing = useCallback(async (serverInput: string) => {
    if (!identity) return;
    cancelPairing();
    const serverUrl = normalizeServerUrl(serverInput);
    setPairingProgress({ phase: 'requesting' });
    let code: DeviceCodeResponse;
    try {
      code = await api.deviceCode(serverUrl, identity.endpointId, identity.deviceName);
    } catch (e) {
      setPairingProgress({ phase: 'error', message: e instanceof ApiError ? e.detail : (e as Error).message });
      return;
    }
    setPairingProgress({ phase: 'waiting', code, serverUrl });
    const token = { cancelled: false };
    pollCancel.current = token;
    const deadline = Date.now() + code.expiresInSeconds * 1000;
    while (!token.cancelled && Date.now() < deadline) {
      await new Promise((r) => setTimeout(r, Math.max(2, code.intervalSeconds) * 1000));
      if (token.cancelled) return;
      try {
        const res = await api.deviceToken(serverUrl, code.deviceCode);
        if (res.status === 'pending') continue;
        if (res.status === 'approved' && res.token && res.tenant && res.credential) {
          const p: Pairing = {
            serverUrl, endpointId: identity.endpointId, deviceName: identity.deviceName, token: res.token, tenant: res.tenant,
            scopes: res.credential.scopes, credentialHint: res.credential.hint, pairedAt: new Date().toISOString(),
          };
          await savePairing(p);
          setPairingProgress(null);
          setPairing(p);
          setTab('subscriptions');
          return;
        }
        setPairingProgress({ phase: 'error', message: res.status === 'denied' ? 'The pairing was denied in the dashboard.' : 'The pairing code expired. Start again.' });
        return;
      } catch (e) {
        if (e instanceof ApiError && e.status === 0) continue; // transient network problem: keep polling
        setPairingProgress({ phase: 'error', message: e instanceof ApiError ? e.detail : (e as Error).message });
        return;
      }
    }
    if (!token.cancelled) setPairingProgress({ phase: 'error', message: 'The pairing code expired. Start again.' });
  }, [identity, cancelPairing]);

  const unpair = useCallback(async () => {
    const p = pairingRef.current;
    if (p && push.provider) { try { await api.registerEndpoint(p, { provider: push.provider, token: '' }); } catch { /* best effort */ } }
    await realtime.current?.stop();
    await savePairing(null);
    setPairing(null);
    setSubscriptions([]);
    setInbox([]);
    setTab('subscriptions');
  }, [push]);

  const renameDevice = useCallback(async (name: string) => {
    const n = name.trim().slice(0, 80);
    if (!identity || !n) return;
    const id = { ...identity, deviceName: n };
    await saveIdentity(id);
    setIdentity(id);
    const p = pairingRef.current;
    if (p) {
      const np = { ...p, deviceName: n };
      await savePairing(np);
      setPairing(np);
    }
  }, [identity]);

  // ---- subscriptions & publishing ----
  const subscribe = useCallback(async (tags: string[]) => {
    const p = pairingRef.current;
    if (!p || tags.length === 0) return;
    try { setSubscriptions((await api.subscribe(p, tags)).tags); } catch (e) { fail(e); throw e; }
  }, [fail]);

  const unsubscribe = useCallback(async (tag: string) => {
    const p = pairingRef.current;
    if (!p) return;
    setSubscriptions((s) => s.filter((t) => t !== tag));
    try { setSubscriptions((await api.unsubscribe(p, [tag])).tags); } catch (e) { fail(e); void refreshSubscriptions(); }
  }, [fail, refreshSubscriptions]);

  const publish = useCallback(async (req: PublishRequest) => {
    const p = pairingRef.current;
    if (!p) throw new Error('Not paired.');
    return api.publish(p, req);
  }, []);

  const open = useCallback(async (item: { url: string | null; detailUrl: string | null; rich: boolean }) => {
    const target = item.rich && item.detailUrl ? item.detailUrl : item.url ?? item.detailUrl;
    if (!target) return;
    try { await Linking.openURL(target); } catch (e) { fail(e); }
  }, [fail]);

  const value = useMemo<Store>(() => ({
    ready, identity, pairing, pairingProgress, subscriptions, inbox, connection, connectionDetail, push, notificationsAllowed, lastError, tab, setTab,
    renameDevice, beginPairing, cancelPairing, unpair, refreshSubscriptions, subscribe, unsubscribe, publish, refreshInbox, ack, open, registerPush,
    clearError: () => setLastError(null),
  }), [ready, identity, pairing, pairingProgress, subscriptions, inbox, connection, connectionDetail, push, notificationsAllowed, lastError, tab,
    renameDevice, beginPairing, cancelPairing, unpair, refreshSubscriptions, subscribe, unsubscribe, publish, refreshInbox, ack, open, registerPush]);

  return <StoreContext.Provider value={value}>{children}</StoreContext.Provider>;
}

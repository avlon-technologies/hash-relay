// Push via Firebase Cloud Messaging on both platforms (Firebase relays iOS to APNs). The Firebase modules are loaded
// lazily so the app still runs when the Firebase client files are absent (see app.config.js); push is then reported as
// unavailable and delivery happens over the realtime hub while the app is open.
import * as Notifications from 'expo-notifications';
import { Platform } from 'react-native';
import { api } from './api';
import { loadPairing } from './config';

export const ANDROID_CHANNEL = 'hashrelay'; // must match HashRelay:Push:Fcm:AndroidChannelId on the server
export type PushData = Record<string, string | object> | undefined;
export type PushState = { provider: 'fcm'; token: string } | { provider: null; token: null; reason: string };

type Messaging = typeof import('@react-native-firebase/messaging');
let messagingPromise: Promise<Messaging | null> | null = null;

function messaging(): Promise<Messaging | null> {
  messagingPromise ??= import('@react-native-firebase/messaging').then((m) => m, () => null);
  return messagingPromise;
}

/** Foreground presentation of notifications the app schedules locally (live hub deliveries, foreground pushes). */
export function configureForegroundDisplay(): void {
  Notifications.setNotificationHandler({
    handleNotification: async () => ({ shouldShowBanner: true, shouldShowList: true, shouldPlaySound: true, shouldSetBadge: false }),
  });
}

/** Asks for the OS notification permission and creates the Android channel. Returns whether notifications may be shown. */
export async function ensureNotificationPermission(): Promise<boolean> {
  if (Platform.OS === 'android') {
    await Notifications.setNotificationChannelAsync(ANDROID_CHANNEL, {
      name: 'HashRelay notifications',
      importance: Notifications.AndroidImportance.HIGH,
      sound: 'default',
      lightColor: '#2563eb',
    });
  }
  const current = await Notifications.getPermissionsAsync();
  if (current.granted) return true;
  const asked = await Notifications.requestPermissionsAsync();
  return asked.granted;
}

/** Obtains the FCM registration token, requesting permission on iOS. Never throws. */
export async function getPushToken(): Promise<PushState> {
  const fb = await messaging();
  if (!fb) return { provider: null, token: null, reason: 'Firebase is not available in this build.' };
  try {
    const m = fb.getMessaging();
    if (Platform.OS === 'ios') {
      const status = await fb.requestPermission(m);
      if (status <= 0) return { provider: null, token: null, reason: 'Notification permission was not granted.' };
      if (!fb.isDeviceRegisteredForRemoteMessages(m)) await fb.registerDeviceForRemoteMessages(m);
    }
    const token = await fb.getToken(m);
    if (!token) return { provider: null, token: null, reason: 'Firebase returned no token.' };
    return { provider: 'fcm', token };
  } catch (e) {
    return { provider: null, token: null, reason: (e as Error).message ?? 'Firebase token request failed.' };
  }
}

export function onPushTokenRefresh(cb: (token: string) => void): () => void {
  let off: (() => void) | null = null;
  let cancelled = false;
  messaging().then((fb) => {
    if (!fb || cancelled) return;
    try { off = fb.onTokenRefresh(fb.getMessaging(), cb); } catch { /* Firebase not initialised */ }
  });
  return () => { cancelled = true; off?.(); };
}

/** A push that arrived while the app was in the foreground; the OS shows nothing, so the app does. */
export function onForegroundPush(cb: (title: string, body: string, data: PushData) => void): () => void {
  let off: (() => void) | null = null;
  let cancelled = false;
  messaging().then((fb) => {
    if (!fb || cancelled) return;
    try {
      off = fb.onMessage(fb.getMessaging(), async (msg) => {
        cb(msg.notification?.title ?? 'HashRelay', msg.notification?.body ?? '', msg.data as PushData);
      });
    } catch { /* Firebase not initialised */ }
  });
  return () => { cancelled = true; off?.(); };
}

/** The user tapped a push: from the background (onNotificationOpenedApp) or from a cold start (getInitialNotification). */
export function onPushOpened(cb: (data: PushData) => void): () => void {
  let off: (() => void) | null = null;
  let cancelled = false;
  messaging().then(async (fb) => {
    if (!fb || cancelled) return;
    try {
      const m = fb.getMessaging();
      const initial = await fb.getInitialNotification(m);
      if (initial && !cancelled) cb(initial.data as PushData);
      off = fb.onNotificationOpenedApp(m, (msg) => cb(msg.data as PushData));
    } catch { /* Firebase not initialised */ }
  });
  return () => { cancelled = true; off?.(); };
}

/** Headless handler for pushes received while backgrounded or killed: acknowledge the delivery so the dashboard shows it reached the device. */
export function registerBackgroundPushHandler(): void {
  messaging().then((fb) => {
    if (!fb) return;
    try { fb.setBackgroundMessageHandler(fb.getMessaging(), async (msg) => { await ackFromPushData(msg.data as PushData); }); }
    catch { /* Firebase not initialised */ }
  });
}

export async function ackFromPushData(data: PushData): Promise<string | null> {
  const deliveryId = typeof data?.deliveryId === 'string' ? data.deliveryId : null;
  if (!deliveryId) return null;
  const pairing = await loadPairing();
  if (!pairing) return deliveryId;
  try { await api.ack(pairing, deliveryId); } catch { /* best effort; the inbox refresh will show the true status */ }
  return deliveryId;
}

export async function showLocalNotification(title: string, body: string, data: Record<string, unknown>): Promise<void> {
  await Notifications.scheduleNotificationAsync({
    content: { title, body, data, sound: 'default' },
    trigger: null,
  });
}

/** The user tapped a locally scheduled notification (live delivery shown while the app was open). */
export function onLocalNotificationOpened(cb: (data: Record<string, unknown>) => void): () => void {
  const sub = Notifications.addNotificationResponseReceivedListener((r) => cb(r.notification.request.content.data ?? {}));
  return () => sub.remove();
}

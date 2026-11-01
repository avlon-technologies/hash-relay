import Constants from 'expo-constants';
import * as Crypto from 'expo-crypto';
import * as Device from 'expo-device';
import * as SecureStore from 'expo-secure-store';
import { Platform } from 'react-native';

export type TenantInfo = { id: string; slug: string; name: string };

/** What the phone knows about itself before it is paired: a stable endpoint id and a display name. */
export type Identity = { endpointId: string; deviceName: string };

/** A paired device: the subscriber token issued by the workspace member who approved the pairing. */
export type Pairing = {
  serverUrl: string;
  endpointId: string;
  deviceName: string;
  token: string;
  tenant: TenantInfo;
  scopes: string[];
  credentialHint: string;
  pairedAt: string;
};

const KEY_IDENTITY = 'hashrelay.identity';
const KEY_PAIRING = 'hashrelay.pairing';

export const appVersion: string = Constants.expoConfig?.version ?? '0.0.0';
export const defaultServerUrl: string = Constants.expoConfig?.extra?.defaultServerUrl ?? 'https://hashrelay.dev.avlon.ca';
/** Whether this build has Firebase client files for this OS (see app.config.js); without them push is off. */
export const firebaseConfigured: boolean = Constants.expoConfig?.extra?.firebase?.[Platform.OS] === true;
export const platform: string = Platform.OS;

export function normalizeServerUrl(input: string): string {
  let s = input.trim().replace(/\/+$/, '');
  if (!/^https?:\/\//i.test(s)) s = 'https://' + s;
  return s;
}

export async function loadIdentity(): Promise<Identity> {
  const raw = await SecureStore.getItemAsync(KEY_IDENTITY);
  if (raw) return JSON.parse(raw) as Identity;
  const identity: Identity = {
    endpointId: `${platform}-${Crypto.randomUUID()}`,
    deviceName: (Device.deviceName || Device.modelName || `${platform} phone`).slice(0, 80),
  };
  await SecureStore.setItemAsync(KEY_IDENTITY, JSON.stringify(identity));
  return identity;
}

export async function saveIdentity(identity: Identity): Promise<void> {
  await SecureStore.setItemAsync(KEY_IDENTITY, JSON.stringify(identity));
}

export async function loadPairing(): Promise<Pairing | null> {
  const raw = await SecureStore.getItemAsync(KEY_PAIRING);
  return raw ? (JSON.parse(raw) as Pairing) : null;
}

export async function savePairing(pairing: Pairing | null): Promise<void> {
  if (pairing === null) await SecureStore.deleteItemAsync(KEY_PAIRING);
  else await SecureStore.setItemAsync(KEY_PAIRING, JSON.stringify(pairing));
}

export const hasScope = (p: Pairing | null, scope: string) => !!p && p.scopes.some((s) => s.toLowerCase() === scope);

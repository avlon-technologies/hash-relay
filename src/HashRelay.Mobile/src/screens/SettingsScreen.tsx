import React, { useState } from 'react';
import { Alert, ScrollView, View } from 'react-native';
import { appVersion, firebaseConfigured, platform } from '../config';
import { useStore } from '../store';
import { Banner, Button, Card, Dot, Input, Muted, styles, Title, useTheme } from '../ui';

export function SettingsScreen() {
  const t = useTheme();
  const { pairing, identity, push, connection, connectionDetail, notificationsAllowed, registerPush, renameDevice, unpair, lastError, clearError } = useStore();
  const [name, setName] = useState(pairing?.deviceName ?? identity?.deviceName ?? '');
  const [saving, setSaving] = useState(false);

  const confirmUnpair = () => Alert.alert('Unregister this device?', 'The phone stops receiving notifications and its subscriptions stay on the server until the device is removed in the dashboard.', [
    { text: 'Cancel', style: 'cancel' },
    { text: 'Unregister', style: 'destructive', onPress: () => { void unpair(); } },
  ]);

  return (
    <ScrollView style={[styles.screen, { backgroundColor: t.bg }]} contentContainerStyle={styles.content} keyboardShouldPersistTaps="handled">
      <Title>Settings</Title>
      {lastError ? <Banner kind="error" text={lastError} onDismiss={clearError} /> : null}

      <Card>
        <Muted>Workspace</Muted>
        <Muted style={{ fontSize: 16, color: t.text }}>{pairing?.tenant.name} ({pairing?.tenant.slug})</Muted>
        <Muted>{pairing?.serverUrl}</Muted>
        <Muted>Device token {pairing?.credentialHint} · permissions: {pairing?.scopes.join(', ')}</Muted>
        <Muted>Endpoint id: {pairing?.endpointId}</Muted>
        <Muted>Paired {pairing ? new Date(pairing.pairedAt).toLocaleString() : ''}</Muted>
      </Card>

      <Card>
        <Input label="Device name" value={name} onChangeText={setName} maxLength={80} />
        <Button kind="secondary" label="Rename" busy={saving} disabled={!name.trim() || name.trim() === pairing?.deviceName}
          onPress={async () => { setSaving(true); try { await renameDevice(name); } finally { setSaving(false); } }} />
      </Card>

      <Card>
        <Muted>Delivery</Muted>
        <View style={styles.row}><Dot color={connection === 'connected' ? t.success : t.muted} /><Muted>Live connection while the app is open: {connection}{connectionDetail ? ` (${connectionDetail})` : ''}</Muted></View>
        <View style={styles.row}><Dot color={push.provider ? t.success : t.muted} /><Muted>Push while in the background: {push.provider ? `${push.provider.toUpperCase()} token registered` : push.reason}</Muted></View>
        <View style={styles.row}><Dot color={notificationsAllowed ? t.success : t.muted} /><Muted>System notification permission: {notificationsAllowed === null ? 'unknown' : notificationsAllowed ? 'granted' : 'blocked'}</Muted></View>
        {!firebaseConfigured ? <Muted>This build was made without Firebase client files, so background push is unavailable; see the README.</Muted> : null}
        <Button kind="secondary" label="Re-register push token" onPress={registerPush} />
      </Card>

      <Card>
        <Button kind="danger" label="Unregister this device" onPress={confirmUnpair} />
        <Muted>HashRelay mobile {appVersion} · {platform}</Muted>
      </Card>
    </ScrollView>
  );
}

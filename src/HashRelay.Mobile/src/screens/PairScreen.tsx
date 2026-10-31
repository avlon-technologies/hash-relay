// First run: register this phone with a HashRelay workspace. Mirrors the agent's pairing window: ask for a code,
// show it, open the dashboard's /pair page (signed-in on the phone's browser) and wait for approval.
import React, { useState } from 'react';
import { KeyboardAvoidingView, Linking, Platform, ScrollView, Share, View } from 'react-native';
import { defaultServerUrl } from '../config';
import { useStore } from '../store';
import { Banner, Body, Button, Card, Input, Muted, styles, Title, useTheme } from '../ui';

export function PairScreen() {
  const t = useTheme();
  const { identity, pairingProgress, beginPairing, cancelPairing, renameDevice } = useStore();
  const [server, setServer] = useState(defaultServerUrl);
  const [name, setName] = useState(identity?.deviceName ?? '');
  const waiting = pairingProgress?.phase === 'waiting' ? pairingProgress : null;

  const start = async () => {
    if (name.trim() && name.trim() !== identity?.deviceName) await renameDevice(name);
    await beginPairing(server);
  };

  return (
    <KeyboardAvoidingView style={[styles.screen, { backgroundColor: t.bg }]} behavior={Platform.OS === 'ios' ? 'padding' : undefined}>
      <ScrollView contentContainerStyle={styles.content} keyboardShouldPersistTaps="handled">
        <Title>Register this device</Title>
        <Body>Pair the phone with a HashRelay workspace once. You can then subscribe to hashtags and receive notifications, even before anyone publishes.</Body>

        {!waiting ? (
          <Card>
            <Input label="Server" value={server} onChangeText={setServer} autoCapitalize="none" autoCorrect={false} keyboardType="url" placeholder="https://hashrelay.example" />
            <Input label="Device name" value={name} onChangeText={setName} placeholder="My phone" maxLength={80} />
            <Muted>Endpoint id: {identity?.endpointId}</Muted>
            <Button label="Get a pairing code" onPress={start} busy={pairingProgress?.phase === 'requesting'} disabled={!server.trim()} />
            {pairingProgress?.phase === 'error' ? <Banner kind="error" text={pairingProgress.message} onDismiss={cancelPairing} /> : null}
          </Card>
        ) : (
          <Card>
            <Muted>Approve this code in the dashboard</Muted>
            <Body style={{ fontSize: 34, fontWeight: '800', letterSpacing: 4, textAlign: 'center', paddingVertical: 8 }}>{waiting.code.userCode}</Body>
            <Muted>
              Open the link below on this phone (or on any signed-in browser), pick the workspace and tick the permissions you want this phone to have: receive notifications, and optionally publish.
            </Muted>
            <Button label="Open the approval page" onPress={() => Linking.openURL(waiting.code.verificationUrlComplete)} />
            <View style={[styles.row, { justifyContent: 'space-between' }]}>
              <Button kind="secondary" label="Share link" onPress={() => Share.share({ message: waiting.code.verificationUrlComplete })} style={{ flex: 1 }} />
              <Button kind="secondary" label="Cancel" onPress={cancelPairing} style={{ flex: 1 }} />
            </View>
            <Muted>Waiting for approval at {waiting.serverUrl} · code valid for {Math.round(waiting.code.expiresInSeconds / 60)} minutes.</Muted>
          </Card>
        )}
      </ScrollView>
    </KeyboardAvoidingView>
  );
}

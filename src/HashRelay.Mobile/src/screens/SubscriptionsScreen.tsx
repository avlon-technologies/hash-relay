// Home tab: the hashtags this phone receives. Subscribe ahead of time; notifications arrive by push when the app is
// in the background and live while it is open.
import React, { useState } from 'react';
import { RefreshControl, ScrollView, View } from 'react-native';
import { useStore } from '../store';
import { displayTag, parseTags } from '../tags';
import { Banner, Body, Button, Card, Chip, Dot, Input, Muted, styles, Title, useTheme } from '../ui';

export function SubscriptionsScreen() {
  const t = useTheme();
  const { pairing, subscriptions, subscribe, unsubscribe, refreshSubscriptions, connection, push, notificationsAllowed, registerPush } = useStore();
  const [input, setInput] = useState('');
  const [problem, setProblem] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [refreshing, setRefreshing] = useState(false);

  const add = async () => {
    const { tags, invalid } = parseTags(input);
    if (invalid.length) { setProblem(`Not a valid hashtag: ${invalid.join(', ')}. Use letters, digits, '_' or '-'.`); return; }
    if (!tags.length) return;
    setBusy(true); setProblem(null);
    try { await subscribe(tags); setInput(''); }
    catch (e) { setProblem((e as Error).message); }
    finally { setBusy(false); }
  };

  const refresh = async () => { setRefreshing(true); await refreshSubscriptions(); setRefreshing(false); };

  const live = connection === 'connected';
  const pushOk = push.provider !== null;
  const statusColor = live ? t.success : connection === 'disconnected' ? t.muted : t.warn;

  return (
    <ScrollView style={[styles.screen, { backgroundColor: t.bg }]} contentContainerStyle={styles.content} keyboardShouldPersistTaps="handled"
      refreshControl={<RefreshControl refreshing={refreshing} onRefresh={refresh} />}>
      <Title>Subscriptions</Title>
      <Muted>{pairing?.tenant.name} · {pairing?.deviceName}</Muted>

      <Card>
        <Input label="Subscribe to hashtags" value={input} onChangeText={setInput} placeholder="#builds #alerts" autoCapitalize="none" autoCorrect={false}
          onSubmitEditing={add} returnKeyType="done" />
        <Button label="Subscribe" onPress={add} busy={busy} disabled={!input.trim()} />
        {problem ? <Banner kind="error" text={problem} onDismiss={() => setProblem(null)} /> : null}
      </Card>

      <Card>
        <Muted>Receiving {subscriptions.length === 0 ? 'nothing yet' : `${subscriptions.length} hashtag${subscriptions.length === 1 ? '' : 's'}`}</Muted>
        {subscriptions.length === 0 ? (
          <Body>Add a hashtag above. Anything published to it in {pairing?.tenant.name ?? 'the workspace'} will reach this phone.</Body>
        ) : (
          <View style={styles.wrap}>
            {subscriptions.map((tag) => <Chip key={tag} label={displayTag(tag)} onRemove={() => unsubscribe(tag)} />)}
          </View>
        )}
      </Card>

      <Card>
        <View style={styles.row}><Dot color={statusColor} /><Muted>Live connection: {connection}</Muted></View>
        <View style={styles.row}><Dot color={pushOk ? t.success : t.muted} /><Muted>Background push: {pushOk ? `registered (${push.provider.toUpperCase()})` : push.reason}</Muted></View>
        {notificationsAllowed === false ? <Banner kind="info" text="Notifications are blocked for HashRelay in the system settings; nothing will be shown until they are allowed." /> : null}
        {!pushOk ? <Button kind="secondary" label="Retry push registration" onPress={registerPush} /> : null}
      </Card>
    </ScrollView>
  );
}

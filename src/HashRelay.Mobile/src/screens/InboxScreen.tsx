// What was routed to this phone, newest first. Pushed items that were never opened stay marked as such: provider
// acceptance is not display, so tapping one here acknowledges it.
import React, { useState } from 'react';
import { Pressable, RefreshControl, ScrollView, View } from 'react-native';
import { EndpointDeliveryInfo } from '../api';
import { useStore } from '../store';
import { displayTag } from '../tags';
import { Body, Card, Dot, Muted, styles, Title, useTheme } from '../ui';

function when(iso: string): string {
  const d = new Date(iso);
  const diff = (Date.now() - d.getTime()) / 1000;
  if (diff < 60) return 'just now';
  if (diff < 3600) return `${Math.floor(diff / 60)} min ago`;
  if (diff < 86400) return `${Math.floor(diff / 3600)} h ago`;
  return d.toLocaleDateString();
}

function statusLabel(d: EndpointDeliveryInfo): string {
  switch (d.status) {
    case 'acked': return 'seen';
    case 'pushed': return 'pushed to this phone';
    case 'sent': return 'delivered live';
    case 'queued': return 'waiting for this phone';
    case 'expired': return 'expired before delivery';
  }
}

export function InboxScreen() {
  const t = useTheme();
  const { inbox, refreshInbox, ack, open, pairing } = useStore();
  const [refreshing, setRefreshing] = useState(false);
  const refresh = async () => { setRefreshing(true); await refreshInbox(); setRefreshing(false); };

  const tap = (d: EndpointDeliveryInfo) => {
    if (d.status !== 'acked') void ack(d.deliveryId);
    void open(d);
  };

  return (
    <ScrollView style={[styles.screen, { backgroundColor: t.bg }]} contentContainerStyle={styles.content}
      refreshControl={<RefreshControl refreshing={refreshing} onRefresh={refresh} />}>
      <Title>Inbox</Title>
      <Muted>Notifications routed to this phone in {pairing?.tenant.name}. Pull to refresh.</Muted>
      {inbox.length === 0 ? (
        <Card><Body>Nothing yet. Subscribe to a hashtag and publish to it from the dashboard, the CLI, an API key or this app.</Body></Card>
      ) : inbox.map((d) => {
        const unseen = d.status !== 'acked';
        const color = d.priority === 'high' ? t.danger : unseen ? t.accent : t.muted;
        return (
          <Pressable key={d.deliveryId} onPress={() => tap(d)}>
            <Card>
              <View style={styles.row}>
                <Dot color={color} />
                <Body style={{ fontWeight: unseen ? '700' : '500', flex: 1 }}>{d.title}</Body>
                <Muted>{when(d.publishedAtUtc)}</Muted>
              </View>
              {d.body ? <Muted style={{ fontSize: 14, color: t.text }}>{d.body}</Muted> : null}
              <Muted>{d.tags.map(displayTag).join(' ')} · {statusLabel(d)}{d.rich ? ' · open for the full page' : d.url ? ' · opens link' : ''}</Muted>
            </Card>
          </Pressable>
        );
      })}
    </ScrollView>
  );
}

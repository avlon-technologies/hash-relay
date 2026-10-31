// HashRelay mobile: register the phone once, then subscribe to hashtags (the primary job), read what arrived, and
// publish when the device was approved with the publish permission.
import { StatusBar } from 'expo-status-bar';
import React from 'react';
import { ActivityIndicator, Platform, Pressable, SafeAreaView, StatusBar as RNStatusBar, StyleSheet, Text, View } from 'react-native';
import { InboxScreen } from './src/screens/InboxScreen';
import { PairScreen } from './src/screens/PairScreen';
import { PublishScreen } from './src/screens/PublishScreen';
import { SettingsScreen } from './src/screens/SettingsScreen';
import { SubscriptionsScreen } from './src/screens/SubscriptionsScreen';
import { StoreProvider, Tab, useStore } from './src/store';
import { useTheme } from './src/ui';

const TABS: { key: Tab; label: string; glyph: string }[] = [
  { key: 'subscriptions', label: 'Subscriptions', glyph: '#' },
  { key: 'inbox', label: 'Inbox', glyph: '▤' },
  { key: 'publish', label: 'Publish', glyph: '➤' },
  { key: 'settings', label: 'Settings', glyph: '⚙' },
];

function Shell() {
  const t = useTheme();
  const { ready, pairing, tab, setTab, inbox } = useStore();
  const unseen = inbox.filter((d) => d.status !== 'acked').length;

  if (!ready) {
    return (
      <View style={[styles.center, { backgroundColor: t.bg }]}>
        <ActivityIndicator color={t.accent} />
      </View>
    );
  }
  if (!pairing) return <PairScreen />;

  return (
    <View style={{ flex: 1, backgroundColor: t.bg }}>
      <View style={{ flex: 1 }}>
        {tab === 'subscriptions' ? <SubscriptionsScreen /> : tab === 'inbox' ? <InboxScreen /> : tab === 'publish' ? <PublishScreen /> : <SettingsScreen />}
      </View>
      <View style={[styles.tabs, { backgroundColor: t.card, borderTopColor: t.border }]}>
        {TABS.map((x) => {
          const active = tab === x.key;
          const color = active ? t.accent : t.muted;
          return (
            <Pressable key={x.key} onPress={() => setTab(x.key)} style={styles.tab} accessibilityRole="tab" accessibilityState={{ selected: active }}>
              <View>
                <Text style={[styles.glyph, { color }]}>{x.glyph}</Text>
                {x.key === 'inbox' && unseen > 0 ? (
                  <View style={[styles.badge, { backgroundColor: t.danger }]}><Text style={styles.badgeText}>{unseen > 99 ? '99+' : unseen}</Text></View>
                ) : null}
              </View>
              <Text style={[styles.tabLabel, { color }]}>{x.label}</Text>
            </Pressable>
          );
        })}
      </View>
    </View>
  );
}

export default function App() {
  const t = useTheme();
  return (
    <StoreProvider>
      {/* SafeAreaView only insets on iOS. Android draws edge-to-edge, so the status bar height is padded by hand here
          and the tab bar reserves room for the gesture pill (see styles.tabs). react-native-safe-area-context would be
          the proper fix but its native build needs Windows long paths enabled. */}
      <SafeAreaView style={{ flex: 1, backgroundColor: t.bg, paddingTop: Platform.OS === 'android' ? RNStatusBar.currentHeight ?? 24 : 0 }}>
        <StatusBar style={t.dark ? 'light' : 'dark'} />
        <Shell />
      </SafeAreaView>
    </StoreProvider>
  );
}

const styles = StyleSheet.create({
  center: { flex: 1, alignItems: 'center', justifyContent: 'center' },
  tabs: { flexDirection: 'row', borderTopWidth: 1, paddingBottom: Platform.OS === 'android' ? 22 : 6 }, // Android: clear the gesture pill
  tab: { flex: 1, alignItems: 'center', paddingVertical: 8, gap: 2 },
  glyph: { fontSize: 20, fontWeight: '700' },
  tabLabel: { fontSize: 11, fontWeight: '600' },
  badge: { position: 'absolute', top: -4, right: -14, minWidth: 18, height: 18, borderRadius: 9, alignItems: 'center', justifyContent: 'center', paddingHorizontal: 4 },
  badgeText: { color: '#fff', fontSize: 11, fontWeight: '700' },
});

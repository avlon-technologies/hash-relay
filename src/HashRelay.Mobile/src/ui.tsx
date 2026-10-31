// Small shared UI kit: theme tokens and a handful of primitives, so the screens stay readable without a UI library.
import React from 'react';
import { ActivityIndicator, Pressable, StyleSheet, Text, TextInput, TextInputProps, useColorScheme, View, ViewStyle } from 'react-native';

export function useTheme() {
  const dark = useColorScheme() === 'dark';
  return {
    dark,
    bg: dark ? '#0b1220' : '#f5f7fb',
    card: dark ? '#141c2e' : '#ffffff',
    border: dark ? '#24304a' : '#e3e8f0',
    text: dark ? '#e6ebf5' : '#0f172a',
    muted: dark ? '#8d9bb5' : '#64748b',
    accent: '#2563eb',
    accentText: '#ffffff',
    danger: '#dc2626',
    success: '#16a34a',
    warn: '#d97706',
    chip: dark ? '#1e2a44' : '#e8efff',
    chipText: dark ? '#b9caff' : '#1d4ed8',
    input: dark ? '#0f172a' : '#ffffff',
  };
}
export type Theme = ReturnType<typeof useTheme>;

export function Card({ children, style }: { children: React.ReactNode; style?: ViewStyle }) {
  const t = useTheme();
  return <View style={[styles.card, { backgroundColor: t.card, borderColor: t.border }, style]}>{children}</View>;
}

export function Title({ children }: { children: React.ReactNode }) {
  const t = useTheme();
  return <Text style={[styles.title, { color: t.text }]}>{children}</Text>;
}

export function Muted({ children, style }: { children: React.ReactNode; style?: object }) {
  const t = useTheme();
  return <Text style={[styles.muted, { color: t.muted }, style]}>{children}</Text>;
}

export function Body({ children, style }: { children: React.ReactNode; style?: object }) {
  const t = useTheme();
  return <Text style={[styles.body, { color: t.text }, style]}>{children}</Text>;
}

export function Button({ label, onPress, kind = 'primary', disabled, busy, style }: {
  label: string; onPress: () => void; kind?: 'primary' | 'secondary' | 'danger'; disabled?: boolean; busy?: boolean; style?: ViewStyle;
}) {
  const t = useTheme();
  const bg = kind === 'primary' ? t.accent : kind === 'danger' ? t.danger : 'transparent';
  const fg = kind === 'secondary' ? t.accent : t.accentText;
  return (
    <Pressable
      onPress={onPress}
      disabled={disabled || busy}
      style={({ pressed }) => [styles.button, { backgroundColor: bg, borderColor: kind === 'secondary' ? t.accent : bg, opacity: disabled || busy ? 0.5 : pressed ? 0.8 : 1 }, style]}
    >
      {busy ? <ActivityIndicator color={fg} /> : <Text style={[styles.buttonText, { color: fg }]}>{label}</Text>}
    </Pressable>
  );
}

export function Input(props: TextInputProps & { label?: string }) {
  const t = useTheme();
  const { label, style, ...rest } = props;
  return (
    <View>
      {label ? <Text style={[styles.label, { color: t.muted }]}>{label}</Text> : null}
      <TextInput
        placeholderTextColor={t.muted}
        {...rest}
        style={[styles.input, { color: t.text, backgroundColor: t.input, borderColor: t.border }, style]}
      />
    </View>
  );
}

export function Chip({ label, onRemove, onPress }: { label: string; onRemove?: () => void; onPress?: () => void }) {
  const t = useTheme();
  return (
    <Pressable onPress={onPress} style={[styles.chip, { backgroundColor: t.chip }]}>
      <Text style={[styles.chipText, { color: t.chipText }]}>{label}</Text>
      {onRemove ? (
        <Pressable onPress={onRemove} hitSlop={8} accessibilityLabel={`Unsubscribe from ${label}`}>
          <Text style={[styles.chipX, { color: t.chipText }]}>×</Text>
        </Pressable>
      ) : null}
    </Pressable>
  );
}

export function Banner({ text, kind, onDismiss }: { text: string; kind: 'error' | 'info' | 'success'; onDismiss?: () => void }) {
  const t = useTheme();
  const color = kind === 'error' ? t.danger : kind === 'success' ? t.success : t.accent;
  return (
    <Pressable onPress={onDismiss} style={[styles.banner, { borderColor: color, backgroundColor: t.card }]}>
      <Text style={{ color, fontSize: 14 }}>{text}</Text>
    </Pressable>
  );
}

export function Dot({ color }: { color: string }) {
  return <View style={{ width: 8, height: 8, borderRadius: 4, backgroundColor: color, marginRight: 6 }} />;
}

export const styles = StyleSheet.create({
  screen: { flex: 1 },
  content: { padding: 16, gap: 12, paddingBottom: 32 },
  card: { borderWidth: 1, borderRadius: 14, padding: 16, gap: 10 },
  title: { fontSize: 24, fontWeight: '700' },
  body: { fontSize: 16, lineHeight: 22 },
  muted: { fontSize: 13, lineHeight: 18 },
  label: { fontSize: 12, fontWeight: '600', textTransform: 'uppercase', letterSpacing: 0.5, marginBottom: 6 },
  input: { borderWidth: 1, borderRadius: 10, paddingHorizontal: 12, paddingVertical: 10, fontSize: 16 },
  button: { borderWidth: 1, borderRadius: 10, paddingVertical: 12, paddingHorizontal: 16, alignItems: 'center', justifyContent: 'center', minHeight: 46 },
  buttonText: { fontSize: 16, fontWeight: '600' },
  chip: { flexDirection: 'row', alignItems: 'center', borderRadius: 999, paddingVertical: 6, paddingLeft: 12, paddingRight: 10, gap: 6 },
  chipText: { fontSize: 15, fontWeight: '600' },
  chipX: { fontSize: 18, lineHeight: 18, fontWeight: '700' },
  banner: { borderWidth: 1, borderRadius: 10, padding: 12 },
  row: { flexDirection: 'row', alignItems: 'center', gap: 8 },
  wrap: { flexDirection: 'row', flexWrap: 'wrap', gap: 8 },
});

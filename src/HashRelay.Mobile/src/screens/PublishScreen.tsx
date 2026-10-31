// Publish a notification to hashtags from the phone. Needs the "publish" permission ticked when the device was approved.
import React, { useState } from 'react';
import { KeyboardAvoidingView, Platform, ScrollView, View } from 'react-native';
import { ApiError, PublishResult } from '../api';
import { hasScope } from '../config';
import { useStore } from '../store';
import { displayTag, parseTags, TAGS_PER_PUBLICATION } from '../tags';
import { Banner, Body, Button, Card, Chip, Input, Muted, styles, Title, useTheme } from '../ui';

type Priority = 'low' | 'normal' | 'high';

export function PublishScreen() {
  const t = useTheme();
  const { pairing, publish, subscriptions } = useStore();
  const [tags, setTags] = useState('');
  const [title, setTitle] = useState('');
  const [body, setBody] = useState('');
  const [url, setUrl] = useState('');
  const [priority, setPriority] = useState<Priority>('normal');
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<PublishResult | null>(null);
  const [problem, setProblem] = useState<string | null>(null);
  const canPublish = hasScope(pairing, 'publish');

  const send = async () => {
    const parsed = parseTags(tags);
    if (parsed.invalid.length) { setProblem(`Not a valid hashtag: ${parsed.invalid.join(', ')}.`); return; }
    if (!parsed.tags.length) { setProblem('Address at least one hashtag.'); return; }
    if (parsed.tags.length > TAGS_PER_PUBLICATION) { setProblem(`At most ${TAGS_PER_PUBLICATION} hashtags per notification.`); return; }
    if (!title.trim()) { setProblem('A title is required.'); return; }
    setBusy(true); setProblem(null); setResult(null);
    try {
      const r = await publish({ tags: parsed.tags, title: title.trim(), body: body.trim() || undefined, url: url.trim() || undefined, priority });
      setResult(r);
      setTitle(''); setBody(''); setUrl('');
    } catch (e) {
      setProblem(e instanceof ApiError ? e.detail : (e as Error).message);
    } finally { setBusy(false); }
  };

  return (
    <KeyboardAvoidingView style={[styles.screen, { backgroundColor: t.bg }]} behavior={Platform.OS === 'ios' ? 'padding' : undefined}>
      <ScrollView contentContainerStyle={styles.content} keyboardShouldPersistTaps="handled">
        <Title>Publish</Title>
        <Muted>Send a notification to everyone in {pairing?.tenant.name} subscribed to the hashtags.</Muted>

        {!canPublish ? (
          <Card>
            <Body>This phone was approved to receive notifications only.</Body>
            <Muted>To publish from here, remove the device in the dashboard, pair again and tick “publish” on the approval page. Publishing from automations should use an API key or a service account instead.</Muted>
          </Card>
        ) : null}

        <Card style={{ opacity: canPublish ? 1 : 0.5 }}>
          <Input label="Hashtags" value={tags} onChangeText={setTags} placeholder="#deploys #ops" autoCapitalize="none" autoCorrect={false} editable={canPublish} />
          {subscriptions.length ? (
            <View style={styles.wrap}>
              {subscriptions.slice(0, 8).map((s) => <Chip key={s} label={displayTag(s)} onPress={() => setTags((v) => (v.includes(s) ? v : `${v} ${displayTag(s)}`.trim()))} />)}
            </View>
          ) : null}
          <Input label="Title" value={title} onChangeText={setTitle} placeholder="Deploy finished" maxLength={120} editable={canPublish} />
          <Input label="Body (optional)" value={body} onChangeText={setBody} placeholder="v1.4.2 is live in production." maxLength={1000} multiline numberOfLines={3} style={{ minHeight: 80, textAlignVertical: 'top' }} editable={canPublish} />
          <Input label="Link (optional)" value={url} onChangeText={setUrl} placeholder="https://…" autoCapitalize="none" autoCorrect={false} keyboardType="url" editable={canPublish} />
          <Muted>Priority</Muted>
          <View style={styles.wrap}>
            {(['low', 'normal', 'high'] as Priority[]).map((p) => (
              <Button key={p} label={p} kind={priority === p ? 'primary' : 'secondary'} onPress={() => setPriority(p)} style={{ minHeight: 36, paddingVertical: 6 }} disabled={!canPublish} />
            ))}
          </View>
          <Button label="Publish" onPress={send} busy={busy} disabled={!canPublish} />
          {problem ? <Banner kind="error" text={problem} onDismiss={() => setProblem(null)} /> : null}
          {result ? (
            <Banner kind="success" onDismiss={() => setResult(null)}
              text={`${result.duplicate ? 'Already published' : 'Published'} to ${result.tags.map(displayTag).join(' ')}: ${result.matchedEndpoints} device${result.matchedEndpoints === 1 ? '' : 's'} matched, ${result.deliveredLive} live, ${result.pushed} pushed, ${result.queued} queued.`} />
          ) : null}
        </Card>
      </ScrollView>
    </KeyboardAvoidingView>
  );
}

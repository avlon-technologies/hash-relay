// Mirrors HashRelay.Contracts.Tags: "#Builds", "builds" and " #BUILDS " address the same topic.
export const TAG_MAX = 64;
export const TAGS_PER_PUBLICATION = 10;
const VALID = /^[a-z0-9][a-z0-9_-]{0,63}$/;

export function normalizeTag(raw: string): string | null {
  let t = raw.trim();
  while (t.startsWith('#')) t = t.slice(1);
  t = t.trim().toLowerCase();
  if (!t || t.length > TAG_MAX || !VALID.test(t)) return null;
  return t;
}

/** Splits free text on whitespace and commas, normalises and de-duplicates (first-seen order). */
export function parseTags(input: string): { tags: string[]; invalid: string[] } {
  const tags: string[] = [];
  const invalid: string[] = [];
  for (const part of input.split(/[\s,]+/)) {
    if (!part) continue;
    const t = normalizeTag(part);
    if (t === null) invalid.push(part);
    else if (!tags.includes(t)) tags.push(t);
  }
  return { tags, invalid };
}

export const displayTag = (tag: string) => '#' + tag;

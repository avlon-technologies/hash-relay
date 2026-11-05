#!/usr/bin/env bash
# Check HashRelay's decision record: the one script the Decisions workflow runs, and the one to run locally.
#
#   bash architecture/decisions/check.sh                       (keys fetched from $CONTINUUM_HOST, anonymously)
#   KEYS_FILE=host-keys.json bash architecture/decisions/check.sh
#
# Needs the `continuum` CLI on PATH (or CONTINUUM="node path/to/continuum.mjs"). Writes a Markdown summary to
# $GITHUB_STEP_SUMMARY when set, else to stdout. Exit: 0 PASS, 1 FAIL, 3 INDETERMINATE (nothing failed, but
# something could not be established — for example no signature could be checked).
set -uo pipefail
DIR="${DIR:-architecture/decisions}"
CLI="${CONTINUUM:-continuum}"
SUMMARY="${GITHUB_STEP_SUMMARY:-/dev/stdout}"
out="$(mktemp -d)"

keys=()
if [ -n "${KEYS_FILE:-}" ]; then
  keys=(--keys "$KEYS_FILE")
elif [ -n "${CONTINUUM_HOST:-}" ] && curl -fsS -X POST -H 'content-type: application/json' -d '{}' "${CONTINUUM_HOST%/}/v1/decisions/keys" -o "$out/host-keys.json"; then
  keys=(--keys "$out/host-keys.json")
else
  echo "::warning::the Host's public keys could not be obtained; no signature can be checked, so the record is INDETERMINATE (UNVERIFIED), not FAIL" >&2
fi

# the export must be THIS space's record: the pin names the space, which is the digest of its genesis line
space="$(node -e 'try{process.stdout.write(JSON.parse(require("fs").readFileSync(process.argv[1],"utf8")).space||"")}catch{}' "$DIR/decision-space.json")"
pin=(); [ -n "$space" ] && pin=(--pin "$space")
if [ ${#keys[@]} -gt 0 ] || ! head -n 1 "$DIR/decisions.jsonl" 2>/dev/null | grep -q 'continuum-decision-record/1'; then
  $CLI decision export --dir "$DIR" --offline "${keys[@]}" --check "$DIR/DECISIONS.md" --json > "$out/export.json" 2> "$out/export.err"; e=$?
else
  # DECISIONS.md states that the record was VERIFIED; without the keys it can only render as UNVERIFIED, so a comparison
  # would report a difference that is not there. Not comparable is INDETERMINATE, never FAIL.
  echo '{"result":"NOT COMPARED","reason":"not compared: DECISIONS.md renders a Host-signed record as VERIFIED, which cannot be established without the Host keys"}' > "$out/export.json"; e=3
fi
$CLI decision assert integrity --dir "$DIR" --offline "${keys[@]}" "${pin[@]}" --checks "$DIR/assertions.json" --json > "$out/assert.json" 2> "$out/assert.err"; a=$?
if [ -z "$space" ] && head -n 1 "$DIR/decisions.jsonl" 2>/dev/null | grep -q 'continuum-decision-record/1'; then
  echo "::error::$DIR/decisions.jsonl is Host-signed but $DIR/decision-space.json names no space: the export cannot be tied to its space" >&2; a=1
fi
$CLI decision assert conflicts --dir "$DIR" --offline "${keys[@]}" --json > "$out/conflicts.json" 2> /dev/null; c=$?

node - "$out" "$e" "$a" <<'JS' >> "$SUMMARY"
const fs = require('fs'); const [dir, e, a] = process.argv.slice(2);
const read = (f) => { try { return JSON.parse(fs.readFileSync(`${dir}/${f}`, 'utf8')); } catch { return null; } };
const err = (f) => {
  let t = ''; try { t = fs.readFileSync(`${dir}/${f}`, 'utf8').trim(); } catch { return ''; }
  try { const j = JSON.parse(t); t = String(j.reason ?? j.error ?? j.message ?? t); } catch {}
  return t.split('\n')[0];
};
const label = (code) => ({ 0: 'PASS', 1: 'FAIL', 3: 'INDETERMINATE' }[code] ?? `FAIL (exit ${code})`);
const cell = (s) => String(s ?? '').replace(/\|/g, '\\|').replace(/\n/g, ' ');
const rows = [];
const ex = read('export.json');
rows.push(['DECISIONS.md is the rendering of the record', label(e), ex
  ? (ex.result === 'MATCHES' ? `MATCHES (record ${ex.verification?.status})` : ex.first ? `DIFFERS at line ${ex.first.line}: DECISIONS.md has "${ex.first.file}", the record renders "${ex.first.record}"` : (ex.reason ?? ex.error ?? 'DIFFERS'))
  : err('export.err') || 'DECISIONS.md missing or unreadable']);
const as = read('assert.json');
for (const r of as?.results ?? []) rows.push([r.check, r.result, r.note]);
if (!as?.results) rows.push(['assertions.json', label(a), err('assert.err') || 'no output']);
const cf = read('conflicts.json')?.results?.[0];
if (cf) rows.push(['conflicts (informational, never fails the job)', cf.result, `${cf.coverage ? `${cf.coverage.structured} of ${cf.coverage.inForce} decision(s) in force state an ADL expectation; ` : ''}${cf.note}`]);
console.log('## Decision record\n\n| Check | Result | Detail |\n|---|---|---|');
for (const [k, r, n] of rows) {
  console.log(`| ${cell(k)} | **${r}** | ${cell(n)} |`);
  if (r !== 'PASS' && !k.startsWith('conflicts')) console.error(`::${r.startsWith('FAIL') ? 'error' : 'warning'}::${k}: ${r} — ${n}`);
}
JS

worst=0
for x in "$e" "$a"; do
  if [ "$x" = 3 ]; then [ "$worst" = 0 ] && worst=3; elif [ "$x" != 0 ]; then worst=1; fi
done
case "$worst" in
  0) echo "decisions: PASS" ;;
  1) echo "decisions: FAIL — see the summary" ;;
  3) echo "decisions: INDETERMINATE — nothing failed, but not everything could be established (see the summary)" ;;
esac
rm -rf "$out"
exit "$worst"

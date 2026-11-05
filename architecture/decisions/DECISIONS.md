# Decision history

> A rendering of the decision record `decisions.jsonl`, produced by continuum-cli 0.26.0. It is not the authority: the record is. Each statement copied from the record cites its event as [eN]; the chronology at the end gives every event's line digest. `continuum decision export --check <this file>` re-verifies the record and confirms that this file is its complete rendering.

## Verification

**VERIFIED** — 1 Host-signed event(s); every line canonical, chained and signed (ES256) by key(s) decisions-dev-2026-11-04

| | |
|---|---|
| Record | `decisions.jsonl`, sha256:feba92b040ff0f0362275c731a99955c6681c85f0b2b7d6e5b1da6c344623184 |
| Profile | continuum-decision-record/1: each act authorized and signed (ES256) by the Host |
| Checked | canonical lines, chain, space and every ES256 signature against key(s) decisions-dev-2026-11-04, by continuum-cli 0.26.0 |
| Events | 1 (e1–e1) |
| Head | sha256:e95ec7a36a31218578c0047e52ae08ee4c43e28549e6ef58287a61573b9f76c0 (e1) |
| Decisions | 0 |
| Authority policy | none named |
| Problems | none |

## How to read this

- **Recorded:** wording, reasons, actors, grants, times and model states are copied from the acts, each cited [eN].
- **Derived:** standing, lines of replacement and structured conflicts are computed by continuum-cli from the recorded acts. Each is labelled *(derived)* and names the events it rests on.
- **Not established by this record:** whether any implementation follows a decision, or whether the model conforms. Evidence is the reporter's claim. A challenge's standing is what was derived when it was recorded.
- A decision binds only while IN FORCE: accepted and not since superseded, revoked or rejected. A pending decision binds nothing, however recent.

## Actors

| Actor | Identity as recorded | Acts | First |
|---|---|---|---|
| Michael Kang | Michael Kang (Host-verified: https://login.microsoftonline.com/f7ef9e19-20fa-47ac-91dd-47890978afe8/v2.0 oid 2605c7a8-5120-4edb-98cc-76d563601c0f) | 1 | [e1] |

## Current standing (derived)

Derived from the accept, reject, revoke and supersede acts up to e1.

| Decision | Title | Standing | Since | Detail |
|---|---|---|---|---|

In force: none.

## Unresolved or conflicting authority (derived)

- No structured contradiction among the 0 decision(s) in force that state ADL expectations.
- Compared by their ADL expectations: none. Not compared, because they state no ADL expectation: none.
- **Prose is not compared.** Continuum does not read decision wording for contradictions. Two decisions in force that say opposite things in words are both listed above as IN FORCE: compare their current wording below.
- Pending, binding nothing until accepted: none.
- Challenges with no disposition: none.

## Decisions

## The record itself

- [e1] 2026-11-05T05:39:32.439Z **Policy governed** by Michael Kang (grant `owner`)
  - Policy: 1 principal(s), 2 grant(s) (`owner`: self may govern, propose, revise, amend, accept, reject, revoke, supersede, check, evidence; `agent-architect`: agent:claude-code may propose, amend, check, evidence); Stage 9: HashRelay's architectural decisions are governed from the Host-kept record

## Chronology

Every event in record order, with the sha256 of its line.

| Event | Time | Act | Decision | Actor | Line digest |
|---|---|---|---|---|---|
| e1 | 2026-11-05T05:39:32.439Z | governed | — | Michael Kang | sha256:e95ec7a36a31218578c0047e52ae08ee4c43e28549e6ef58287a61573b9f76c0 |


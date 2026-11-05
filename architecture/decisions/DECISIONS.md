# Decision history

> A rendering of the decision record `decisions.jsonl`, produced by continuum-cli 0.26.0. It is not the authority: the record is. Each statement copied from the record cites its event as [eN]; the chronology at the end gives every event's line digest. `continuum decision export --check <this file>` re-verifies the record and confirms that this file is its complete rendering.

## Verification

**VERIFIED** — 7 Host-signed event(s); every line canonical, chained and signed (ES256) by key(s) decisions-dev-2026-11-04

| | |
|---|---|
| Record | `decisions.jsonl`, sha256:9bbb66ae287905a95311161336d112c4a2d1b1f5fde96a094e012974e77b70de |
| Profile | continuum-decision-record/1: each act authorized and signed (ES256) by the Host |
| Checked | canonical lines, chain, space and every ES256 signature against key(s) decisions-dev-2026-11-04, by continuum-cli 0.26.0 |
| Events | 7 (e1–e7) |
| Head | sha256:31d2466082046fa4764bde1a407fb83418704ecfd4a70506d6942456daf6c656 (e7) |
| Decisions | 1 |
| Authority policy | Host policy sha256:9792b32aab7d1fa86b22854f484aaeefe50d78d7187a754a7a9fd2549ca88a08 |
| Problems | none |

## How to read this

- **Recorded:** wording, reasons, actors, grants, times and model states are copied from the acts, each cited [eN].
- **Derived:** standing, lines of replacement and structured conflicts are computed by continuum-cli from the recorded acts. Each is labelled *(derived)* and names the events it rests on.
- **Not established by this record:** whether any implementation follows a decision, or whether the model conforms. Evidence is the reporter's claim. A challenge's standing is what was derived when it was recorded.
- A decision binds only while IN FORCE: accepted and not since superseded, revoked or rejected. A pending decision binds nothing, however recent.

## Actors

| Actor | Identity as recorded | Acts | First |
|---|---|---|---|
| Michael Kang | Michael Kang (Host-verified: https://login.microsoftonline.com/f7ef9e19-20fa-47ac-91dd-47890978afe8/v2.0 oid 2605c7a8-5120-4edb-98cc-76d563601c0f) | 2 | [e1] |
| agent:claude-code for Michael Kang | agent:claude-code acting for Michael Kang (Host-verified: https://login.microsoftonline.com/f7ef9e19-20fa-47ac-91dd-47890978afe8/v2.0 oid 2605c7a8-5120-4edb-98cc-76d563601c0f) under Host delegation 701cafdf-e357-423f-bf4b-d527b1c6ab0e | 5 | [e2] |

## Current standing (derived)

Derived from the accept, reject, revoke and supersede acts up to e7.

| Decision | Title | Standing | Since | Detail |
|---|---|---|---|---|
| ADR-001 | Offline deliveries are kept for a bounded replay window | IN FORCE | [e5] | option bounded-replay-window |

In force: ADR-001.

## Unresolved or conflicting authority (derived)

- No structured contradiction among the 1 decision(s) in force that state ADL expectations.
- Compared by their ADL expectations: ADR-001. Not compared, because they state no ADL expectation: none.
- **Prose is not compared.** Continuum does not read decision wording for contradictions. Two decisions in force that say opposite things in words are both listed above as IN FORCE: compare their current wording below.
- Pending, binding nothing until accepted: none.
- Challenges with no disposition: none.

## Decisions

### ADR-001 — Offline deliveries are kept for a bounded replay window

Standing *(derived)*: **IN FORCE** [e5].

- [e2] 2026-11-05T05:43:37.810Z **Proposed** by agent:claude-code for Michael Kang (grant `agent-architect`, delegated by `owner`)
  - Title: Offline deliveries are kept for a bounded replay window
  - Decision (as proposed): "A delivery queued for an offline endpoint is retained until it is acknowledged or until its replay window (ReplayWindowHours, 24 by default) elapses, whichever comes first. An expired delivery is recorded as expired and is never counted as delivered."
  - Rationale: The model says queued deliveries are 'retained until acknowledged', but the code expires them after ReplayWindowHours, and only when the endpoint reconnects (RoutingService.ReplayAsync). Expired deliveries are then counted as delivered in RecentAsync, DuplicateAsync, the notification page and the dashboard. docs/ARCHITECTURE.md lists offline retention as unresolved. Unbounded retention has no upper limit on storage for a device that never returns, so the bound is made explicit and the reporting is made honest.
  - Alternatives considered:
    - Retain until acknowledged, with no bound (what the model says today): storage grows without limit for endpoints that never return
    - A per-workspace replay window: more configuration than any current workspace needs
  - Consequences:
    - Queued deliveries expire on a schedule, not only when the endpoint reconnects
    - Activity, duplicate and notification views stop counting expired deliveries as delivered
  - Risks:
    - A device offline for longer than the window loses those notifications; it is told they expired, not that they were delivered
  - Option `bounded-replay-window`: change ../changes/2026-11-04-bounded-offline-retention.adl-change — applied; A delivery held for an offline endpoint is kept for a bounded replay window, after which it expires whether or not the endpoint returns; an expired delivery is reported as expired and never as delivered.
  - Scope:
    - ::HashRelay.Notifications.HashtagDelivery (Capability)
  - Expectations:
    - exists ::HashRelay.Notifications.HashtagDelivery.BoundedOfflineRetention
    - ::HashRelay.Notifications.Delivery.DeliveryService satisfies ::HashRelay.Notifications.HashtagDelivery.BoundedOfflineRetention
  - Bound to model Avlon.HashRelay sha256:c2824b8808af7698b9fffaf5810d7b4083593ad3f79d5778c98853a360feb405
- [e3] 2026-11-05T05:43:47.538Z **Checked** by agent:claude-code for Michael Kang (grant `agent-architect`, delegated by `owner`)
  - Mechanical: passed — the model compiles to sha256:c2824b8808af7698b9fffaf5810d7b4083593ad3f79d5778c98853a360feb405
  - Alignment (decision → model, no source code read): change-required — 0 hold, 0 contradicted by a declaration, 2 missing from the model, 0 not evaluable
    - `exists ::HashRelay.Notifications.HashtagDelivery.BoundedOfflineRetention`: missing
    - `::HashRelay.Notifications.Delivery.DeliveryService satisfies ::HashRelay.Notifications.HashtagDelivery.BoundedOfflineRetention`: missing
  - Assessor: @avlon/continuum-cli 0.26.0
  - Bound to model Avlon.HashRelay sha256:c2824b8808af7698b9fffaf5810d7b4083593ad3f79d5778c98853a360feb405
- [e4] 2026-11-05T05:43:50.828Z **Checked** by agent:claude-code for Michael Kang (grant `agent-architect`, delegated by `owner`)
  - Mechanical: passed — the model compiles to sha256:c2824b8808af7698b9fffaf5810d7b4083593ad3f79d5778c98853a360feb405
  - Alignment (decision → model, no source code read): change-required — 0 hold, 0 contradicted by a declaration, 2 missing from the model, 0 not evaluable
    - `exists ::HashRelay.Notifications.HashtagDelivery.BoundedOfflineRetention`: missing
    - `::HashRelay.Notifications.Delivery.DeliveryService satisfies ::HashRelay.Notifications.HashtagDelivery.BoundedOfflineRetention`: missing
  - Assessor: @avlon/continuum-cli 0.26.0
  - Bound to model Avlon.HashRelay sha256:c2824b8808af7698b9fffaf5810d7b4083593ad3f79d5778c98853a360feb405
- [e5] 2026-11-05T05:44:50.339Z **Accepted** by Michael Kang (grant `owner`, separation-of-duties rule a-person-who-did-not-author-it-approves)
  - Reason: Bounded retention, with expired reported as expired
  - Option accepted: `bounded-replay-window`
  - Bound to model Avlon.HashRelay sha256:c2824b8808af7698b9fffaf5810d7b4083593ad3f79d5778c98853a360feb405
- [e6] 2026-11-05T05:45:36.068Z **Checked** by agent:claude-code for Michael Kang (grant `agent-architect`, delegated by `owner`)
  - Mechanical: passed — the model compiles to sha256:a89061e4c049377dbe06faf4e79fe9fd6fd97fff1e3d2281a50cc26bddc13f7f
  - Alignment (decision → model, no source code read): aligned — all 4 expectation(s) hold
    - `exists ::HashRelay.Notifications.HashtagDelivery.BoundedOfflineRetention`: satisfied
    - `::HashRelay.Notifications.Delivery.DeliveryService satisfies ::HashRelay.Notifications.HashtagDelivery.BoundedOfflineRetention`: satisfied
    - `exists ::HashRelay.Notifications.HashtagDelivery.BoundedOfflineRetention`: satisfied
    - `::HashRelay.Notifications.Delivery.DeliveryService satisfies ::HashRelay.Notifications.HashtagDelivery.BoundedOfflineRetention`: satisfied
  - Assessor: @avlon/continuum-cli 0.26.0
  - Bound to model Avlon.HashRelay sha256:a89061e4c049377dbe06faf4e79fe9fd6fd97fff1e3d2281a50cc26bddc13f7f
- [e7] 2026-11-05T05:45:45.504Z **Checked** by agent:claude-code for Michael Kang (grant `agent-architect`, delegated by `owner`)
  - Mechanical: passed — the model compiles to sha256:a89061e4c049377dbe06faf4e79fe9fd6fd97fff1e3d2281a50cc26bddc13f7f
  - Alignment (decision → model, no source code read): aligned — all 4 expectation(s) hold
    - `exists ::HashRelay.Notifications.HashtagDelivery.BoundedOfflineRetention`: satisfied
    - `::HashRelay.Notifications.Delivery.DeliveryService satisfies ::HashRelay.Notifications.HashtagDelivery.BoundedOfflineRetention`: satisfied
    - `exists ::HashRelay.Notifications.HashtagDelivery.BoundedOfflineRetention`: satisfied
    - `::HashRelay.Notifications.Delivery.DeliveryService satisfies ::HashRelay.Notifications.HashtagDelivery.BoundedOfflineRetention`: satisfied
  - Assessor: @avlon/continuum-cli 0.26.0
  - Bound to model Avlon.HashRelay sha256:a89061e4c049377dbe06faf4e79fe9fd6fd97fff1e3d2281a50cc26bddc13f7f

Current wording *(derived from [e2])*: "A delivery queued for an offline endpoint is retained until it is acknowledged or until its replay window (ReplayWindowHours, 24 by default) elapses, whichever comes first. An expired delivery is recorded as expired and is never counted as delivered."
Expectations in effect *(derived)*: `exists ::HashRelay.Notifications.HashtagDelivery.BoundedOfflineRetention`, `::HashRelay.Notifications.Delivery.DeliveryService satisfies ::HashRelay.Notifications.HashtagDelivery.BoundedOfflineRetention`, `exists ::HashRelay.Notifications.HashtagDelivery.BoundedOfflineRetention`, `::HashRelay.Notifications.Delivery.DeliveryService satisfies ::HashRelay.Notifications.HashtagDelivery.BoundedOfflineRetention`

## The record itself

- [e1] 2026-11-05T05:39:32.439Z **Policy governed** by Michael Kang (grant `owner`)
  - Policy: 1 principal(s), 2 grant(s) (`owner`: self may govern, propose, revise, amend, accept, reject, revoke, supersede, check, evidence; `agent-architect`: agent:claude-code may propose, amend, check, evidence); Stage 9: HashRelay's architectural decisions are governed from the Host-kept record

## Chronology

Every event in record order, with the sha256 of its line.

| Event | Time | Act | Decision | Actor | Line digest |
|---|---|---|---|---|---|
| e1 | 2026-11-05T05:39:32.439Z | governed | — | Michael Kang | sha256:e95ec7a36a31218578c0047e52ae08ee4c43e28549e6ef58287a61573b9f76c0 |
| e2 | 2026-11-05T05:43:37.810Z | proposed | ADR-001 | agent:claude-code for Michael Kang | sha256:cd3e238ed410e25495c905c497535d0c937ab4b4431ad18d4d4fceeeb184cd71 |
| e3 | 2026-11-05T05:43:47.538Z | checked | ADR-001 | agent:claude-code for Michael Kang | sha256:dbbbe5d57e91cde5fecea5b81ac5b4c388af9c88e14e68930b9ad323a1929a27 |
| e4 | 2026-11-05T05:43:50.828Z | checked | ADR-001 | agent:claude-code for Michael Kang | sha256:b2df9874b828be0d8b5845601e67031587f337d126e6da6926bfedb0b2bea6a8 |
| e5 | 2026-11-05T05:44:50.339Z | accepted | ADR-001 | Michael Kang | sha256:c3f95a540b0a66fdf14ed90c91875c941458096db71d4dd0362a821601e1de80 |
| e6 | 2026-11-05T05:45:36.068Z | checked | ADR-001 | agent:claude-code for Michael Kang | sha256:b93a49d6758880159f0667618004caa4c85d23b6375f774e12f4beba86630dac |
| e7 | 2026-11-05T05:45:45.504Z | checked | ADR-001 | agent:claude-code for Michael Kang | sha256:31d2466082046fa4764bde1a407fb83418704ecfd4a70506d6942456daf6c656 |


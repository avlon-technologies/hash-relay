# Decision history

> A rendering of the decision record `decisions.jsonl`, produced by continuum-cli 0.28.1. It is not the authority: the record is. Each statement copied from the record cites its event as [eN]; the chronology at the end gives every event's line digest. `continuum decision export --check <this file>` re-verifies the record and confirms that this file is its complete rendering.

## Verification

**VERIFIED** — 15 Host-signed event(s); every line canonical, chained and signed (ES256) by key(s) decisions-dev-2026-11-04

| | |
|---|---|
| Record | `decisions.jsonl`, sha256:afaac1698326102d17776f95566a6d7b8a3f0b87cb3ede267c247b9a6eb18a4b |
| Profile | continuum-decision-record/1: each act authorized and signed (ES256) by the Host |
| Checked | canonical lines, chain, space and every ES256 signature against key(s) decisions-dev-2026-11-04, by continuum-cli 0.28.1 |
| Events | 15 (e1–e15) |
| Head | sha256:d50b025314dd25edb399d4f6be23e4e2d23fd5591fbda41f867e765fefd6efba (e15) |
| Decisions | 2 |
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
| Michael Kang | Michael Kang (Host-verified: https://login.microsoftonline.com/f7ef9e19-20fa-47ac-91dd-47890978afe8/v2.0 oid 2605c7a8-5120-4edb-98cc-76d563601c0f) | 7 | [e1] |
| agent:claude-code for Michael Kang | agent:claude-code acting for Michael Kang (Host-verified: https://login.microsoftonline.com/f7ef9e19-20fa-47ac-91dd-47890978afe8/v2.0 oid 2605c7a8-5120-4edb-98cc-76d563601c0f) under Host delegation 701cafdf-e357-423f-bf4b-d527b1c6ab0e | 8 | [e2] |

## Current standing (derived)

Derived from the accept, reject, revoke and supersede acts up to e15.

| Decision | Title | Standing | Since | Detail |
|---|---|---|---|---|
| ADR-001 | Offline deliveries are kept for a bounded replay window | IN FORCE | [e5] | option bounded-replay-window |
| ADR-002 | Native notifications are shown only by a per-user desktop agent | IN FORCE | [e12] |  |

In force: ADR-001, ADR-002.

## Unresolved or conflicting authority (derived)

- No structured contradiction among the 2 decision(s) in force that state ADL expectations.
- Compared by their ADL expectations: ADR-001, ADR-002. Not compared, because they state no ADL expectation: none.
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
  - Assessor: @avlon/continuum-cli 0.26.0
  - Bound to model Avlon.HashRelay sha256:a89061e4c049377dbe06faf4e79fe9fd6fd97fff1e3d2281a50cc26bddc13f7f
- [e7] 2026-11-05T05:45:45.504Z **Checked** by agent:claude-code for Michael Kang (grant `agent-architect`, delegated by `owner`)
  - Mechanical: passed — the model compiles to sha256:a89061e4c049377dbe06faf4e79fe9fd6fd97fff1e3d2281a50cc26bddc13f7f
  - Alignment (decision → model, no source code read): aligned — all 4 expectation(s) hold
    - `exists ::HashRelay.Notifications.HashtagDelivery.BoundedOfflineRetention`: satisfied
    - `::HashRelay.Notifications.Delivery.DeliveryService satisfies ::HashRelay.Notifications.HashtagDelivery.BoundedOfflineRetention`: satisfied
  - Assessor: @avlon/continuum-cli 0.26.0
  - Bound to model Avlon.HashRelay sha256:a89061e4c049377dbe06faf4e79fe9fd6fd97fff1e3d2281a50cc26bddc13f7f
- [e8] 2026-11-05T05:52:45.735Z **Evidence** by agent:claude-code for Michael Kang (grant `agent-architect`, delegated by `owner`)
  - Implementation evidence, REPORTED (not verified by Continuum): established
  - Method: regression tests written first (2), full suite 167/167, guard PASS, model Apply EQUIVALENT
  - Class: reported by the actor the Host recorded — not verified by Continuum
  - Evidence: {"digest":"sha256:ea5fb101fdc18f4e49c2096b90c11ac1c2270caedc5e5355bfd076695034e2d0","path":"evidence/ADR-001-implementation.json"}
- [e9] 2026-11-05T08:03:45.344Z **Checked** by Michael Kang (grant `owner`)
  - Mechanical: passed — the model compiles to sha256:a89061e4c049377dbe06faf4e79fe9fd6fd97fff1e3d2281a50cc26bddc13f7f
  - Alignment (decision → model, no source code read): aligned — all 4 expectation(s) hold
    - `exists ::HashRelay.Notifications.HashtagDelivery.BoundedOfflineRetention`: satisfied
    - `::HashRelay.Notifications.Delivery.DeliveryService satisfies ::HashRelay.Notifications.HashtagDelivery.BoundedOfflineRetention`: satisfied
  - Assessor: @avlon/continuum-cli 0.27.1
  - Bound to model Avlon.HashRelay sha256:a89061e4c049377dbe06faf4e79fe9fd6fd97fff1e3d2281a50cc26bddc13f7f

Current wording *(derived from [e2])*: "A delivery queued for an offline endpoint is retained until it is acknowledged or until its replay window (ReplayWindowHours, 24 by default) elapses, whichever comes first. An expired delivery is recorded as expired and is never counted as delivered."
Expectations in effect *(derived)*: `exists ::HashRelay.Notifications.HashtagDelivery.BoundedOfflineRetention`, `::HashRelay.Notifications.Delivery.DeliveryService satisfies ::HashRelay.Notifications.HashtagDelivery.BoundedOfflineRetention`

### ADR-002 — Native notifications are shown only by a per-user desktop agent

Standing *(derived)*: **IN FORCE** [e12].

- [e10] 2026-11-05T09:51:14.685Z **Proposed** by agent:claude-code for Michael Kang (grant `agent-architect`, delegated by `owner`)
  - Title: Native notifications are shown only by a per-user desktop agent
  - Decision (as proposed): "On desktop, notifications are displayed only by DesktopAgent running in the signed-in user's session, through the platform notification center (on Windows, the Windows App SDK AppNotificationManager). No service, session-0 or server process renders user notifications. The Windows agent ships as a self-contained x64 build in a per-user Inno Setup installer that needs no admin rights."
  - Rationale: Windows services run in session 0 and cannot show notifications in a user's session; native toasts need a process in that session with its own app identity. A per-user install keeps the agent with the user who paired it and avoids elevation. Recorded originally as docs/adr/0001-windows-first.md (2026-10-31), outside the decision record; this brings it in, without its offline-delivery clause, which ADR-001 replaces.
  - Alternatives considered:
    - A Windows service that renders notifications: cannot reach the user session from session 0
    - A per-machine MSI install: needs admin rights and does not map an install to one user
    - Framework-dependent build: requires a matching .NET runtime on every machine
  - Consequences:
    - Notifications appear only while the user is signed in and the agent is running
    - Each Windows user who wants notifications installs and pairs their own agent
  - Risks:
    - A self-contained build is large and must be re-shipped for every .NET security patch
  - Scope:
    - ::HashRelay.DesktopAgent (Deployable)
  - Expectations:
    - exists ::HashRelay.DesktopAgent
    - exists ::HashRelay.DesktopNotificationCenter
  - Bound to model Avlon.HashRelay sha256:a89061e4c049377dbe06faf4e79fe9fd6fd97fff1e3d2281a50cc26bddc13f7f
- [e11] 2026-11-05T09:51:20.981Z **Checked** by agent:claude-code for Michael Kang (grant `agent-architect`, delegated by `owner`)
  - Mechanical: passed — the model compiles to sha256:a89061e4c049377dbe06faf4e79fe9fd6fd97fff1e3d2281a50cc26bddc13f7f
  - Alignment (decision → model, no source code read): aligned — all 2 expectation(s) hold
    - `exists ::HashRelay.DesktopAgent`: satisfied
    - `exists ::HashRelay.DesktopNotificationCenter`: satisfied
  - Assessor: @avlon/continuum-cli 0.28.1
  - Bound to model Avlon.HashRelay sha256:a89061e4c049377dbe06faf4e79fe9fd6fd97fff1e3d2281a50cc26bddc13f7f
- [e12] 2026-11-05T09:53:06.235Z **Accepted** by Michael Kang (grant `owner`, separation-of-duties rule a-person-who-did-not-author-it-approves)
  - Reason: Desktop notifications stay in a per-user session agent; brings docs/adr/0001 into the record
  - Bound to model Avlon.HashRelay sha256:a89061e4c049377dbe06faf4e79fe9fd6fd97fff1e3d2281a50cc26bddc13f7f
- [e13] 2026-11-05T09:53:20.134Z **Checked** by Michael Kang (grant `owner`)
  - Mechanical: passed — the model compiles to sha256:a89061e4c049377dbe06faf4e79fe9fd6fd97fff1e3d2281a50cc26bddc13f7f
  - Alignment (decision → model, no source code read): aligned — all 2 expectation(s) hold
    - `exists ::HashRelay.DesktopAgent`: satisfied
    - `exists ::HashRelay.DesktopNotificationCenter`: satisfied
  - Assessor: @avlon/continuum-cli 0.28.1
  - Bound to model Avlon.HashRelay sha256:a89061e4c049377dbe06faf4e79fe9fd6fd97fff1e3d2281a50cc26bddc13f7f
- [e14] 2026-11-05T09:56:25.791Z **Checked** by Michael Kang (grant `owner`)
  - Mechanical: passed — the model compiles to sha256:a89061e4c049377dbe06faf4e79fe9fd6fd97fff1e3d2281a50cc26bddc13f7f
  - Alignment (decision → model, no source code read): aligned — all 2 expectation(s) hold
    - `exists ::HashRelay.DesktopAgent`: satisfied
    - `exists ::HashRelay.DesktopNotificationCenter`: satisfied
  - Assessor: @avlon/continuum-cli 0.28.1
  - Bound to model Avlon.HashRelay sha256:a89061e4c049377dbe06faf4e79fe9fd6fd97fff1e3d2281a50cc26bddc13f7f
- [e15] 2026-11-05T10:00:16.004Z **Evidence** by Michael Kang (grant `owner`)
  - Implementation evidence, REPORTED (not verified by Continuum): established
  - Method: code inspection: toasts only in HashRelay.Agent (WinExe, user session); win-x64 self-contained (build.ps1:65); Inno PrivilegesRequired=lowest, {localappdata}. DEVIATION: uses Microsoft.Toolkit.Uwp.Notifications, not AppNotificationManager as worded
  - Class: reported by the actor the Host recorded — not verified by Continuum
  - Evidence: {"digest":"sha256:28aa180d99f9bd1a7dfc82b2f0a8f6b32239a7de4edfe48c547e52b7d9fef9b0","path":"evidence/ADR-002-implementation.json"}

Current wording *(derived from [e10])*: "On desktop, notifications are displayed only by DesktopAgent running in the signed-in user's session, through the platform notification center (on Windows, the Windows App SDK AppNotificationManager). No service, session-0 or server process renders user notifications. The Windows agent ships as a self-contained x64 build in a per-user Inno Setup installer that needs no admin rights."
Expectations in effect *(derived)*: `exists ::HashRelay.DesktopAgent`, `exists ::HashRelay.DesktopNotificationCenter`

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
| e8 | 2026-11-05T05:52:45.735Z | evidenced | ADR-001 | agent:claude-code for Michael Kang | sha256:f36306c40c9d6e720b22f048a5ecf535568ecc164a824a00b0c708a7cb0fda83 |
| e9 | 2026-11-05T08:03:45.344Z | checked | ADR-001 | Michael Kang | sha256:db19430f7d9e6b3f89dec179a7f926fb855dace68dac91ca7771bae6c7fa4173 |
| e10 | 2026-11-05T09:51:14.685Z | proposed | ADR-002 | agent:claude-code for Michael Kang | sha256:29a7e6d396bad5b6299d95f4d5409d18ee564df3cadd5b34b29fcc07c62a079e |
| e11 | 2026-11-05T09:51:20.981Z | checked | ADR-002 | agent:claude-code for Michael Kang | sha256:bc83b9862ce3b793205067f602824e62f0ea8f9f6f8aea62042952c28ee202b5 |
| e12 | 2026-11-05T09:53:06.235Z | accepted | ADR-002 | Michael Kang | sha256:8178494cb14d8b2fe385ec355ee86aae4efd885ad5992b196c6f0d080d050363 |
| e13 | 2026-11-05T09:53:20.134Z | checked | ADR-002 | Michael Kang | sha256:1118ee49b29b97006f3c5b539f22d987f29905a0ada7d9234fb4e0cb6c2ac876 |
| e14 | 2026-11-05T09:56:25.791Z | checked | ADR-002 | Michael Kang | sha256:f90b70d1a4434a99d10e8ededc12eae105ad2d8518eb1ca7fada03c4be409375 |
| e15 | 2026-11-05T10:00:16.004Z | evidenced | ADR-002 | Michael Kang | sha256:d50b025314dd25edb399d4f6be23e4e2d23fd5591fbda41f867e765fefd6efba |


# HashRelay architecture — initial draft

## Authority and state
The ADL package at ../architecture/model is a compile-valid **draft** architectural model, not an accepted or ratified baseline. This document contains proposed technology decisions and product contracts. Do not infer that the ADL formally declares these bindings.

## Logical boundaries
- **Routing** owns publication authorization, workspace scope, hashtag subscriptions, and matching.
- **Delivery** owns endpoint registration, transport selection, fan-out and endpoint-level idempotency.
- Interfaces: application SDK, HTTPS Web Service API, CLI, MCP server, installable desktop agent, and mobile SDK.
- External integrations: Windows/macOS/Linux native notification centers, APNs and FCM.

## Transport decision: SignalR (proposed)
Use ASP.NET Core SignalR for connected desktop agents; negotiate and authenticate a connection, associate it with a stable endpoint ID, and rebuild any ephemeral routing state after reconnect. Persist subscriptions and delivery records separately. Do not use SignalR groups as the subscription database or a durable queue. Multiple server instances will require a scale-out strategy if adopted later.

Use APNs for iOS background push and FCM for Android background push. An app's provider credentials and push tokens are scoped to that app; the HashRelay first-party companion app has its own identity. Device tokens are secrets and rotate. A foreground mobile SDK may use SignalR where useful; it cannot rely on permanent background sockets.

## Delivery semantics (proposed)
- Publication has a client-provided idempotency key, workspace identity, tags, title and body.
- Match the union of subscribers for the tags, not a concatenation of per-tag results.
- Snapshot target endpoints and deduplicate by (notification ID, endpoint ID).
- Retry transient transport errors with bounded backoff and expiry; permanent token errors disable endpoints.
- Track accepted / queued / provider-accepted / failed separately; delivered-to-device or displayed cannot be universally proven.
- Access controls for publishing and subscribing are explicit; tenant-wide public tags are not assumed.

## Important unresolved design choices
Subscription ownership (user vs installation), notification payload/privacy limits, identity provider, offline retention, delivery SLA, application credential onboarding and push credential custody, rate limits, opt-out and deletion controls, and whether mobile notification history is retained are not yet decided.

## Continuum verification
Compiled with Continuum ADL core adl/1.4, compiler adlc 0.2.0; 0 errors, 6 review warnings (missing ownership, deployment mappings and verification). Last attestation: `sha256:f2ce446b35e77d5a7aae1bed4715ac80f0f61e4fd6b9fd75d0cf7c78047012dd`.

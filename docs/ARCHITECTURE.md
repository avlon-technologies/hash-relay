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


## Multi-tenancy and developer onboarding (2026-10-31)

**Confirmed product requirement:** a person signs up, enters a dashboard, operates within a tenant workspace, and obtains an API key scoped to that tenant. All routing, subscriptions, endpoints and notifications are tenant-isolated. An account may belong to multiple tenants; tenant selection is explicit.

### Proposed onboarding journey
1. Sign up or sign in as an AccountHolder (exact identity provider deferred).
2. Create a tenant workspace or accept an invitation to an existing workspace.
3. Select the tenant in the dashboard.
4. Create an API key with named purpose and tenant-local permissions; display the secret only at creation.
5. Use the key from a trusted publisher (backend, CLI or MCP integration) to publish to hashtags in that tenant.

### Trust and authorization rules
- Never trust a client-supplied `tenantId` as authorization. Resolve the effective tenant from a verified user session plus membership, or from a validated API key.
- Every persistent record (subscription, publication, endpoint, delivery and audit) is tenant-owned. Filter queries by verified tenant context, enforce uniqueness within that scope, and validate cross-tenant references.
- API keys are bound to exactly one tenant. Persist only a secure verifier/hash, a stable key identifier, tenant ownership, scopes, creator, expiry/revocation state and audit metadata. Never store or log full plaintext API keys.
- Key issuance/rotation/revocation requires an authorized tenant role. Prefer separate keys per integration and limited permission scopes; avoid putting publisher secrets into distributable desktop or mobile applications.
- Native desktop/mobile subscribers authenticate using a user/session or installation identity and appropriate subscriber permissions; they do not embed the tenant's server-side publisher API key.
- HashRelay SignalR connections must be authenticated to an endpoint and tenant; group names should be tenant-qualified, but groups are never the authorization authority or source of persisted subscriptions.
- Tenant data leakage tests are release-blocking. Include cross-tenant publish, subscribe, endpoint registration, data read, realtime connection and key revocation tests.

### Scope boundary
The ADL now contains an Access domain, TenantIdentity bounded context, AccountHolder actor, CreateTenant and IssueApiKey commands, and tenant/key stores. Detailed dashboard UI, user invitations and roles, tenant-switch semantics, registration flow, API-key renewal, and infrastructure identity/binding remain to be authored. Compilation does not ratify any of these product decisions.

## Diagrams committed with documentation

- [Product vision](diagrams/hashrelay-vision.svg) — high-level publisher → routing → delivery → devices narrative.
- [Notification destinations](diagrams/hashrelay-delivery.svg) — desktop and future mobile endpoints, with proposed transport links distinguished.

These SVGs were rendered with Continuum Diagram from an **earlier** ADL revision, attestation `sha256:f2ce446b35e77d5a7aae1bed4715ac80f0f61e4fd6b9fd75d0cf7c78047012dd`. The current Windows-first model attestation is `sha256:15001b83acfbb18c92baf0a9a945592ef66015ec5205b6cb2168589419254714`. Regenerate before using the figures as a current-model architecture baseline. They are illustrative vision material, not an implementation verification.

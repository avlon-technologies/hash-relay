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

**Decided 2026-10-31:** Firebase Cloud Messaging is the single push provider for mobile endpoints on both platforms; iOS is reached through Firebase's APNs relay (the project's APNs key lives in Firebase), so HashRelay holds no APNs connection of its own. An app's provider credentials and push tokens are scoped to that app; the HashRelay first-party app has its own Firebase identity. Device tokens are secrets and rotate; the app re-registers its token on every connect. The first-party app uses SignalR while in the foreground and disconnects when backgrounded so the server routes to push; it cannot rely on permanent background sockets.

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
Compiled with Continuum ADL core adl/1.4, compiler adlc 0.2.0; 0 errors, 10 review warnings (missing ownership, deployment mappings and verification). Current state (after the 2026-10-31 mobile change, changeState `sha256:f054f59937833a63b2a698c2bc10fd42e9e35b20e89dd9bbef496e6c58ed1427`): attestation `sha256:ec8d8674f26c93366ec102a3f7ce6c2aa4836410ac05a20820070933daa962e7`. Previous Windows-first state: attestation `sha256:15001b83acfbb18c92baf0a9a945592ef66015ec5205b6cb2168589419254714`.


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

These SVGs were rendered with Continuum Diagram from an **earlier** ADL revision, attestation `sha256:f2ce446b35e77d5a7aae1bed4715ac80f0f61e4fd6b9fd75d0cf7c78047012dd`. The current model attestation is `sha256:ec8d8674f26c93366ec102a3f7ce6c2aa4836410ac05a20820070933daa962e7` (mobile change applied). Regenerate before using the figures as a current-model architecture baseline. They are illustrative vision material, not an implementation verification.

## Windows-first implementation decisions (2026-10-31)

[Windows M1 implementation contract](WINDOWS-M1-CONTRACT.md) and [ADR-0001](adr/0001-windows-first.md) specify exact CLI/HTTP/SignalR contracts, user-session Windows agent, installer lifecycle, dev-only loopback credentials, and separation between M1A local transport and M1B hosted SaaS. Terraform provisions M1B resources; it is **not** needed for the initial loopback demonstration. The ADL and BDL remain valid as semantic source; technology bindings are a proposal, not claimed machine-verified.

## Implementation (2026-10-31)

The model is realised in `src/` (see the repository README). Mapping: `Access.TenantIdentity` → `AccountService`, `CredentialService` and the dashboard pages; `Notifications.Routing.RoutingService` → `RoutingService` + `POST /api/v1/notifications`; `Notifications.Delivery.EndpointService` → `EndpointService` + endpoint/subscription routes; `DeliveryService` → hub dispatch and replay in `RoutingService`/`NotificationsHub`; `DesktopAgent` → `HashRelay.Agent`; `CloudApi` → `HashRelay.Server` on Azure Container Apps, scale-to-zero (`infra/terraform`, `Dockerfile`). The unspecified `ClientEnrollment` question in the BDL is answered in code by device pairing (device-authorization grant shape); the BDL now lists it as an alternative but it is still not ratified as the business rule.

## Mobile (2026-10-31)

Architecture change `architecture/changes/2026-10-31-mobile-app.adl-change` (approved, applied: base `sha256:ab23b1eb…` → `sha256:f054f599…`) adds the deployable `MobileApp`, makes `DeliveryService` depend on `FirebaseMessaging`, and adds the requirement `DeliveryEvidence`: provider acceptance of a push is recorded as provider acceptance and never reported as display; display is known only when the endpoint acknowledges. Bindings: `architecture/bindings/mobile-fcm.adl-binding.json`.

Realisation: `MobileApp` → `src/HashRelay.Mobile` (Expo/React Native; pairs with `client: "mobile"`, subscription-first UI, publish when permitted, foreground SignalR, FCM token on both platforms); `FirebaseMessaging` → `Services/Push/FcmPushSender` (HTTP v1, service-account JWT) behind `IPushSender`; `DeliveryService` push path → `RoutingService.PushAsync` (queued deliveries of push-capable, not-connected endpoints; status `pushed`; `UNREGISTERED` clears the token); `EndpointService` → `pushProvider`/`pushToken` on registration and hub `Hello`; per-endpoint inbox `GET /api/v1/endpoints/{id}/deliveries` and HTTP ack `POST …/deliveries/{deliveryId}/ack`. Tests: `tests/HashRelay.Server.Tests/PushTests.cs`.

Realisation note (2026-10-31): self-service password reset. `Access.TenantIdentity` → `Services/PasswordResetService` with the `PasswordResets` store (hashed single-use tokens, 1 h lifetime, 3 per account per hour), pages `/forgot-password` and `/reset-password`, and an outbound `IEmailSender` (SMTP, or log-only when unconfigured). Email delivery is a new external dependency of `CloudApi` that the ADL does not yet declare; it is a realisation detail until the registration/recovery flow is authored in the model (see "remain to be authored" above). Tests: `tests/HashRelay.Server.Tests/PasswordResetTests.cs`.

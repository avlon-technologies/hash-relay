# Incremental delivery plan (draft)


## M0 — Multi-tenant onboarding and authorization foundation
Build a minimal signup/signin experience, tenant creation, dashboard tenant selection, tenant-scoped API-key creation/revocation and verification middleware. Establish tenant isolation and authorization tests before accepting publisher traffic. API keys are for trusted publisher integrations, not distributed mobile/desktop clients. This milestone is a prerequisite for M1; keep the UI minimal.

## M1 — Windows vertical slice
One authenticated workspace and publisher, publish API + CLI, persist tags/subscriptions, register one Windows endpoint, SignalR connection with reconnection, native toast and duplicate suppression. Tests: subscription match, multi-tag deduplication, unauthorized publication denied, restart/reconnect and missed-event policy. Done when a clean Windows install receives a notification published from a separate machine.

## M2 — Desktop portability
macOS and Linux per-user agents, startup, signing/package automation, diagnostics, update and uninstall, native notification adapters.

## M3 — Mobile app integration
**Decision (2026-10-31):** Firebase Cloud Messaging is the single push provider for both platforms; iOS is reached through Firebase's APNs relay (APNs key uploaded to the Firebase project), so HashRelay holds no APNs connection. Recorded in the ADL by `architecture/changes/2026-10-31-mobile-app.adl-change` (deployable `MobileApp`, `DeliveryService` dependsOn `FirebaseMessaging`, requirement `DeliveryEvidence`) and in the BDL (`2026-10-31.mobile-m3`).

**In progress:** first-party app in `src/HashRelay.Mobile` (Expo/React Native, iOS + Android). The phone registers once through device pairing (`client: "mobile"`), its primary screen manages hashtag subscriptions, and it can also publish when approved with the publish permission. Server side: push token on the endpoint (`pushProvider`/`pushToken`, rotated through registration and the hub `Hello`), FCM HTTP v1 sender, delivery status `pushed` for provider acceptance distinct from `acked`, token invalidation on `UNREGISTERED`, per-endpoint inbox and HTTP ack. Remaining: Firebase project + credentials in Azure, EAS builds for iOS, store distribution.

**Later:** per-application provider credentials and SDKs for third-party apps, deep links, sample integrating apps.

## M4 — Platform integrations
.NET/JS SDKs, managed API keys/scopes, MCP server, documented API contracts, audit trails and reference client apps.

## M5 — Production hardening
Scale-out, durable dispatch/retry/outbox, rate limits, quotas, observability, dashboard, privacy controls, rotation, threat model and operational SLOs.

No infrastructure provider, database product or message broker is selected by the ADL draft. Validate the vertical slice before committing to a distributed deployment.

## Release execution rule

M1A must pass the [Windows development contract](WINDOWS-M1-CONTRACT.md) including a real interactive Windows toast and installer smoke test before M1B (hosted Azure App Service/SignalR, developer web app, tenant credentials). The Azure Terraform is scoped to M1B; no Azure resource is needed for M1A.

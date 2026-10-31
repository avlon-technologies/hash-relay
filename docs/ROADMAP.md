# Incremental delivery plan (draft)


## M0 — Multi-tenant onboarding and authorization foundation
Build a minimal signup/signin experience, tenant creation, dashboard tenant selection, tenant-scoped API-key creation/revocation and verification middleware. Establish tenant isolation and authorization tests before accepting publisher traffic. API keys are for trusted publisher integrations, not distributed mobile/desktop clients. This milestone is a prerequisite for M1; keep the UI minimal.

## M1 — Windows vertical slice
One authenticated workspace and publisher, publish API + CLI, persist tags/subscriptions, register one Windows endpoint, SignalR connection with reconnection, native toast and duplicate suppression. Tests: subscription match, multi-tag deduplication, unauthorized publication denied, restart/reconnect and missed-event policy. Done when a clean Windows install receives a notification published from a separate machine.

## M2 — Desktop portability
macOS and Linux per-user agents, startup, signing/package automation, diagnostics, update and uninstall, native notification adapters.

## M3 — Mobile app integration
Android SDK + FCM, then iOS SDK + APNs; per-application provider credentials; token refresh; sample integrating apps; foreground vs background handling; permission prompts; deep links.

## M4 — Platform integrations
.NET/JS SDKs, managed API keys/scopes, MCP server, documented API contracts, audit trails and reference client apps.

## M5 — Production hardening
Scale-out, durable dispatch/retry/outbox, rate limits, quotas, observability, dashboard, privacy controls, rotation, threat model and operational SLOs.

No infrastructure provider, database product or message broker is selected by the ADL draft. Validate the vertical slice before committing to a distributed deployment.

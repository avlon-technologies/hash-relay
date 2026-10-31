# HashRelay Windows M1 — Development contract
Status: implementation-ready design decision (2026-10-31). ADL and BDL remain semantic source; this document makes the Windows milestone operational.

## Milestone contract
M1A proves a real Windows toast: CLI HTTP publish -> one local .NET 10 ASP.NET Core server -> SignalR -> per-user Windows agent -> Windows App SDK AppNotification -> Windows Notification Center. Host server at 127.0.0.1:5075 for M1A. No Azure, signup or SaaS tenancy in this phase. M1B deploys API plus Azure SignalR via Terraform, then developer dashboard/signup, tenant-scoped API keys, durable subscriptions and subscriber identity. Never claim M1A demo authentication establishes M1B tenant isolation.

## Components and ownership
- HashRelay.Contracts: public request/event/error schema definitions.
- HashRelay.Server: publication HTTP endpoint, endpoint/subscription registry, matching, deduplication and SignalR hub; one process, in-memory for M1A.
- HashRelay.Sdk: .NET publisher HTTP SDK; shared with CLI.
- HashRelay.Cli: `configure`, `subscribe`, `subscriptions`, `publish`, `status`, `test`. `test` verifies server acceptance; Windows notification display requires interactive OS verification.
- HashRelay.Windows.Agent: interactive per-user executable with persistent stable endpoint ID, SignalR reconnect, native toast registration and recent notification ID deduplication.
- HashRelay.Windows.Installer: per-user Inno Setup EXE, self-contained Windows x64 binaries, stable icon assets, startup at login, Start menu shortcut, launch post-install, upgrade preserving endpoint ID, uninstall removing startup and app notification registration. No elevated privileges for M1A.

## Identity and security decisions
M1A server binds loopback ONLY and uses one random development token for CLI and agent; reject missing/wrong token for publish/subscribe/SignalR. Store token per Windows user with restricted permissions; never embed a production key in a public client. Refuse dev-token mode when configured for a non-loopback server.
M1B: user dashboard authentication via OIDC; tenant membership controls key creation. Publisher API keys are tenant-bound, limited-scope, revocable, server-side secrets for trusted integrations. Store verifier/hash only, show secret once. Windows agents authenticate as subscribers with OAuth authorization-code + PKCE and short-lived tenant/endpoint-limited tokens, never publisher keys. Resolve tenant from validated credentials, not a submitted tenant ID; partition all persistent data and SignalR groups by resolved tenant. API-key and OAuth issuance are M1B, not M1A.

## Protocol v1
JSON over HTTP `/api/v1`; demo token header `X-HashRelay-Dev-Token` on loopback only.
- GET `/health/live` returns 200.
- POST `/api/v1/endpoints` `{endpointId,platform:"windows"}` returns 201 or 200 idempotently.
- PUT/DELETE `/api/v1/endpoints/{endpointId}/subscriptions/{tag}` return 204 idempotently.
- GET `/api/v1/endpoints/{endpointId}/subscriptions` returns `{tags:[...]}`.
- POST `/api/v1/notifications` `{notificationId,tags,title,body}` returns 202 `{notificationId,acceptedAtUtc,matchedEndpoints}`.
- Same notification ID + identical payload is idempotent; changed payload is HTTP 409.
- Error envelope `{code,message,traceId}`; 400 malformed input, 401 unauthenticated, 403 forbidden endpoint, 404 missing, 409 conflict, 429 throttled, 500 unexpected.
- Hashtags normalized invariant lowercase, `^#[a-z0-9][a-z0-9_-]{0,63}$`, max 10 distinct per publication. Title 1–120 chars; body 1–1000 chars. Notification ID is UUID.
- Server matches union of subscriptions and sends at most one logical notification per `(tenantId,notificationId,endpointId)`.

SignalR hub `/hubs/notifications`: authenticate on connect, bind endpoint identity server-side, no untrusted broadcast hub method. Server callback `NotificationReceived` payload `{notificationId,tenantId,tags,title,body,publishedAtUtc}`. Agent suppresses recent duplicates, reconnects automatically and rehydrates subscription membership from server. SignalR groups are not persistent subscription truth. M1A delivery is online-only, best-effort: disconnected agent misses messages; no offline replay guarantee.

## CLI examples
```powershell
hash-relay configure --server http://127.0.0.1:5075
hash-relay subscribe "#demo"
hash-relay publish --tag "#demo" --title "Build complete" --body "All tests passed"
hash-relay subscriptions
hash-relay test
hash-relay status
```
Exit codes 0 success, 2 invalid input, 3 authentication, 4 server unavailable, 5 request rejected, 10 unexpected failure. `--json` for machine output, never print credentials.

## Tests and release gates
Automated: contract serialization, hashtag matching and tenant discrimination, idempotency and conflict, unauthorized publish/listen, no cross-endpoint send, SignalR reconnect, SDK/CLI shared API, installer artifact manifests.
Interactive Windows VM: fresh per-user install without elevation; startup agent and notification identity; publish through a separate CLI process; native toast visible and retained in Notification Center if OS settings permit; unsubscribed tag no toast; multi-tag one toast; reboot autostart; uninstall startup and notification registration cleanup; upgrade preserves endpoint identity.
Evidence: capture Windows build, test ID, release SHA, install logs, process/server logs and screenshot; if no interactive Windows environment, agent must report NOT VERIFIED, not PASS.
Azure M1B: `terraform fmt -check`, `terraform init`, `terraform validate`, reviewed plan/apply, App Service health, managed-identity Azure SignalR connection, cross-tenant isolation tests.
No mobile, email, SMS, WNS, broker, strong offline guarantee or global availability SLOs in M1.

## Agent operating constraints
Agent may choose code organization, test framework and logging library, but must not change protocol fields, authentication boundaries, installer semantics or delivery guarantees without an ADL/BDL/change review. Stop and report an architectural contradiction rather than guessing.

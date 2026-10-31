# M1 traceability and autonomous execution gates

| Business intent (BDL) | ADL element(s) | Development contract | Required evidence |
|---|---|---|---|
| DeveloperOnboarding | Access.TenantIdentity.TenantAdministrationService, ApiKeyService | M1B dashboard/tenant keys | Tenant A/B permission tests and key rotation |
| HashtagNotifications | Notifications.Routing.RoutingService and PublicationApi | M1A HTTP publish; matching and dedup | 202 acceptance, 409 collision, unmatched tag |
| SubscribeHashtag | Notifications.Routing.SubscribeTag | M1A subscribe and list | Subscription persists across reconnect while server up |
| PublishNotification | Notifications.Routing.PublishNotification, NotificationAccepted | M1A publication and event handoff | CLI and SDK identical schema; SignalR receives one frame |
| Endpoint registration | Notifications.Delivery.EndpointService, EndpointApi | M1A Windows agent registration | Stable endpoint ID across reboot and upgrade |
| Native display | HashRelay.DesktopAgent, DesktopNotificationCenter | M1A Windows SDK toast + installer | Interactive Windows screenshot and logs |
| SaaS hosting | HashRelay.CloudApi + Windows/production binding proposal | M1B Terraform App Service/SignalR | Terraform validated, approved plan, deployed health |

## Exit gates
**Architecture source:** ADL compiler PASS, BDL compiler PASS. Binding document remains a candidate until Continuum binding conformance tooling validates its vocabulary. Do not claim binding conformance.

**M1A autonomous agent may begin** when this contract is read, the agent has repo write access, and a suitable .NET Windows build runner. It should implement/test in vertical slices:
1. Contracts and server; HTTP integration tests.
2. SDK, CLI and executable test runner; programmatic SignalR tests.
3. Windows agent and OS native notifications.
4. Per-user installer, upgrade/uninstall and Windows smoke test.

Do not declare M1A complete without a Windows desktop session and genuine toast evidence. A headless Linux CI pass is necessary but insufficient.

**M1B should not begin** until M1A is verified and the remaining business decisions (dashboard identity provider choice, persistent store and retention, key rotation, operational quotas) are accepted and reflected in ADL/BDL/bindings.

## Decision log
Windows agent auth in M1A is strictly local dev token; remote agents use OAuth/PKCE in M1B. Publisher tenant key is never embedded in distributable SDK client binaries. SignalR provides best-effort online delivery in M1A; there is no offline queue. The installer is a per-user Inno Setup EXE. Azure App Service and Azure SignalR are provisioned only for M1B.

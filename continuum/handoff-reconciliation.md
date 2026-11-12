# M2 Desktop portability — handoff reconciliation and gaps

Authored review artifact (not package inventory, not authority). Status of the handoff: **NOT READY for
implementation without human confirmation** of the proposed decisions in
`architecture/proposals/2026-11-11-m2-desktop-portability-decisions.md`; the package itself builds and checks.

## Reconciliation matrix

| # | Agreed intent (source) | Authoritative artifact / element | Executable evidence | Disposition |
|---|---|---|---|---|
| 1 | macOS + Linux per-user agents (ROADMAP M2) | ADL `DesktopAgent` (desc), `EndpointAgent.AgentRuntime` deployedAs it; Environments `MacOSDesktop`, `LinuxDesktop`; bindings `mac-osdesktop`, `linux-desktop` | all 19 scenarios | represented (technology PROPOSED) |
| 2 | Protocol parity: pairing device flow, Hello on every (re)connect, ack, replay, duplicate suppression, catch-up summary (brief; `src/HashRelay.Client`) | `DesktopEndpoint.DesktopProtocolParity`; AgentRuntime `sends RegisterEndpoint`, `dependsOn DeliveryService, EndpointService`; also satisfies `OncePerEndpoint`, `CatchUpWithoutInterruption` | `*-agent-registers-on-every-connect`, `*-backlog-presented-as-one-summary`, `*-duplicate-delivery-presented-once` | represented; hub Hello/Ack are not modelled as ADL operations (gap G3) |
| 3 | Actionable approvals at parity (NATIVE-APPROVAL-ACTIONS.md, ACTIONABLE-APPROVALS.md) | `NativeActionFallback`, existing `DeviceDecisionAuthority`, rule `ActionsConferNoAuthority`; AgentRuntime `sends DecideApproval` | `*-approval-actions-need-grant`, `linux-no-action-capability-opens-review-page` | represented |
| 4 | Native notification adapters | `UserSessionPresentation`; `dependsOn DesktopNotificationCenter`; bindings: UNUserNotificationCenter / freedesktop Notifications | `*-per-user-install-lifecycle`, `macos-permission-denied-is-reported` | represented (D-M2-01, D-M2-02 proposed) |
| 5 | Start at login | `PerUserLifecycle`; bindings SMAppService / XDG autostart | `*-per-user-install-lifecycle` | represented (D-M2-05 proposed) |
| 6 | Signing / packaging automation | `VerifiedUpdates` (verification side); BDL obligation `ShipOnlySignedAgents`; bindings `signing`, `notarization`, `distribution`, `ci` keys | `*-only-signed-updates-apply` (agent side only) | PARTIAL: CI pipeline itself has no executable scenario (gap G5) |
| 7 | Diagnostics | `AgentDiagnostics` | `*-diagnostics-without-secrets`, `macos-permission-denied-is-reported`, `linux-no-secret-service-degrades-custody` | represented (D-M2-08 proposed) |
| 8 | Update | `VerifiedUpdates`, external `AgentReleaseChannel` | `*-only-signed-updates-apply` | represented (D-M2-07 proposed) |
| 9 | Uninstall | `PerUserLifecycle`; BDL `UninstallDesktopAgent`, obligation `ForgetDeviceOnUninstall`; AgentRuntime `sends RevokeDeviceDecisions` | `*-per-user-install-lifecycle` | represented; server endpoint disposition unresolved (BDL `UninstallServerDisposition`) |
| 10 | Secret storage for device tokens | `LocalCredentialCustody`, resource `DeviceVault [secret-store]` | `*-secrets-stay-in-vault`, `linux-no-secret-service-degrades-custody` | represented (D-M2-06 proposed); write path is a language limit (G1) |
| 11 | No change to server behaviour (brief: "existing protocol") | change adds nothing server-side; `EndpointService` binding notes platform is a free string (verified `EndpointService.cs:44`) | — | represented |
| 12 | Windows parity drift (repo evidence) | `local-development.adl-binding.json` names the gaps on `DesktopAgent`, `AgentRuntime`, `DeviceVault` | none for Windows | explicitly deferred (D-M2-09) |

## Gaps

| id | kind | gap | smallest next action |
|---|---|---|---|
| G1 | confirmed (language limit) | ADL forbids `Service writes Resource[secret-store]`; the agent provisions its own credential/grant. Modelled as read-only + desc. | owner/language ruling, or accept as stated |
| G2 | needs-evidence | All `sqlite-efcore` store bindings and both vault technologies declare A.4 claims that no evidence record witnesses (`compile --bindings`: "declared; unverified"). | `continuum evidence` records once implemented |
| G3 | deferred | Hub `Hello`, `Ack`, `NotificationReceived` and the device-code pairing endpoints are not ADL operations; parity is asserted through `RegisterEndpoint` and requirement prose. | a protocol-surface `.adl-change` (out of M2 scope) |
| G4 | needs-evidence | 7 `causal-relation-undeclared` findings in `BEHAVIOUR.md` (server Reactions: RegisterEndpoint, PublishNotification, NotificationAccepted, DecideApproval, RevokeDeviceDecisions, ApprovalResolved) predate M2. | declare Reactions in a server-side change |
| G5 | unknown | Release CI (sign, notarize, publish manifest) has no executable acceptance; scenarios cover only the agent's verification. | add a release-pipeline check outside Continuum, or a test element |
| G6 | deferred | Windows agent does not meet `LocalCredentialCustody`, `PerUserLifecycle` (uninstall), `VerifiedUpdates`, `AgentDiagnostics`. | D-M2-09 follow-up change for Windows |
| G7 | out-of-scope | 8 server requirements have no scenario (`uncoveredRequirements` in PACKAGE.json). | M5 / server packages |
| G8 | unknown | Supported Linux desktops (BDL `SupportedLinuxDesktops`) and uninstall server disposition (BDL `UninstallServerDisposition`). | product owner decision |
| G9 | confirmed (tooling) | No adapter: `continuum package run` refuses ("no adapter at continuum/acceptance/adapter.mjs"). Scenarios are validated, not executed. | implementer writes `acceptance/adapter.mjs` (`continuum package adapter init continuum`) |
| G10 | confirmed (tooling) | `architecture/bindings/{windows-local,mobile-fcm}.adl-binding.json` are pinned to an old attestation and both bind `DeliveryService` in `LocalDevelopment`; superseded in this package by `continuum/model/local-development.adl-binding.json` (identities reused). | retire or regenerate the old documents |
| G11 | confirmed | The package rests on proposed state B; `architecture/model/hash-relay.adl` is still state A. Adoption is a human act. | approve the change, then adopt B |

# M2 Desktop portability — Continuum Diagram requests

These are REQUESTS for native Continuum Diagram renders; no diagram has been drawn. The authoring
session had no Continuum Diagram tools (`continuum doctor`: "diagram — no CLI integration"), so nothing
here was rendered, substituted or approximated.

Model to import for all three (state B, the proposal):

- manifest: `continuum/model/hash-relay.adl-manifest.json` (document `continuum/model/hash-relay.adl`)
- attestation `sha256:f2b757e3706b13154fba281d4a9e15ef9513d12a0d2e486d8b4a4a08da528870`
- changeState `sha256:847cb3c84834a50ddec4f6190a01fb65fe6bd49d28c1f81235be766cc1a8dc1f`
- derived by `architecture/changes/2026-11-11-m2-desktop-portability.adl-change` from state A
  `architecture/model/hash-relay.adl-manifest.json` (changeState `sha256:a89061e4…f7f`). Everything the change
  adds is a PROPOSAL and should be drawn and labelled as proposed.

## 1. Exploratory (grounding: proposal) — where the desktop agent fits

Question to ask, verbatim:

> Show me how the proposed M2 desktop agent fits into HashRelay: the platform-neutral AgentRuntime deployed
> as DesktopAgent, its DeviceVault, what it depends on in the cloud (DeliveryService realtime hub,
> EndpointService registration), the commands it sends (RegisterEndpoint on every connect, DecideApproval
> under a grant, RevokeDeviceDecisions at uninstall), the DesktopNotificationCenter and the
> AgentReleaseChannel. Mark everything added by the M2 change as proposed.

Rendering: exploratory (default). Purpose: let the owner react to the boundary before confirming D-M2-01..11.

## 2. Polished (grounding: proposal) — requirements to tests traceability for M2

Question to ask, verbatim:

> A polished, presentation-quality diagram of the DesktopEndpoint capability: its seven requirements
> (DesktopProtocolParity, UserSessionPresentation, LocalCredentialCustody, NativeActionFallback,
> PerUserLifecycle, VerifiedUpdates, AgentDiagnostics), the AgentRuntime service that satisfies them, the
> existing HashtagDelivery requirements it now also satisfies (OncePerEndpoint, CatchUpWithoutInterruption)
> and DeviceDecisionAuthority, and the eight tests that verify them. This is our proposed design.

Rendering: polished (verified, editable SVG). Purpose: review artifact for the M2 proposal.

## 3. Exploratory, optional (grounding: proposal) — one environment, three platforms

Question to ask, verbatim:

> How does one DesktopAgent deployable and one AgentRuntime service become three platform builds? Show the
> three Environments the model declares (LocalDevelopment for Windows, MacOSDesktop, LinuxDesktop) and that
> each binds DesktopAgent, AgentRuntime and DeviceVault separately, while the cloud elements are the same.

Note for the renderer: bindings are not ADL relationships. If the diagram service draws only the semantic
model, it should show the three Environment elements and the agent elements and say that the per-platform
technology lives in `continuum/model/{local-development,mac-osdesktop,linux-desktop}.adl-binding.json`;
it must not draw an inferred edge as a declared one.

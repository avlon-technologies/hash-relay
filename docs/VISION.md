# HashRelay — product vision (draft)

HashRelay is a workspace-scoped hashtag-based notification platform. Authorized applications, CLIs and AI agents publish a notification with one or more tags; subscribed users receive it through registered desktop or mobile application endpoints.

## Product surfaces
- HashRelay Cloud: public HTTPS API, subscription and endpoint management, routing and delivery.
- HashRelay Agent: installable native notification endpoint for Windows, macOS and Linux, with per-user startup and CLI.
- Mobile SDKs: native iOS/Android and later wrappers, using the integrating app's APNs/FCM credentials and identity.
- HashRelay App: optional first-party mobile subscriber app.
- MCP server: agent-facing publish and subscription capabilities, with scoped authorization.
- Developer SDK: application-facing publish and subscription operations.

## Principles
- A hashtag names a subscription topic within a workspace, not a globally public broadcast channel.
- A publication matching several tags produces one logical notification per eligible endpoint.
- Persistence and subscriptions belong to HashRelay, not SignalR groups.
- Delivery transports are adapters: SignalR for connected desktop clients; APNs/FCM for mobile push. Foreground mobile clients may use SignalR.
- Never equate provider acceptance with display or user acknowledgement.
- Authorization, quotas, and isolation must exist before permitting untrusted external publishers.

## Initial proof of value
One Windows user installs the agent, subscribes to #builds, and receives a native notification published through the cloud API or CLI. Subscriptions survive restart/reconnection. An unrelated workspace cannot publish into that subscription.

## Deliberate exclusions from first milestone
Mobile push, marketplace-ready packaging, extensive multi-tenant administration, web dashboard, MCP tooling, rich notification actions, and scale-out are later increments.

## Multi-tenant developer experience
Users sign up, select or create a tenant workspace, and access a developer dashboard to issue tenant-scoped API credentials. Tags are meaningful only inside the authenticated tenant. Trusted integrations publish using tenant-bound credentials; users and devices subscribe through individually authorized identities.

## Continuum Diagram — product vision

![HashRelay product vision](diagrams/hashrelay-vision.svg)

This is a checked Continuum Diagram rendering of the earlier pinned ADL architecture (attestation `sha256:f2ce446b35e77d5a7aae1bed4715ac80f0f61e4fd6b9fd75d0cf7c78047012dd`). It illustrates the product ambition, including future mobile endpoints; dotted paths are planned rather than established ADL relationships. The current Windows-first ADL has since evolved; the image must not be treated as the current model's canonical projection.

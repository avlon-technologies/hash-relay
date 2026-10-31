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

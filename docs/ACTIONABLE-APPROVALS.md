# Actionable approvals — implementation contract

Status: **Development-ready design proposal, not implementation.** Date: 2026-11-01.
Source architecture: `architecture/model/hash-relay.adl` (Continuum ADL 1.4 draft; compile attestation `sha256:260afc5be54aeddcadaad953d6b08256efdc63f167434104b0f1e317ae84ad8a`). Compiler result: 0 errors / 13 warnings; approval-specific warnings include unverified DecisionIntegrity, missing BC ownership and unconsumed ApprovalResolved. Acceptance and implementation verification remain separate gates.

## Bounded-context responsibility
`Approvals.DecisionManagement.ApprovalService` owns approval state, authorized transitions and durable result publication. `ApprovalStore` is the authoritative transactional store. Existing `Notifications.Routing` publishes an ordinary notification referencing an approval, and `Notifications.Delivery` transports it with the existing at-most-once logical endpoint fanout. `Access.TenantIdentity` supplies verified tenant and human identity. Requesting systems execute their own actions after reading/validating the decision. A toast, webhook, SignalR event or MCP response is not the decision source of truth.

## Model and persistence
Persist `Approval`: `id UUID`, `tenantId`, `requesterCredentialId`, `requesterCorrelationId`, `subject`, `description` (text, untrusted), `targetUserIds` (nonempty normalized stable tenant-member IDs), `status`, `createdAt`, `expiresAt`, `decidedAt`, `decidedByUserId`, `decisionNote`, `version`, `notificationId` nullable. Use existing secure authenticated tenant context; do not trust caller-supplied tenant or actor IDs. Store a normalized request payload hash for idempotency. Add unique `(tenantId, requesterCredentialId, idempotencyKey)`. Store `ApprovalAudit` immutable entries recording creation, notifications, decision, expiry, cancellation, actor, correlation and request IDs (without tokens or sensitive message bodies). Add a durable completion/outbox record keyed by approval ID and transition version to publish `approval.resolved` after commit; consumers deduplicate.

Terminal states: `Approved`, `Rejected`, `Expired`, `Cancelled`; initial `Pending`. Only `Pending` transitions to terminal. Time of decision must satisfy `serverNow < expiresAt`. Expiry may be materialized lazily on every read/decide plus a background sweep; the database condition is definitive. Decision CAS: update with `WHERE tenantId = ? AND id = ? AND status = Pending AND expiresAt > serverNow` under transaction, insert audit and outbox atomically. Losing contender returns current state with conflict response; never reverts state. Exact retries with the same decision idempotency key return prior outcome. Requester cancellation requires original requester credential identity or expressly authorized admin; no cancellation of terminal request.

### Authentication and permissions
Request: authenticated publisher credential carrying `approvals:create` (service account), or equivalent explicitly granted publish-side authority; authorization bounded to tenant. Reject unsafe creation using a subscriber-only device token. Resolve each designated approver to a tenant member server-side; no cross-tenant identities. Decision: authenticated interactive human account/session with `approvals:decide` and member identity in the request's target set. **Device pairing alone and publish credentials alone must never approve**. For an agent/desktop/mobile button, open a first-party HTTPS decision page authenticated as that human, unless a secure existing interactive identity flow supports equivalent authorization. Protect browser decision POST with CSRF mitigations and SameSite policy; rate limit endpoints; do not treat bearer tokens in URLs as authorization. Recheck account membership and request eligibility at decision time. No global admin bypass in v1. Display publisher provenance from verified credentials where possible, otherwise Unknown; do not classify MCP callers as agents.

## HTTP API v1 extensions (proposed)
Existing `/api/v1/notifications` remains unchanged.
- `POST /api/v1/approvals` — create with `Idempotency-Key`, `subject`, optional `description`, `targetUserIds`, `expiresAt`, `tags` (optional routing tags), `requesterCorrelationId` (optional). Server derives tenant/requester. Return `201 Created` + resource or `200` same-key replay; mismatched same-key payload returns `409`. Must not trigger duplicate notification on replay.
- `GET /api/v1/approvals/{id}` — tenant-scoped resource; authorized original requester (or designated approver) only. Return status, version, timestamps, decision actor where entitled, and `notificationId`.
- `GET /api/v1/approvals?status=&limit=&cursor=` — only requests actor may view; bounded pagination and tenant filtering.
- `POST /api/v1/approvals/{id}/decision` — body `{ "decision": "approve" | "reject", "note": "optional" }`, plus `Idempotency-Key`; interactive human only. On success `200` state; concurrent conflicting decision `409` with current state; expired `409` with `Expired`; invalid decision `400`; unauthorized `403`; invisible or foreign tenant `404`.
- `POST /api/v1/approvals/{id}/cancel` — requester-authenticated, idempotent until terminal; `200` current record or `409` if already decided. Expiry takes precedence when past expiry.

Error responses use existing API error envelope. No implicit approval on GET, toast click, notification acknowledgement or device delivery. Use server-generated immutable approval IDs; no arbitrary action URL supplied by publisher. If approved, requester must independently validate business preconditions before executing.

## MCP surface
Add `request_approval` and `get_approval` with tenant/scope enforcement and untrusted-content notices. Add `list_approvals` only if existing read policy allows actor-filtered visibility. **Do not expose `approve_approval` or `reject_approval` to non-human MCP service credentials in v1.** Tool descriptions must not imply that submission waits for approval or execution. Tools return IDs/status and authoritative detail URL. Preserve `publish_notification` contract and existing MCP features.

## Client experience
Notification link opens `/approvals/{id}` on HashRelay's own origin. Detail view shows verified requester (or Unknown), action, expiry, full request details and current status. Approved/rejected buttons submit authenticated POST after displaying the operation, particularly high-risk actions. Confirm success from the server before reporting completion. All other devices refresh final state; stale notifications cannot reverse it. Offline catch-up can summarize requests but must prominently surface still-pending approvals, never turn historical terminal requests into active action buttons. No Windows toast executable command or untrusted external URL is an authorization mechanism. Screen readers, keyboard navigation and small screens must work.

## Result delivery and liveness
Use transactional outbox and a bounded retry dispatcher. Provide an authenticated GET for any requester that misses callbacks, and optional `approval.resolved` event published after commit (with approval ID/status/version/correlation, not full sensitive detail). Never infer approval from notification receipt/ack. Sweep expired Pending approvals and emit one terminal event. If an outbox consumer crashes, retry idempotently. Use existing service infrastructure if adequate rather than adding a queue product.

## Acceptance scenarios (release blocking)
1. Create for a valid target in tenant A; one notification routes through existing tag/endpoint mechanisms, one persistent Pending approval exists.
2. Same idempotency key and same payload returns one request; differing payload conflicts.
3. Authorized human approves; state, audit, outbox commit together; requester GET reads Approved and can resume its own process.
4. Reject path records Rejected and prevents requester treating it as approval.
5. Two devices decide concurrently; exactly one wins and all later reads show the winner.
6. Expired approval cannot be decided, including at the expiry boundary; sweep and lazy handling converge.
7. Requester cancels Pending; further approve/reject fails; no post-cancellation execution implied.
8. Cross-tenant request/read/decide and service-account-only decision are denied without leaking record existence.
9. Unlisted tenant member, expired account session and revoked tenant membership cannot decide.
10. WebSocket disconnect/outbox retry does not lose committed decision or duplicate its state transition.
11. An approval presented to several devices remains one approval; duplicate tags do not duplicate logical deliveries.
12. Notification delivery/display/ack is independent of approval state; old toast only opens current detail.
13. Existing non-actionable REST/MCP notifications and clients continue functioning unchanged.
14. Audit identifies verified decision actor/time/requester without leaking credentials.
15. Mobile/desktop toast action requiring user auth opens first-party confirmation, cannot approve from a device key or forged deep link.

## Development sequence
1. Migration and domain service with atomic transitions, authorization and unit/integration tests.
2. HTTP resources and tenant-filtered query policies.
3. Outbox/event + requester read/recovery.
4. Server-side actionable notification rendering and authenticated web confirmation.
5. MCP creation/read tools and client integrations.
6. End-to-end Windows/mobile multi-device tests, regression, migration/rollback review and deployment.

## Explicit deferrals
Multi-person quorum, delegation, hierarchical policies, escalation, approval execution, scheduling, custom approval buttons, external action URLs and third-party callback registration. Notification backlog UX is a separate concern; don't expand this change into general delivery redesign.

## Remaining implementation gates
ADL is an unaccepted draft; review/adopt against repo lifecycle rules. Continuum warnings must be evaluated (not silently ignored), particularly the approval event consumer, verification and ownership bindings. Validate schema against existing database migrations, identity policies and deployment conventions before implementation. No code/tests were executed by this design-only artifact.

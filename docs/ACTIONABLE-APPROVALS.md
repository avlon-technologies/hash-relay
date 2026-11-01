# Actionable approvals — implementation contract

Status: **Milestone 1 (backend vertical slice) and Milestone 2 (MCP tools) implemented**; see [Milestone 1 implementation](#milestone-1-implementation) and [Milestone 2 implementation](#milestone-2-implementation-mcp-tools). Date: 2026-11-01.
Source architecture: `architecture/model/hash-relay.adl` (Continuum ADL 1.4 draft). Designed against changeState `sha256:44127a98aa3fa1f667c2f57ef2ac9e3a5c4664b703aac6f73c3cecad4e4d56ea`; Milestone 1 applied `architecture/changes/2026-11-01-approval-outcome-contracts.adl-change`, giving changeState `sha256:36ec56ff234b6deac6a20bb6bd6d156bed45f7b0b2a28ade137a5984338b805e` (0 errors / 16 warnings). Acceptance and implementation verification remain separate gates.

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

## Milestone 1 implementation

Shipped: `POST /api/v1/approvals`, `GET /api/v1/approvals/{id}`, `POST /api/v1/approvals/{id}/decision`, `POST /api/v1/approvals/{id}/cancel`, the decision page `/approvals/{id}`, the approver's pending list `/approvals`, the transactional outbox with its dispatcher and sweep, and `ApprovalOutcomeReader`. Code: `src/HashRelay.Server/Services/Approvals/`, `Api/ApprovalEndpoints.cs`, `Data/ApprovalEntities.cs`, `Pages/Approval*.cshtml`; contracts in `src/HashRelay.Contracts/Approvals.cs`; tests in `tests/HashRelay.Server.Tests/ApprovalTests.cs`.

How this contract's open points were resolved:

| Point | Contract said | Milestone 1 does | Why |
|---|---|---|---|
| ApprovalResolved payload | ADL: `payload ApprovalDecision` (a human's decision) | ADL change: payload `ApprovalOutcome {approvalId, workspaceId, requesterId, correlationId, status, cause, version, resolvedAt, decidedBy?}` | Expiry and cancellation also resolve an approval; an event typed as a decision would claim a person decided. `cause` is `decision`, `expiry` or `cancellation`; `decidedBy` is set only for `decision`. |
| Cancel and retrieval in the model | Not declared | ADL change: command `CancelApproval` (payload `ApprovalReference`), operations `ApprovalApi.Retrieve` and `ApprovalApi.Cancel`, requirement `OutcomeLifecycle` | The HTTP surface had no counterpart in the model. |
| Requester credential | "`approvals:create` (service account), or equivalent publish-side authority" | Service key needs new scope `approvals:create`. API key needs `publish`. Device tokens are refused whatever their scopes. | `approvals:create` lets a service account request but never decide. |
| Decider authority (`approvals:decide`) | Scope on a human | No new scope. Authority = signed-in session cookie + current membership (re-read at decision time) + listed in `targetUserIds`. All roles may be designated in v1. | People do not hold scopes in HashRelay; designation by the requester, bounded to members, is the grant. |
| Decision transport | "interactive human only" | `POST …/decision` authenticates **only** the `hr_session` cookie scheme and validates the antiforgery token (`RequestVerificationToken` header). API, service, OAuth and device bearer tokens get `401`. The page's form posts to the same service. | Bearer tokens never reach the decision path, so no machine credential can approve. |
| `GET` by a designated approver | Allowed | Approvers read through `/approvals/{id}` (session). `GET /api/v1/approvals/{id}` is for the requester only: the creating credential, or any key of the same service account. | Keeps the API surface for machines and the human view on the session. |
| Who may cancel | Requester or expressly authorised admin | Requester only | Admin cancellation deferred. |
| Notification | One ordinary notification | Published through the unchanged routing path only when `tags` are given, with a notification id reserved at creation (retries are idempotent), title `Approval requested: <subject>`, link to the decision page, priority high, and **no description** (that is shown only after sign-in). Skipped if the approval left Pending first. | Publication failure never changes approval state; the intent is retried with backoff (up to 10 attempts). |
| Decision idempotency | `Idempotency-Key` on decision | Optional on decision. The same key from the same person returns the prior outcome; the same key for a different decision is `409`. The page sends a per-render key. | |
| Lifetime bounds | — | `expiresAt` 1 minute to 30 days from now; 1–10 approvers; subject ≤ 120, description ≤ 4000, note ≤ 500. | |

Lifecycle and consistency as built: `Pending` leaves exactly once. Every transition is one `UPDATE … WHERE id, tenantId, status = 'pending', version = v` with `expiresAt > now` for a decision or cancellation, or `expiresAt <= now` for expiry. It runs in one SQLite write transaction (`BEGIN IMMEDIATE`) with the audit row and the `approval.resolved` outbox row; the outbox is unique per `(approvalId, kind, version)`. A loser rolls back and receives `409` with `details.status` and `details.version`. Expiry is materialised lazily on read, decide and cancel, and by `ApprovalMaintenanceService` (`HashRelay:Approvals:SweepIntervalSeconds`, default 15; 0 disables it). `ApprovalAudit` is append-only: the DbContext refuses updates and deletes.

Not in Milestone 1: `GET /api/v1/approvals` (list), MCP `request_approval` / `get_approval` (shipped in Milestone 2, below), native toast or mobile action buttons, admin cancellation, webhook or callback delivery of `approval.resolved` (the outcome is read by `GET`), and quorum, delegation and escalation (unchanged deferrals).

## Milestone 2 implementation: MCP tools

Shipped: three tools on the existing MCP endpoint (`/mcp`, stateless Streamable HTTP, bearer-authenticated). Code: `src/HashRelay.Server/Mcp/ApprovalTools.cs`, plus `ApprovalService.EligibleApproversAsync`; tests in `tests/HashRelay.Server.Tests/McpApprovalTests.cs`. Each tool calls the same `ApprovalService` methods as the HTTP routes, with the requester built by the same `ApprovalEndpoints.Requester`. The tools hold no state of their own, so there is no second source of truth.

| Tool | Inputs | Returns | Maps to |
|---|---|---|---|
| `list_approvers` | none | `approvers[]`: `accountId`, `displayName`, `role`, `authorizedThisCredential` (the person who created this service account; for an OAuth connector, the person who connected it). No emails. At most 100. | Membership read, using the same eligibility rule as create |
| `request_approval` | `subject`, `targetUserIds[]`, `expiresAt`, `idempotencyKey` (required); `description`, `tags[]`, `requesterCorrelationId` (optional) | `approvalId`, `status` (`pending`), `proceed` (false), `version`, `expiresAt`, `requesterCorrelationId`, `approvalUrl`, `notificationId`, `created` (false on an idempotent replay), `next` (what the agent should do) | `POST /api/v1/approvals` (`ApprovalApi.Request`); `idempotencyKey` is the `Idempotency-Key` header |
| `get_approval` | `approvalId` | `approval`: the full `ApprovalResource` (status, proceed, version, timestamps, resolution with cause, decider and note), plus `guidance` and an untrusted-content `note` | `GET /api/v1/approvals/{id}` (`ApprovalApi.Retrieve`) |

| `publish_notification` with `approval` | the usual `tags`, `title`, `body`/`markdown`, `notificationId`, plus `approval: { targetUserIds?, expiresInSeconds? (default 900), requesterCorrelationId? }` | the usual publish outcome plus `approvalId`, `approvalStatus`, `approvalExpiresAt`, `approvalUrl` | The same create path as `request_approval`: title → subject, markdown or body → description, `notificationId` → idempotency key (a retry keeps the original deadline). At least one hashtag is required. |

`targetUserIds` is optional in both creating tools. When it is omitted, the approver is the person who created this credential's service account (for an OAuth connector, the person who connected it), resolved on the server. A credential with no such person, such as a plain API key, must name approvers.

**Windows toast actions.** The desktop agent adds **Approve…** and **Reject…** buttons to any notification whose link is `{its own server}/approvals/{id}` (`HashRelay.Client.ApprovalLink`). The buttons open that first-party page with `?decision=approve|reject` pre-selected. The page shows "Confirm approval" or "Confirm rejection" and records nothing until the signed-in person submits the form, which carries the antiforgery token and the per-render idempotency key. So:

- The device token never decides. A pairing alone cannot approve, as this contract requires.
- A forged approval link in an ordinary notification gains nothing: the page enforces sign-in, membership and designation.
- Toasts that come back after an agent restart, or are replayed to an offline device on reconnect, still lead to the live page. That page shows the current state: confirmable while Pending and before expiry, otherwise the final outcome.
- A double submit records one decision. A conflicting later decision gets "already resolved".

That remains the behaviour when one-click approvals are not enabled. With them enabled, the buttons decide directly; see the next section.

Rules, all inherited from Milestone 1 rather than re-implemented:

- **Who may call.** A service key with `approvals:create`, or an API key with `publish`. Device tokens and subscribe-only keys are refused. The service account's hashtag allowlist bounds `tags`. Approvers must be eligible members of the caller's workspace.
- **Visibility.** `get_approval` returns only approvals created by this credential or by another key of the same service account. Anything else, in this workspace or another, returns "No such approval".
- **No decision tool.** There is no `approve_approval`, `reject_approval` or `cancel_approval` tool. Deciding stays on `POST /api/v1/approvals/{id}/decision`, which accepts only a signed-in session cookie plus an antiforgery token. An agent's bearer key gets `401` there. Cancelling stays on the REST route for requesters.
- **Asynchronous.** `request_approval` returns as soon as the approval is committed. Nothing blocks, polls or calls back. The agent tells the user the `approvalUrl` and calls `get_approval` later. Only `status = approved` (`proceed = true`) read from `get_approval` permits the agent to continue, after re-checking its own permissions and preconditions. A toast, a notification or an opened page is not a decision.
- **Errors** are MCP tool errors (`isError: true`) carrying the HTTP error's message, e.g. the idempotency conflict, a disallowed hashtag, an expiry out of range, or a lost-race `409`.

Not added: `list_approvals`. Requesters have no list query in the service yet (`GET /api/v1/approvals` is still deferred). Adding a list only for MCP would create a second read path. The MCP endpoint has no request rate limit of its own. REST create uses the `publish` limiter, and over MCP creation is bounded by idempotency and the authenticated credential.

Architecture: no ADL change. The tools are a second entry channel onto the existing `ApprovalApi.Request` and `ApprovalApi.Retrieve` operations, the same way MCP `publish_notification` uses `PublicationApi.Publish`. Membership lookup is covered by `ApprovalService`'s declared `dependsOn` on `TenantAdministrationService`. The model compiles unchanged at changeState `sha256:36ec56ff…805e` (attestation `sha256:7f19ee3517699ea7bbb01ac0b29c91fb492471d70e0243c6db727502f54d6c0c`, 0 errors, 16 warnings, 73/73 checks passed).

## One-click native approvals (Windows agent)

Shipped on branch `feature/one-click-approvals`. With one-click approvals enabled, the toast shows plain **Approve** and **Reject** buttons. A click is submitted by the agent straight to the API, with no browser and no second click. The toast changes to *Approving…* or *Rejecting…*, then shows only what the server confirmed: **Approved**, **Rejected**, **Expired**, **Cancelled**, **Already resolved**, **Can't decide from here**, or **Unable to submit** with Retry and Open HashRelay buttons.

The device-pairing token never authorizes a decision. One-click needs a separate, person-granted **decision grant**:

1. **Enrolment.** The tray menu item *Enable one-click approvals…* sends `POST /api/v1/decision-grants` with the device token. It gets a poll code and a short user code, and opens `/approvals/devices?code=XXXX-XXXX`. This is the same device-authorization pattern as pairing; there is no second identity protocol.
2. **The person grants it, interactively.** The page requires a signed-in session. Only the person who paired this device may grant it, and only while they are a member of its workspace. The page states the risk before anything is granted: *anyone who can use this computer while it is unlocked will be able to approve or reject as you*. Granting needs a ticked acknowledgement, and the form carries the antiforgery token. Another member gets "Only the person who paired … can let it approve on their behalf".
3. **Collection.** The agent polls `POST /api/v1/decision-grants/poll` with its device token and the poll code. It receives the `hr_dg_…` secret exactly once; the server keeps only its SHA-256. The agent seals the secret with **DPAPI (current user)** in `%APPDATA%\HashRelay\decision-grant.bin`, separate from `config.json`. The secret never appears in toast arguments.
4. **Deciding.** `POST /api/v1/approvals/{id}/device-decision` requires the device token (policy `api:device-decisions`), the `HashRelay-Decision-Grant` header and a body `{decision, deliveryId}`. The server checks, in order:
   - the grant is active and unexpired (90 days), and is bound to this very device credential and workspace;
   - `deliveryId` is this device's delivery of **the approval's own notification**;
   - then the same `DecideAsync` the page uses: current membership, designated approver, Pending and before expiry, idempotency, compare-and-set, and audit (`via: one-click on {device}`).

   The decision is recorded as the grant holder's.
5. **Revocation.** *Disable one-click approvals* in the tray revokes the grant on the server (`DELETE /api/v1/decision-grants/current`) and deletes the sealed file. The page `/approvals/devices` lists the person's grants, each with a *Turn off* button. Granting again replaces the device's earlier grant. Re-pairing the device issues a new device credential, which invalidates the grant. Grant creation and revocation are audited (`decision_grant.created`, `decision_grant.revoked`).

Why the delivery binding: anyone who may publish to a hashtag can send a look-alike toast that links a real pending approval and says "Tap Approve to dismiss". That toast's delivery belongs to a different notification, so the server refuses it with `403`. The page flow never needed this check, because there the person reads the real request before confirming.

Repeated clicks: the Idempotency-Key is deterministic per toast button (`toast-{approvalId}-{decision}`). A double click, a Retry, or the same click handled by both a running and a relaunched agent records one decision. The other button afterwards gets "Already resolved". Within one process, a click on an approval that is already being submitted is ignored.

Activation: in a running agent, `OnActivated` handles the click. When the agent was closed, Windows starts the process for the click. That process now waits for the decision to finish, up to the HTTP timeout, instead of exiting after a fixed 1.5 s. Both paths use only the sealed grant and the toast arguments, so they behave the same.

The website flow is unchanged and stays the fallback. `POST /api/v1/approvals/{id}/decision` still accepts only a session cookie with antiforgery. Without a grant, the buttons are still **Approve…** and **Reject…** and open the confirmation page.

Code: `Services/Approvals/DecisionGrants.cs` (`ApprovalService` partial), `Pages/ApprovalDevices.cshtml`, `Api/ApprovalEndpoints.cs`, `HashRelay.Client/OneClickApprovals.cs`, `HashRelay.Agent/OneClick.cs`, `Toasts.cs` and `Program.cs`. Tests: `tests/HashRelay.Server.Tests/OneClickApprovalTests.cs`, covering:
- authorized approve and reject;
- the device token alone, a guessed secret, another device's token, and an API key;
- the pairer-only grant, the acknowledgement and the signed-out redirect;
- a non-designated approver;
- a look-alike notification;
- revocation from the web, from the device and by replacement;
- grant expiry and approval expiry;
- concurrent one-click and page decisions, and repeated clicks;
- offline and 5xx failures that never report success;
- the browser flow;
- a fresh process deciding from the sealed grant and the toast arguments alone;
- cross-workspace.

Limits:
- A toast is not updated when the approval is resolved elsewhere; a later click reports the current state.
- Other platforms (macOS, iOS, Android) can reuse the same grant endpoints with their own secure storage (Keychain, Android Keystore), but they are not implemented.

Architecture: `architecture/changes/2026-11-01-device-decision-grants.adl-change` adds:
- record `DecisionGrant` and store `DecisionGrantStore`, owned by `ApprovalService`;
- commands `GrantDeviceDecisions` and `RevokeDeviceDecisions`;
- `ApprovalApi` operations `EnrollDevice` and `RevokeDevice`;
- requirement `DeviceDecisionAuthority`, satisfied by `ApprovalService`.

Applied to `36ec56ff…805e`, it gives changeState `sha256:1f7eab6fb7d82773ef781ce4ce7750147d49f708b92fceeaa2f464234e4b2900`. The authored model compiles to that state (EQUIVALENT, 0 errors). `DecideApproval`, its payload and the lifecycle are unchanged.

## Approval round trip — who records and returns the decision

![Continuum Diagram — actionable approvals round trip](diagrams/hashrelay-approval-roundtrip.svg)

This revised **Continuum Diagram**, generated from the evolved ADL, shows ApprovalService, existing RoutingService/DeliveryService, durable ApprovalStore, ApprovalResolved and ApprovalOutcomeReader. The actual human-to-service and originating publisher-to-service paths remain labeled narrative proposals because ADL does not declare them. The detailed sequence below specifies those proposed interactions.

This sequence is the **proposed implementation contract** grounded in the Continuum model. The model now declares ApprovalService's dependency on RoutingService and TenantAdministrationService and declares ApprovalOutcomeReader subscribing to ApprovalResolved. The concrete HTTPS calls, transactional outbox and originating-system polling are implementation choices documented here, **not falsely claimed to be executable ADL workflow semantics**.

```mermaid
sequenceDiagram
    autonumber
    participant Origin as Originating system
    participant Approval as ApprovalService
    participant Store as ApprovalStore + Outbox
    participant Routing as Existing RoutingService
    participant Delivery as Existing DeliveryService
    actor Human as End user
    participant Reader as ApprovalOutcomeReader

    Origin->>Approval: POST /approvals (idempotency key, approver, expiry, correlationId)
    Approval->>Approval: Validate tenant, requester and designated approver
    Approval->>Store: Commit Pending approval + audit + notification intent
    Store-->>Approval: approvalId
    Approval-->>Origin: 201 Pending (approvalId; status URL)
    Approval->>Routing: Publish linked notification to hashtags
    Routing-->>Delivery: NotificationAccepted
    Delivery-->>Human: Native toast / mobile push (approvalId link)
    Human->>Approval: Open first-party approval page (authenticated)
    Approval->>Store: Read current state and approver entitlement
    Store-->>Approval: Pending, version, expiry
    Approval-->>Human: Show details and Approve / Reject
    Human->>Approval: POST /approvals/{id}/decision (approve or reject)
    Approval->>Approval: Recheck human identity, eligibility and expiry
    Approval->>Store: Atomic Pending -> Approved/Rejected + audit + outbox
    alt First valid decision wins
        Store-->>Approval: Committed terminal state
        Approval-->>Human: Decision confirmed
        Store-->>Reader: ApprovalResolved (via durable outbox)
        Reader-->>Origin: Result available / event hint
        Origin->>Approval: GET /approvals/{id}
        Approval->>Store: Read authoritative terminal state
        Store-->>Origin: Approved/Rejected + actor + time + correlationId
        Note over Origin: Revalidate business preconditions; execute only when Approved
    else Already terminal or expired
        Store-->>Approval: Conflict / final state
        Approval-->>Human: Existing decision / expired; no new transition
    end
```

**Where the decision lives:** ApprovalStore is the server's transactional source of truth; the decision row, immutable audit entry, and outbox event are committed atomically. SignalR and push deliver *information*, not authoritative state. If the client disconnects after clicking Approve, it can re-read status; the server cannot undo a committed decision merely because its response was lost.

**How the originating system learns:** It stores `approvalId` and `requesterCorrelationId` returned by creation. It can poll the authenticated GET status endpoint at any time, or wake up on an at-least-once `ApprovalResolved` event and then GET. An offline requester can recover without a persistent socket; duplicated events are deduplicated by approval ID and state version. `ApprovalOutcomeReader` is modeled as an internal completion subscriber, **not as a guaranteed external webhook**. Do not suggest that directly pushing events to arbitrary systems already exists. 

**What approval authorizes:** It is a durable human decision, not an executable delegation to HashRelay. The originating system still validates authorization scope, original request identity, target release/environment, and current business preconditions before doing anything.

**Example:** A deployment service requests approval for release v2.4.1 and receives `approvalId=123`. Your Windows device displays the request. You open the authenticated decision page and approve. HashRelay records `Approved` plus the actor/time and emits a durable completion notification. The deployment service sees that completion, GETs approval 123, confirms it still matches release v2.4.1, and runs the deployment using its own credentials. If the requester was offline, it retrieves that same Approved state on restart.

**Model fidelity:** Continuum Diagram generated a revised round-trip editorial view from ADL attestation `sha256:44127a98aa3fa1f667c2f57ef2ac9e3a5c4664b703aac6f73c3cecad4e4d56ea` (run `7c89f31a-b716-473b-bd7c-de40a9854e24`). It reports that no formal Workflow elements exist, so the sequence above is explicitly a proposed contract, not an ADL-validated runtime trace.

## Continuum Diagram — approval architecture in context

![Continuum Diagram C3: actionable approvals](diagrams/hashrelay-actionable-approvals.svg)

Generated with Continuum Diagram from the draft ADL at attestation `sha256:44127a98aa3fa1f667c2f57ef2ac9e3a5c4664b703aac6f73c3cecad4e4d56ea` (run `ecfbf24c-2999-4a86-9a06-5bd8bb36def5`). The reconstructed editable SVG passed geometric, semantic fidelity, and containment checks. It is **not** a claim that the draft architecture is accepted or its proposed cross-context integrations already implemented. The Routing-to-Delivery route is deliberately drawn with a weaker dotted grade by the renderer, although Routing publishes and Delivery subscribes to `NotificationAccepted` in the ADL.

### Existing behavior versus proposed approval behavior

| Concern | Existing HashRelay | Actionable approval enhancement |
| --- | --- | --- |
| Submission | An authorized publisher sends a notification to `POST /api/v1/notifications` or MCP `publish_notification` | Authorized requester creates a separate `Approval` resource and an ordinary linked notification is issued |
| Routing | `RoutingService` authorizes workspace/tag matching and emits `NotificationAccepted` | Reuse RoutingService for attention/hashtag addressing; it does not own approval state |
| Delivery | `DeliveryService` fans out to eligible endpoints by realtime/FCM and records delivery status | Multiple endpoint presentations refer to **one approval ID**, never separate approvals |
| Identity | Tenant and credential scopes already gate publishing and subscriptions | Existing tenant isolation is reused, but a real authenticated human's approver entitlement must also be checked |
| Action | Notification may carry informational URL and can be acknowledged | Open a first-party authenticated detail page; Approve/Reject sends a decision command, not an acknowledgement |
| Durable authority | Notification, subscription, delivery and endpoint persistence | `ApprovalStore` is the sole authority for Pending/Approved/Rejected/Expired/Cancelled; delivery status never changes approval state |
| Completion | A notification can be read or acknowledged | `ApprovalResolved` and requester GET expose the terminal result; requester independently executes authorized business work |

### Walkthrough: deployment requires approval

1. A deployment publisher authenticates using its workspace-bound credential and calls `POST /api/v1/approvals`. `ApprovalService` verifies allowed approvers, writes **Pending** and a durable audit record, and associates the approval ID with a notification.
2. Existing `RoutingService` receives the publication, matches its hashtags and emits `NotificationAccepted`. `DeliveryService` routes the normal notification to subscribed Windows/mobile endpoints. **Neither delivery nor toast presentation decides anything.**
3. The user opens the linked HashRelay approval page. The page authenticates the interactive user and retrieves current approval status, verifies actor eligibility, and displays details and expiry. Clicking Approve POSTs `DecideApproval` to `ApprovalService`; clicking a stale Windows toast cannot bypass this check.
4. `ApprovalService` atomically changes Pending to Approved, records actor/time/audit and a durable completion outbox entry. A competing Reject on another device loses, reads final state and cannot reverse it.
5. The requester eventually receives `ApprovalResolved` or retrieves the authoritative result through GET, validates its own preconditions and performs its deployment. HashRelay never performs that deployment.
6. If the device is offline, the notification can be synchronized later but the approval remains governed by its own expiry and final status. A late toast opens the current status; it does not offer a still-active button for an expired/resolved request.

### Architecture relationships still needing explicit declaration

The updated ADL **declares** `RoutingService publishes NotificationAccepted`, `DeliveryService subscribesTo NotificationAccepted`, `ApprovalService publishes ApprovalResolved`, `ApprovalOutcomeReader subscribesTo ApprovalResolved`, plus ApprovalService dependencies on RoutingService and TenantAdministrationService. It does **not** declare a complete execution workflow, concrete end-user interaction calls, the exact notification publication protocol, or the initiating publisher's authenticated retrieval path. Those are explicit implementation contracts above rather than fictional declared edges. The model compiles with zero errors and 12 warnings; review remaining ownership and verification obligations before architecture acceptance.


> **Integration note (2026-11-01):** The current architecture was generated by applying `architecture/changes/2026-11-01-actionable-approvals.adl-change` to the catch-up/attribution baseline using Continuum. Applied state and recompiled emitted state both equal `sha256:44127a98aa3fa1f667c2f57ef2ac9e3a5c4664b703aac6f73c3cecad4e4d56ea`. Existing diagrams were produced from the preceding approvals-only draft and are explanatory references, **not renderings of this new integrated attestation**. Review the 15 design warnings before ratifying the architecture.

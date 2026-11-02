# VALIDATION ONLY: continuum `adl/1.7` command admission on HashRelay

This record lives on the throwaway branch `experiment/command-admission-20261101`. It is
**not for merge**. No PR, no deploy. The toolchain used is the continuum working tree
carrying ADR-0020 (`tools/adlc.mjs`, adlc 0.2.0, 2026-11-01).

## Digests

| State | attestation | change-state |
|---|---|---|
| `origin/main` 89327fc, core `adl/1.4` | `sha256:9c827c98c1dd61459b265d088f07219f83ba6b79ed9724f353b09e6a307d2be2` | `sha256:1f7eab6fb7d82773ef781ce4ce7750147d49f708b92fceeaa2f464234e4b2900` |
| re-pinned to `adl/1.7` (manifest only, a21f8ed) | `sha256:8b442c7ed04a28c80fb1eb64ba6d89964e04c2ea6457c7b2dd865320fddd2626` | `sha256:2f161aaa96b9b7ab3f70227180d64add25627ec2a1dba47835b49708e73f97d9` |
| after `2026-11-01-command-admission.adl-change` | `sha256:d7b0fe012d2f6022ac060c071a6c2d15f95d0cddf2d9c7ef7561f1b56437406f` | `sha256:31c405668b668df95b9bb803dbf14262af3f77da50133b8a6a9351b14ecfb4a2` |

- **The re-pin is compatible.** It moves the digest and nothing else: 0 errors before and
  after, and the same 17 `unrealized-intent` warnings (none new).
- **The change applies.** `adlc … --apply … --emit-source` reports `applied`, with
  0 errors and 17 design warnings (the same 17). The emitted package recompiles to
  `31c40566…`.
- **The authored model is verified.** The hand-edited `hash-relay.adl` compiles to
  change-state `31c40566…`, the same as the applied state. Ledger: 78 passed, 0 failed.

## What the model now states (from the code)

| Command | Admitted | Code evidence |
|---|---|---|
| DecideApproval | `AccountHolder` (direct) | `/{id}/decision` cookie session + antiforgery; `Pages/Approval` |
| DecideApproval | `PairedDevice` on behalf of `AccountHolder`, requiring `DecisionGrant` and `NotificationDelivery` for `approvalId` | `/{id}/device-decision` (CredentialKind Device) → `DecideFromDeviceAsync`: an active, unexpired grant for this device; a Delivery to this endpoint whose `NotificationId` is the approval's own; recorded as `g.AccountId` |
| RequestApproval | `Publisher` | `PolicyRequester` (Api + publish, or Service + approvals:create); REST and MCP `request_approval` alike |
| PublishNotification | `Publisher` | `PolicyPublish`; REST and MCP `publish_notification` alike |

Two elements were added, both backed by code:

- `actor PairedDevice` (the device credential kind);
- `record NotificationDelivery` (the `Delivery` entity, `Data/Entities.cs`).

## The required distinctions: probes against the result state

Each probe is a change applied to `31c40566…`. All of them are refused, as intended.

| Probe | Result |
|---|---|
| a notification publisher (as a sending system) sends DecideApproval | `adl/semantic/unadmitted-sender`, required `admission`, missing the sender |
| an MCP client integration sends DecideApproval | `adl/semantic/unadmitted-sender` |
| the device delegation without evidence | `adl/semantic/admission-delegation`, required `evidence` |
| the device delegation with no direct `AccountHolder` admission (elevation) | `adl/semantic/admission-delegation`, required `principal-admission` |
| the delivery evidence bound to `notificationId` (not a payload member) | `adl/semantic/admission-binding` |
| the publisher delegated without evidence | `adl/semantic/admission-delegation`, required `evidence` |

## What the model does NOT establish

The model states permission and obligation only. It does not establish:

- that the cookie session is authenticated;
- that the grant check runs, or that it compares workspace and membership;
- that the delivery comparison is the approval's own.

Those are the code's to enforce and the tests' to evidence. The delivery linkage is stated as
`NotificationDelivery for approvalId`. It does **not** say *which* member of the delivery
must match (`notificationId` = the approval's `NotificationId`). That is the known Phase 1
limitation: there is no correlation equation (ADR-0020 §9).

**MCP as a channel is not distinguished.** A Publisher credential reaching RequestApproval by
MCP and by REST is one admission. "No MCP route reaches DecideApproval" holds today only
because Publisher is not admitted there on any channel. Telling the channels apart is
Phase 2 (`Operation.enters`, `via`).

## CI

`continuum.yml` runs on every push. On this branch its pinned toolchain predates `adl/1.7`,
so the architecture gate is expected to refuse the `adl/1.7` pin. `build.yml` (the deploy
path) runs only on `main`.

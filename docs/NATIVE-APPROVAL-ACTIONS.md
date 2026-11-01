# Native approval notification actions — client slice (2026-11-01)

## Implemented in this branch

The Expo iOS/Android client registers a native notification category with **Review / Approve** and **Review / Reject** buttons. Locally displayed approval notifications whose URL is an HTTPS `/approvals/{uuid}` resource receive that category. Action responses navigate to the HashRelay approval page **only when its origin matches the paired server origin**. The web page owns human sign-in, anti-CSRF validation, current membership, expiry and the one-way decision transition. Buttons never authorize with the paired device token or decide implicitly.

This is a **review-intent workflow**, not an authenticated one-tap decision, and it affects locally displayed notifications only. OS appearance and availability vary. Do not advertise mobile remote-push actions as completed.

## Work still required

1. **FCM remote push**: use platform-specific Android notification actions and iOS APNs category payload (via Firebase) for background/terminated-app delivery. Verify Expo notification-response listeners receive remote action identifiers on each OS and avoid duplicate foreground display.
2. **Windows agent**: done. Without a decision grant, the buttons open the first-party confirmation page. With one, they are one-click decisions under a person-granted, DPAPI-sealed grant bound to the device credential and to the approval's own delivery. See *One-click native approvals* in `ACTIONABLE-APPROVALS.md`. Still to verify: activation with a signed, installed agent in an interactive session.
3. **macOS client**: implement `UNNotificationCategory` and `UNNotificationAction` when a macOS endpoint exists; no macOS client is shipped by this change.
4. **Lifecycle**: refresh or suppress terminal/expired approvals across devices; stale actions must never decide anything.
5. **Verification**: build TypeScript, Android emulator, iOS physical device, offline/replay, spoofed foreign URL, signed-out account, cross-tenant, concurrent decisions and expiry boundary. No local device build or runtime test has been executed through this GitHub-only change.

## Security invariant

An actionable notification is **untrusted presentation of intent**. Its URL, payload and action id confer no authority. The existing `POST /api/v1/approvals/{id}/decision` stays signed-in-cookie + CSRF only; non-human bearer credentials never decide. A truly one-tap authenticated action needs a separate, reviewed, human-authenticated client mechanism, not device credentials or bearer tokens in notification payloads. That mechanism is the **decision grant**: the pairer signs in and grants it explicitly after an unlocked-PC warning. It is bound to one device credential and one workspace, it is revocable, it is sent only together with the device token and the delivery id of the approval's own notification, and it is re-checked against membership and designation on every decision. Mobile clients may adopt it with platform secure storage; until they do, their buttons stay review-intent only.

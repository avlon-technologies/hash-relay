# Native approval notification actions — client slice (2026-11-01)

## Implemented in this branch

The Expo iOS/Android client registers a native notification category with **Review / Approve** and **Review / Reject** buttons. Locally displayed approval notifications whose URL is an HTTPS `/approvals/{uuid}` resource receive that category. Action responses navigate to the HashRelay approval page **only when its origin matches the paired server origin**. The web page owns human sign-in, anti-CSRF validation, current membership, expiry and the one-way decision transition. Buttons never authorize with the paired device token or decide implicitly.

This is a **review-intent workflow**, not an authenticated one-tap decision, and it affects locally displayed notifications only. OS appearance and availability vary. Do not advertise mobile remote-push actions as completed.

## Work still required

1. **FCM remote push**: use platform-specific Android notification actions and iOS APNs category payload (via Firebase) for background/terminated-app delivery. Verify Expo notification-response listeners receive remote action identifiers on each OS and avoid duplicate foreground display.
2. **Windows agent**: add Windows App SDK action arguments carrying only the approval id, route activation through first-party session-confirmation, and verify notification activation with a signed installed agent.
3. **macOS client**: implement `UNNotificationCategory` and `UNNotificationAction` when a macOS endpoint exists; no macOS client is shipped by this change.
4. **Lifecycle**: refresh or suppress terminal/expired approvals across devices; stale actions must never decide anything.
5. **Verification**: build TypeScript, Android emulator, iOS physical device, offline/replay, spoofed foreign URL, signed-out account, cross-tenant, concurrent decisions and expiry boundary. No local device build or runtime test has been executed through this GitHub-only change.

## Security invariant

An actionable notification is **untrusted presentation of intent**. Its URL, payload and action id confer no authority. The existing `POST /api/v1/approvals/{id}/decision` stays signed-in-cookie + CSRF only; non-human bearer credentials never decide. A truly one-tap authenticated action needs a separate reviewed human-authenticated client mechanism, not device credentials or bearer tokens in notification payloads.

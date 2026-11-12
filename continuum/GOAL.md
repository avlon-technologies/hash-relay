# Goal — HashRelay M2: Desktop portability

A person who uses a Mac or a Linux desktop installs HashRelay for themselves, without
administrator rights, pairs it with their workspace once, and from then on receives their
hashtag notifications natively in their own session, exactly as a Windows user already does:
live notifications appear at once, what arrived while they were away arrives as one catch-up
summary, nothing is shown twice, and approval notifications can be reviewed, or decided under
a grant they gave, with the same rules everywhere.

The agent starts when they sign in, keeps its credential and any decision grant in their own
secret store, tells them (and a supporter) what state it is in without ever revealing a
secret, updates itself only to releases that HashRelay signed, and leaves nothing behind that
could receive or decide on their behalf when they uninstall it.

Success feels like: the same protocol and the same behaviour on three platforms, with the
platform differences confined to how notifications are shown, how the agent starts and
updates, and where it keeps its secrets.

This package rests on the PROPOSED change `architecture/changes/2026-11-11-m2-desktop-portability.adl-change` (repository root)
(state B = apply(A, change)); its macOS and Linux technology choices are proposals awaiting
human confirmation.

The Goal explains intent. The semantic ADL defines architectural
meaning. If they conflict, the semantic model governs.

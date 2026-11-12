# M2 Desktop portability — PROPOSED decisions (not accepted)

Status: **PROPOSED, awaiting human confirmation.** Authored 2026-11-11 by an AI agent on branch
`continuum/m2-desktop-portability`. None of these is in the Host-signed decision record
(`architecture/decisions/decisions.jsonl`), none is accepted, and nothing here changes the
decision-authority policy.

Why they are not in the decision record yet: in this space an agent may propose only through a Host
delegation token (`agent-architect` grant). This session was signed in as the owner, and a proposal made
under the owner's identity would make the owner the author, so under the rule
`a-person-who-did-not-author-it-approves` the owner could not then accept it. To put one into the
record, the owner mints a delegation and the agent runs, for example:

```
continuum decision propose --help        # then propose each D-M2-xx below with its scope and expectations
```

Each decision names the binding entries that carry it (`continuum/model/*.adl-binding.json`). The ADL change
(`architecture/changes/2026-11-11-m2-desktop-portability.adl-change`) contains no technology, so every
decision below can change without touching the attestation.

| id | decision (proposed default) | main alternatives | why this default | carried by |
|---|---|---|---|---|
| D-M2-01 | **macOS agent = .NET 10 `net10.0-macos` app bundle** (Microsoft.macOS SDK): AppKit `NSStatusItem` menu-bar extra (LSUIElement), `UNUserNotificationCenter` with a `UNNotificationCategory` carrying Approve/Reject actions. Minimum macOS 13, universal arm64+x64. | Avalonia UI for macOS+Linux with native notification shims; Swift shell hosting the .NET client as a helper; MAUI Mac Catalyst | Keeps one language and reuses `src/HashRelay.Client` directly; `UNUserNotificationCenter` needs a signed `.app` bundle identity, which this gives natively; macOS 13 is required by `SMAppService` (D-M2-05) | macOS `DesktopAgent`, `AgentRuntime` |
| D-M2-02 | **Linux notifications = `org.freedesktop.Notifications` over the session D-Bus** (managed D-Bus client, e.g. Tmds.DBus.Protocol), `desktop-entry` hint for identity; actions only when `GetCapabilities` lists `actions`, otherwise the default action opens the review page. | libnotify via P/Invoke (native dependency, same D-Bus underneath); `xdg-desktop-portal` Notification (required if sandboxed, see D-M2-03) | No native library to ship; capability probing is explicit, which is what `NativeActionFallback` requires | Linux `AgentRuntime` |
| D-M2-03 | **Linux packaging = signed self-contained `linux-x64` tarball + per-user `install.sh --user`** into `~/.local/share/hashrelay`, `~/.local/bin` symlink, `.desktop` entry. | AppImage (needs FUSE 2, missing by default on recent Ubuntu); Flatpak `--user` (sandbox: portals for notifications, background, secrets — larger change, good later); `.deb`/`.rpm` (need root, violate `PerUserLifecycle`) | Only option that is per-user, needs no root and no runtime dependency, and keeps the in-agent updater (D-M2-07) simple | Linux `DesktopAgent` |
| D-M2-04 | **macOS distribution = Developer ID signed, hardened-runtime, notarized and stapled `.dmg`**; user drags to `~/Applications` (no admin) or `/Applications`. CI on a GitHub Actions macOS runner with the signing identity and App Store Connect API key as secret locators. | `.pkg` with "install for me only"; Mac App Store (sandbox, no self-update) | `.dmg` of a notarized app needs no installer privileges and is what Gatekeeper expects | macOS `DesktopAgent` |
| D-M2-05 | **Start-at-login: macOS `SMAppService.mainApp` login item; Linux XDG autostart** (`~/.config/autostart/ca.avlon.HashRelay.desktop`). | macOS `~/Library/LaunchAgents` plist; Linux `systemd --user` unit | Both start inside the graphical session (session D-Bus, display) where notifications work, and are user-visible and user-removable; `systemd --user` may start before the graphical session and lacks its environment | `DesktopAgent` (both) |
| D-M2-06 | **Secrets: macOS login Keychain generic-password items; Linux Secret Service API** (gnome-keyring/KWallet). No Secret Service: credential in a `0600` file, degraded custody reported, grants refused. | Encrypted file with a key in config (no real protection); refuse to run without a secret store | Matches `LocalCredentialCustody` exactly; the fallback keeps headless/minimal desktops usable without pretending to protect a grant | `DeviceVault` (both) |
| D-M2-07 | **Updates: one in-agent updater in `HashRelay.Client`** reading a release manifest from the release channel (GitHub Releases of this repo), manifest signed with an ECDSA P-256 key held in Azure Key Vault and pinned in the agent; artifact SHA-256 checked; macOS additionally checks the new bundle's Developer ID team before swapping; atomic per-user swap; a failed update leaves the old version running. | Sparkle 2 on macOS (EdDSA appcast; native framework); OS package managers on Linux; no auto-update (manual reinstall as on Windows today) | One verification path for both platforms, implementable in .NET without native deps; reuses existing Azure signing infrastructure custody; Windows can adopt it later | `DesktopAgent`, `AgentRuntime` (both), `AgentReleaseChannel` |
| D-M2-08 | **Diagnostics: `hashrelay agent status [--json]` and `hashrelay agent diagnostics --export <zip>`**, plus a menu item; logs in `~/Library/Logs/HashRelay` and `$XDG_STATE_HOME/hashrelay/logs`; credential and grant redacted by construction (never logged). | Upload diagnostics to the server | Local-only keeps the privacy boundary simple | `DesktopAgent` (both) |
| D-M2-09 | **Uninstall forgets the device**: stop the agent, remove start-at-login and notification registration, send `RevokeDeviceDecisions` for any grant, delete local secrets. The server endpoint record stays (listed offline) until removed in the dashboard — see BDL `UninstallServerDisposition` (unresolved). **Also apply to Windows** in a follow-up: today the Windows uninstaller leaves `%APPDATA%\HashRelay` (plaintext credential, DPAPI grant) and revokes nothing, and the credential is plaintext in `config.json`. | Keep pairing across uninstall/reinstall | `PerUserLifecycle`/`LocalCredentialCustody` as proposed; Windows divergence is recorded, not hidden | `DesktopAgent`, `DeviceVault` (all three envs) |
| D-M2-10 | **Supported Linux desktops at M2: Ubuntu LTS and Fedora Workstation (GNOME), KDE Plasma; x64 only.** GNOME shows no tray without an extension, so the Linux agent must work headless (CLI + notifications). | Any freedesktop desktop, best effort; arm64 at M2 | Bounded test matrix; matches BDL `SupportedLinuxDesktops` (unresolved) | Linux `DesktopAgent` |
| D-M2-11 | **Linux pairing UX: browser device-authorization flow started from `hashrelay login`/tray**, code shown in a notification and in the terminal; no GUI toolkit dependency. | Avalonia pairing window (shared with macOS) | Avoids a GUI toolkit on Linux; the device flow already works from the CLI | Linux `DesktopAgent` |

## Language/model limits recorded, not modelled around

- ADL forbids `Service writes Resource[secret-store]` (`adl/architectural/secret-store-write`). The agent does
  write its credential and grant at pairing/granting. `DeviceVault` stays a secret-store, read by
  `AgentRuntime`; its description states the provisioning path. Needs a language/owner ruling if the write
  must be modelled.
- Environment completeness requires every Environment to bind every owned element, including the mobile
  app and the cloud services, in the macOS and Linux environments. Those bindings say "unchanged by M2".

# HashRelay

**Notifications, addressed by hashtag.** Publish a message to `#builds` from any CLI, script, CI job or AI agent and it
pops up in Windows Notification Center on every device you subscribed. Hashtags, API keys and devices are scoped to a
tenant workspace; nothing crosses the boundary.

```
hashrelay subscribe #builds
hashrelay publish #builds "Build complete" "212 tests passed in 48s"
```

| Component | Path | What it is |
|---|---|---|
| Cloud | `src/HashRelay.Server` | ASP.NET Core 10: dashboard (sign up, API keys, devices, activity), HTTP API `/api/v1`, SignalR hub `/hubs/notifications`, SQLite store |
| Contracts | `src/HashRelay.Contracts` | Wire models and hashtag normalisation shared by everything |
| Client | `src/HashRelay.Client` | .NET client: config store, typed HTTP API, realtime listener with infinite reconnect |
| CLI | `src/HashRelay.Cli` | `hashrelay` — pair, subscribe, publish, listen, test, status |
| Agent | `src/HashRelay.Agent` | Windows tray agent: native toasts, pairing window, start with Windows |
| Mobile | `src/HashRelay.Mobile` | Expo/React Native app for iOS and Android: pair the phone, subscribe to hashtags, inbox, publish; push via Firebase Cloud Messaging |
| Installer | `installer/HashRelay.iss` | Per-user Inno Setup EXE (no admin), agent + CLI, PATH, autostart, clean uninstall |
| Tests | `tests/HashRelay.Server.Tests` | xunit v3 integration tests over the real server |
| Infra | `infra/terraform` | Azure Container Apps (consumption, scales to zero) + Azure Files for SQLite + container registry + custom domain with managed certificate |
| Architecture | `architecture/`, `docs/` | Continuum ADL/BDL model, development contract, ADRs |

## Quick start (hosted)

1. Create a workspace at **https://hashrelay.dev.avlon.ca/signup**.
2. Download and run **HashRelay-Setup.exe** from `/downloads`. It installs per user, puts `hashrelay` on your PATH and
   opens a pairing window. Approve the code in your browser.
3. In a new terminal: `hashrelay subscribe #builds`, then `hashrelay test`. A toast appears.
4. From CI or a server, create an API key in the dashboard and `POST /api/v1/notifications` with
   `Authorization: Bearer hr_live_…`. Full reference at `/docs`.

## Build from source

Requirements: .NET 10 SDK, Node 18+ (icon generation), Inno Setup 6 (installer; `winget install JRSoftware.InnoSetup --scope user`).

```powershell
./build.ps1                               # tests, server (linux-x64), CLI + agent (win-x64), installer, downloads
./build.ps1 -Server http://127.0.0.1:5075 # point the installed clients at a local server
dotnet run --project src/HashRelay.Server # http://127.0.0.1:5075
dotnet run --project tests/HashRelay.Server.Tests
```

Deploy to Azure (needs `az login` and Terraform; no local Docker, the image is built by `az acr build`):

```powershell
./deploy.ps1 -Build        # terraform apply (registry first, then the rest) + image build + rollout + managed cert
./deploy.ps1 -SkipInfra    # code-only redeploy: rebuild the image and roll it out
```

The environment scales to zero: Container Apps (consumption plan, 0..1 replicas) runs the server, an Azure Files
share holds the SQLite database and data-protection keys, and a Basic container registry holds the image. With no
browser or agent connected nothing runs; the first request wakes the app in a few seconds. Connected agents keep
the single replica alive while they are online.

## How it works

- **Workspaces & credentials.** An account owns one or more tenants. Publisher **API keys** (`hr_live_…`) are issued
  in the dashboard, shown once, stored as SHA-256 verifiers, and carry explicit `publish` / `subscribe` scopes.
  Sign-in is email + password (PBKDF2). **Forgot password** at `/forgot-password` emails a single-use link that
  expires in an hour (`HashRelay:Email:Smtp` configures delivery; unconfigured servers only log the link). Operators
  can also run `HashRelay.Server reset-password EMAIL` inside the container.
  Installed clients never hold them: the agent and CLI pair with a **device token** (`hr_dev_…`) through an
  OAuth-style device flow approved at `/pair`, bound to one endpoint, revocable from *Devices*.
- **Routing.** `POST /api/v1/notifications` normalises tags, matches the *union* of subscribers in the caller's
  tenant and records one delivery per endpoint. Connected endpoints receive it live over SignalR; offline ones get it
  replayed on their next `Hello` within the replay window (24 h). `notificationId` makes retries idempotent; a
  reused id with a different payload is `409`.
- **Rich content.** A toast can show plain text and one image, nothing more. So `markdown` (CommonMark, tables,
  task lists, ```` ```mermaid ```` fences) and `diagram` (Mermaid source) are rendered on a per-notification page at
  `/n/{id}` that the toast opens on click; the toast body is the first paragraph of the markdown. Diagrams are also
  rendered to PNG server-side (via mermaid.ink by default, Kroki supported, `HashRelay:Diagrams`) and shown inside the
  toast; `POST /api/v1/images` uploads a picture for `imageUrl`. CLI: `--markdown[-file]`, `--diagram[-file]`, `--image`.
- **Transport.** Groups are per-endpoint addresses only; subscriptions live in the database and are the sole matching
  authority. The agent reconnects with backoff forever, re-registers after every reconnect, acks displayed deliveries
  and suppresses duplicates.
- **Errors.** `{code, message, traceId, details}` with `400/401/403/404/409/429`. CLI exit codes:
  `0` ok, `2` invalid input, `3` authentication, `4` server unavailable, `5` rejected, `10` unexpected.

## Repository layout notes

`architecture/` holds the Continuum ADL/BDL model that this implementation realises; `docs/WINDOWS-M1-CONTRACT.md`
is the development contract the protocol follows. The Continuum git hooks in `.githooks` are no-ops until a
`continuum/` package exists.

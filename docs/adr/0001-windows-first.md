# ADR-0001 — Windows-first delivery boundary
Status: Accepted for M1 development, 2026-10-31. Now in the decision record (architecture/decisions/DECISIONS.md): the desktop-agent boundary is ADR-002, and ADR-001 replaces "Offline delivery is not promised" below.

M1A is loopback-only ASP.NET Core/SignalR plus per-user Windows notification agent, CLI/SDK and per-user unpackaged installer. M1B is tenant-aware hosted API/web dashboard and Azure SignalR provisioned by Terraform. Do not confuse the milestones.

For M1A, SignalR carries messages to a connected user-session agent; Windows App SDK AppNotificationManager displays native notifications. A Windows service must not attempt to render user notifications. Use self-contained x64 Windows deployment with per-user Inno Setup installer. Offline delivery is not promised. For M1B, developer API keys belong to the tenant and are trusted-server credentials; installed client apps use subscriber-limited OAuth/PKCE credentials instead.

Microsoft references: https://learn.microsoft.com/en-us/windows/apps/develop/notifications/ ; https://learn.microsoft.com/windows/apps/develop/notifications/app-notifications/app-notifications-dotnet ; https://learn.microsoft.com/en-us/azure/azure-signalr/signalr-howto-authorize-managed-identity

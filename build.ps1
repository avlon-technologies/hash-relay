<#
.SYNOPSIS
  Builds HashRelay end to end: server (linux-x64 for App Service), Windows agent + CLI (self-contained win-x64),
  the per-user Inno Setup installer, and copies the installer + CLI zip into the server's downloads folder.

.PARAMETER Server
  Public URL baked into the agent and CLI as the default server. Defaults to https://hashrelay.dev.avlon.ca.

.PARAMETER SkipInstaller
  Skip the Inno Setup step (e.g. on a machine without ISCC).

.EXAMPLE
  ./build.ps1
  ./build.ps1 -Server http://127.0.0.1:5075 -Configuration Debug
#>
[CmdletBinding()]
param(
  [string]$Server = "https://hashrelay.dev.avlon.ca",
  [string]$Configuration = "Release",
  [switch]$SkipInstaller,
  [switch]$SkipTests
)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$artifacts = Join-Path $root "artifacts"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_NOLOGO = "1"

# Prefer the user-local SDK installed by dotnet-install.ps1 when the machine-wide one is missing.
$userDotnet = Join-Path $env:USERPROFILE ".dotnet\dotnet.exe"
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue) -or -not (dotnet --list-sdks 2>$null)) {
  if (Test-Path $userDotnet) { $env:DOTNET_ROOT = Split-Path $userDotnet; $env:PATH = "$env:DOTNET_ROOT;$env:PATH" }
}

$version = ([xml](Get-Content (Join-Path $root "Directory.Build.props"))).Project.PropertyGroup.Version
Write-Host "HashRelay $version  (default server: $Server)" -ForegroundColor Cyan

New-Item -ItemType Directory -Force $artifacts | Out-Null
Remove-Item -Recurse -Force (Join-Path $artifacts "agent"), (Join-Path $artifacts "cli"), (Join-Path $artifacts "server") -ErrorAction SilentlyContinue

Write-Host "`n== Icon" -ForegroundColor Cyan
node (Join-Path $root "build\make-icon.mjs")

if (-not $SkipTests) {
  Write-Host "`n== Tests" -ForegroundColor Cyan
  dotnet run --project (Join-Path $root "tests\HashRelay.Server.Tests\HashRelay.Server.Tests.csproj") -c $Configuration
  if ($LASTEXITCODE -ne 0) { throw "Tests failed." }
}

Write-Host "`n== Server (linux-x64, framework-dependent)" -ForegroundColor Cyan
dotnet publish (Join-Path $root "src\HashRelay.Server\HashRelay.Server.csproj") -c $Configuration -r linux-x64 --self-contained false -o (Join-Path $artifacts "server")
if ($LASTEXITCODE -ne 0) { throw "Server publish failed." }

Write-Host "`n== CLI (win-x64, self-contained single file)" -ForegroundColor Cyan
dotnet publish (Join-Path $root "src\HashRelay.Cli\HashRelay.Cli.csproj") -c $Configuration -r win-x64 -p:HashRelayServer=$Server -o (Join-Path $artifacts "cli")
if ($LASTEXITCODE -ne 0) { throw "CLI publish failed." }

Write-Host "`n== Agent (win-x64, self-contained)" -ForegroundColor Cyan
dotnet publish (Join-Path $root "src\HashRelay.Agent\HashRelay.Agent.csproj") -c $Configuration -r win-x64 -p:HashRelayServer=$Server -o (Join-Path $artifacts "agent")
if ($LASTEXITCODE -ne 0) { throw "Agent publish failed." }
Remove-Item (Join-Path $artifacts "agent\*.pdb"), (Join-Path $artifacts "cli\*.pdb") -ErrorAction SilentlyContinue

Write-Host "`n== CLI zip" -ForegroundColor Cyan
$cliZip = Join-Path $artifacts "hash-relay-cli-win-x64.zip"
Remove-Item $cliZip -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $artifacts "cli\hash-relay.exe") -DestinationPath $cliZip

$setup = Join-Path $artifacts "HashRelay-Setup.exe"
if (-not $SkipInstaller) {
  Write-Host "`n== Installer (Inno Setup)" -ForegroundColor Cyan
  $iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
  ) | Where-Object { Test-Path $_ } | Select-Object -First 1
  if (-not $iscc) { throw "Inno Setup 6 not found. Install with: winget install JRSoftware.InnoSetup --scope user  (or pass -SkipInstaller)" }
  & $iscc /Q "/DAppVersion=$version" "/DAgentDir=$artifacts\agent" "/DCliDir=$artifacts\cli" "/DOutputDir=$artifacts" (Join-Path $root "installer\HashRelay.iss")
  if ($LASTEXITCODE -ne 0) { throw "Installer build failed." }
}

Write-Host "`n== Publish downloads into the server" -ForegroundColor Cyan
$downloads = Join-Path $root "src\HashRelay.Server\wwwroot\downloads"
New-Item -ItemType Directory -Force $downloads | Out-Null
Copy-Item $cliZip $downloads -Force
if (Test-Path $setup) { Copy-Item $setup $downloads -Force }
# the server publish output already happened; refresh its copy of the downloads too
$serverDownloads = Join-Path $artifacts "server\wwwroot\downloads"
New-Item -ItemType Directory -Force $serverDownloads | Out-Null
Copy-Item (Join-Path $downloads "*") $serverDownloads -Force

Write-Host "`n== Done" -ForegroundColor Green
Get-ChildItem $artifacts -File | ForEach-Object { "{0,12:N0} KB  {1}" -f ($_.Length / 1KB), $_.Name }

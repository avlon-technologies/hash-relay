<#
.SYNOPSIS
  Signs Windows binaries with Azure Artifact Signing. No certificate on disk: signtool talks to the service
  through Microsoft's dlib, and the service issues a short-lived certificate per signature.

.DESCRIPTION
  Authenticates as the signed-in Azure CLI identity (az login on a workstation; azure/login with OIDC in
  GitHub Actions). That identity needs the "Artifact Signing Certificate Profile Signer" role on the account
  named in build/signing.json (provisioned by infra/signing).

  signtool and the dlib are fetched once from NuGet (pinned versions below) into %LOCALAPPDATA%\HashRelay\signing-tools,
  so this works on Windows PowerShell 5.1 and PowerShell 7 without an installer or admin rights. Needs the .NET 8 runtime.

  Invoked by build.ps1 -Sign for the agent/CLI binaries, and by Inno Setup (SignTool directive) for Setup.exe and
  the uninstaller.

.EXAMPLE
  ./build/sign.ps1 artifacts/cli/hashrelay.exe artifacts/agent/HashRelay.Agent.exe
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory, Position = 0, ValueFromRemainingArguments)] [string[]]$Files,
  [string]$Config = (Join-Path $PSScriptRoot "signing.json"),
  [string]$ToolsDir = (Join-Path $env:LOCALAPPDATA "HashRelay\signing-tools")
)
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

# Pinned tool versions from nuget.org. Bump together: the dlib needs signtool >= 10.0.2261.755 (the 20348 SDK is not supported).
$sdkPackage  = @{ Id = "Microsoft.Windows.SDK.BuildTools"; Version = "10.0.28000.2957"; Bin = "bin\10.0.28000.0\x64\signtool.exe" }
$dlibPackage = @{ Id = "Microsoft.ArtifactSigning.Client"; Version = "1.0.146";        Bin = "bin\x64\Azure.CodeSigning.Dlib.dll" }

function Get-NuGetTool([hashtable]$pkg) {
  $dir = Join-Path $ToolsDir "$($pkg.Id).$($pkg.Version)"
  $bin = Join-Path $dir $pkg.Bin
  if (-not (Test-Path $bin)) {
    Write-Host "  fetching $($pkg.Id) $($pkg.Version)"
    New-Item -ItemType Directory -Force $dir | Out-Null
    $id = $pkg.Id.ToLowerInvariant()
    $zip = "$dir.zip"
    Invoke-WebRequest "https://api.nuget.org/v3-flatcontainer/$id/$($pkg.Version)/$id.$($pkg.Version).nupkg" -OutFile $zip
    Expand-Archive $zip -DestinationPath $dir -Force
    Remove-Item $zip
    if (-not (Test-Path $bin)) { throw "$($pkg.Id) $($pkg.Version) does not contain $($pkg.Bin)" }
  }
  $bin
}

$cfg = Get-Content $Config -Raw | ConvertFrom-Json
foreach ($key in "Endpoint", "CodeSigningAccountName", "CertificateProfileName") {
  if (-not $cfg.$key) { throw "$Config is missing '$key'." }
}

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet -or -not (& $dotnet.Source --list-runtimes | Select-String "Microsoft.NETCore.App 8\.")) {
  throw "The Artifact Signing dlib needs the .NET 8 runtime (x64): winget install Microsoft.DotNet.Runtime.8"
}

$signtool = Get-NuGetTool $sdkPackage
$dlib = Get-NuGetTool $dlibPackage

# Only the Azure CLI credential: avoids slow managed-identity probes on build machines and makes failures obvious.
$runId = if ($env:GITHUB_RUN_ID) { "github-run-$env:GITHUB_RUN_ID" } else { $env:COMPUTERNAME }
$metadata = Join-Path $ToolsDir "metadata.json"
@{
  Endpoint               = $cfg.Endpoint
  CodeSigningAccountName = $cfg.CodeSigningAccountName
  CertificateProfileName = $cfg.CertificateProfileName
  CorrelationId          = "hashrelay $runId"
  ExcludeCredentials     = @(
    "EnvironmentCredential", "ManagedIdentityCredential", "WorkloadIdentityCredential", "SharedTokenCacheCredential",
    "VisualStudioCredential", "VisualStudioCodeCredential", "AzurePowerShellCredential", "AzureDeveloperCliCredential",
    "InteractiveBrowserCredential")
} | ConvertTo-Json | Set-Content $metadata -Encoding ascii

$paths = @($Files | ForEach-Object { (Resolve-Path $_).Path })
Write-Host "== Signing $($paths.Count) file(s) with $($cfg.CodeSigningAccountName)/$($cfg.CertificateProfileName)" -ForegroundColor Cyan
# Capture stdout+stderr ourselves: under Windows PowerShell 5.1 with ErrorActionPreference=Stop a native stderr line would
# abort before the exit-code check, hiding the dlib's /debug output that says why the service refused.
$previousEap = $ErrorActionPreference
$ErrorActionPreference = "Continue"
$output = & $signtool sign /debug /fd SHA256 /tr "http://timestamp.acs.microsoft.com" /td SHA256 /dlib $dlib /dmdf $metadata @paths 2>&1
$exit = $LASTEXITCODE
$ErrorActionPreference = $previousEap
if ($exit -ne 0) {
  $output | ForEach-Object { Write-Host "  $_" }
  throw "signtool failed (exit $exit). Is 'az login' current, and does that identity hold Artifact Signing Certificate Profile Signer on $($cfg.CodeSigningAccountName)?"
}

foreach ($p in $paths) {
  $sig = Get-AuthenticodeSignature $p
  if ($sig.Status -ne "Valid") { throw "$p : signature status $($sig.Status)" }
  Write-Host ("  {0}  <- {1}" -f (Split-Path $p -Leaf), $sig.SignerCertificate.Subject)
}

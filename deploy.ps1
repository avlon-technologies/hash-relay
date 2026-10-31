<#
.SYNOPSIS
  Provisions (or updates) the Azure environment with Terraform and deploys the published server with the Azure CLI.
  Requires: az login (as a subscription contributor), terraform, and a prior ./build.ps1 (or pass -Build).

.EXAMPLE
  ./deploy.ps1 -Build            # build everything, apply infra, deploy
  ./deploy.ps1 -SkipInfra        # redeploy code only
#>
[CmdletBinding()]
param(
  [string]$Environment = "dev",
  [switch]$Build,
  [switch]$SkipInfra,
  [switch]$AutoApprove
)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$tf = Join-Path $root "infra\terraform"

$terraform = Get-Command terraform -ErrorAction SilentlyContinue
if (-not $terraform) {
  $terraform = Get-ChildItem "$env:LOCALAPPDATA\Microsoft\WinGet\Packages" -Recurse -Filter terraform.exe -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty FullName
}
if (-not $terraform) { throw "terraform not found (winget install Hashicorp.Terraform)" }
$terraform = "$terraform"

if ($Build) { & (Join-Path $root "build.ps1") }

if (-not $SkipInfra) {
  Write-Host "`n== Terraform ($Environment)" -ForegroundColor Cyan
  & $terraform -chdir="$tf" init -input=false -backend-config="backend.$Environment.hcl" | Out-Host
  & $terraform -chdir="$tf" plan -input=false -var-file="$Environment.tfvars" -out=tfplan | Out-Host
  if ($LASTEXITCODE -ne 0) { throw "terraform plan failed" }
  if (-not $AutoApprove) {
    $answer = Read-Host "Apply this plan? (yes/no)"
    if ($answer -ne "yes") { Write-Host "Aborted."; exit 1 }
  }
  & $terraform -chdir="$tf" apply -input=false tfplan | Out-Host
  if ($LASTEXITCODE -ne 0) { throw "terraform apply failed" }
}

$rg = (& $terraform -chdir="$tf" output -raw resource_group_name)
$app = (& $terraform -chdir="$tf" output -raw web_app_name)
$url = (& $terraform -chdir="$tf" output -raw public_url)

Write-Host "`n== Deploy $app ($rg)" -ForegroundColor Cyan
$serverDir = Join-Path $root "artifacts\server"
if (-not (Test-Path (Join-Path $serverDir "HashRelay.Server.dll"))) { throw "artifacts\server is missing; run ./build.ps1 first (or pass -Build)." }
$zip = Join-Path $root "artifacts\server.zip"
Remove-Item $zip -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $serverDir "*") -DestinationPath $zip
az webapp config set --resource-group $rg --name $app --startup-file "dotnet HashRelay.Server.dll" --output none
az webapp deploy --resource-group $rg --name $app --src-path $zip --type zip --clean true --restart true --output none
if ($LASTEXITCODE -ne 0) { throw "az webapp deploy failed" }

Write-Host "`n== Health" -ForegroundColor Cyan
$deadline = (Get-Date).AddMinutes(4)
do {
  try {
    $health = Invoke-RestMethod "$url/health/live" -TimeoutSec 15
    Write-Host ("{0}  version {1}" -f $health.status, $health.version) -ForegroundColor Green
    break
  } catch { Start-Sleep -Seconds 10 }
} while ((Get-Date) -lt $deadline)
Write-Host "`nLive at $url" -ForegroundColor Green

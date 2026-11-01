<#
.SYNOPSIS
  Provisions (or updates) the scale-to-zero Azure environment with Terraform, builds the server image in Azure
  Container Registry (no local Docker needed) and rolls it out to the Container App.
  Requires: az login (subscription contributor), terraform, and a prior ./build.ps1 (or pass -Build).

.DESCRIPTION
  Order of operations on a fresh environment:
    1. terraform apply (targeted): resource group, registry, pull identity      -> so an image can be pushed
    2. az acr build: Dockerfile -> <registry>/hashrelay-server:<tag> (+ :latest)
    3. terraform apply (full): storage, environment, container app, DNS, hostname
    4. az containerapp update --image <tag>
    5. az containerapp hostname bind: free managed certificate for the custom domain (first time only)
    6. wait for /health/live

.EXAMPLE
  ./deploy.ps1 -Build            # build everything, apply infra, build image, deploy
  ./deploy.ps1 -SkipInfra        # rebuild image + roll out code only
#>
[CmdletBinding()]
param(
  [string]$Environment = "dev",
  [switch]$Build,
  [switch]$SkipInfra,
  [switch]$SkipImage,
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

function Invoke-Plan([string[]]$ExtraArgs, [string]$PlanFile) {
  & $terraform -chdir="$tf" plan -input=false -var-file="$Environment.tfvars" "-out=$PlanFile" @ExtraArgs | Out-Host
  if ($LASTEXITCODE -ne 0) { throw "terraform plan failed" }
  if (-not $AutoApprove) {
    $answer = Read-Host "Apply this plan? (yes/no)"
    if ($answer -ne "yes") { Write-Host "Aborted."; exit 1 }
  }
  & $terraform -chdir="$tf" apply -input=false $PlanFile | Out-Host
  if ($LASTEXITCODE -ne 0) { throw "terraform apply failed" }
}
function Out-TF([string]$Name) { & $terraform -chdir="$tf" output -raw $Name }

if ($Build) { & (Join-Path $root "build.ps1") }

Write-Host "`n== Terraform init ($Environment)" -ForegroundColor Cyan
& $terraform -chdir="$tf" init -input=false -backend-config="backend.$Environment.hcl" | Out-Host
if ($LASTEXITCODE -ne 0) { throw "terraform init failed" }

if (-not $SkipInfra) {
  Write-Host "`n== Terraform stage 1: registry + identity" -ForegroundColor Cyan
  Invoke-Plan @("-target=azurerm_resource_group.app", "-target=azurerm_container_registry.acr",
                "-target=azurerm_user_assigned_identity.app", "-target=azurerm_role_assignment.acr_pull") "tfplan.stage1"
}

$registry = Out-TF registry_name
$loginServer = Out-TF registry_login_server
# Not read from Terraform outputs: after the targeted stage-1 apply only the targeted outputs exist in state.
# Must match var.image_repository (default) in infra/terraform/variables.tf.
$repository = "hashrelay-server"
if (-not $registry -or -not $loginServer) { throw "Terraform outputs registry_name / registry_login_server are missing; did stage 1 apply?" }

$tag = $null
if (-not $SkipImage) {
  $version = ([xml](Get-Content (Join-Path $root "Directory.Build.props"))).Project.PropertyGroup.Version
  $sha = (git -C $root rev-parse --short HEAD 2>$null); if (-not $sha) { $sha = "local" }
  $tag = "$version-$sha-$(Get-Date -Format yyyyMMddHHmmss)"
  if (-not (Test-Path (Join-Path $root "src\HashRelay.Server\wwwroot\downloads\HashRelay-Setup.exe"))) {
    Write-Warning "wwwroot/downloads has no installer; run ./build.ps1 first so /downloads is populated in the image."
  }
  # az acr build stats every file under the context before it applies .dockerignore, and the Expo node_modules tree
  # under src/HashRelay.Mobile has paths beyond Windows MAX_PATH, so the upload dies before anything reaches ACR.
  # Build from a clean export of HEAD instead (uncommitted changes are therefore not deployed), plus the git-ignored
  # installer + CLI zip that build.ps1 drops into wwwroot/downloads.
  $ctx = Join-Path ([IO.Path]::GetTempPath()) "hashrelay-ctx-$sha"
  if (Test-Path $ctx) { Remove-Item $ctx -Recurse -Force }
  New-Item -ItemType Directory $ctx | Out-Null
  $tar = Join-Path $ctx "context.tar"
  git -C $root archive --format=tar -o $tar HEAD
  if ($LASTEXITCODE -ne 0) { throw "git archive failed" }
  tar -xf $tar -C $ctx
  Remove-Item $tar -Force
  $dl = Join-Path $root "src\HashRelay.Server\wwwroot\downloads"
  if (Test-Path $dl) { Copy-Item (Join-Path $dl "*") (Join-Path $ctx "src\HashRelay.Server\wwwroot\downloads") -Force }
  Write-Host "`n== Build image $loginServer/${repository}:$tag (az acr build)" -ForegroundColor Cyan
  # az writes progress warnings to stderr; keep them from becoming terminating errors when stderr is redirected.
  $eap = $ErrorActionPreference; $ErrorActionPreference = "Continue"
  az acr build --registry $registry --image "${repository}:$tag" --image "${repository}:latest" --build-arg "BUILD_LABEL=$tag" --file (Join-Path $ctx "Dockerfile") $ctx 2>&1 | ForEach-Object { "$_" }
  $acrExit = $LASTEXITCODE
  Remove-Item $ctx -Recurse -Force -ErrorAction SilentlyContinue
  $ErrorActionPreference = $eap
  if ($acrExit -ne 0) { throw "az acr build failed" }
}

if (-not $SkipInfra) {
  # Secret values reach Terraform only through TF_VAR_* (or a git-ignored tfvars). If one is missing, the plan
  # silently deletes the live secret (this removed the FCM key once). Refuse instead.
  $liveSecrets = @(az containerapp secret list -g "rg-hashrelay-$Environment" -n "ca-hashrelay-$Environment" --query "[].name" -o tsv 2>$null)
  $secretVars = @{ "fcm-service-account" = "fcm_service_account_json"; "smtp-password" = "smtp_password" }
  foreach ($s in $secretVars.GetEnumerator()) {
    $supplied = [Environment]::GetEnvironmentVariable("TF_VAR_$($s.Value)")
    if (-not $supplied) { $supplied = Get-ChildItem $tf -Filter *.auto.tfvars -ErrorAction SilentlyContinue | Select-String -Pattern "^\s*$($s.Value)\s*=" -Quiet }
    if (-not $supplied) { $supplied = (Test-Path (Join-Path $tf "terraform.tfvars")) -and (Select-String -Path (Join-Path $tf "terraform.tfvars") -Pattern "^\s*$($s.Value)\s*=" -Quiet) }
    if (($liveSecrets -contains $s.Key) -and -not $supplied) {
      throw "The live app has secret '$($s.Key)' but TF_VAR_$($s.Value) is not set; applying would delete it. Set it (or pass -SkipInfra)."
    }
  }
  Write-Host "`n== Terraform stage 2: everything else" -ForegroundColor Cyan
  Invoke-Plan @() "tfplan"
}

$rg = Out-TF resource_group_name
$app = Out-TF container_app_name
$cae = Out-TF container_app_environment_name
$url = Out-TF public_url
$domain = Out-TF custom_domain
$mcpDomain = Out-TF mcp_domain

if ($tag) {
  Write-Host "`n== Roll out $app -> ${repository}:$tag" -ForegroundColor Cyan
  az containerapp update --resource-group $rg --name $app --image "$loginServer/${repository}:$tag" --output none
  if ($LASTEXITCODE -ne 0) { throw "az containerapp update failed" }
}

# Free managed certificates for every custom hostname (the dashboard host and the MCP host); first time only per name.
foreach ($hostName in @($domain, $mcpDomain) | Where-Object { $_ }) {
  $bound = az containerapp hostname list --resource-group $rg --name $app --query "[?name=='$hostName' && bindingType=='SniEnabled'] | length(@)" -o tsv
  if ("$bound" -ne "1") {
    Write-Host "`n== Managed certificate for $hostName" -ForegroundColor Cyan
    az containerapp hostname bind --resource-group $rg --name $app --hostname $hostName --environment $cae --validation-method CNAME --output none
    if ($LASTEXITCODE -ne 0) { throw "az containerapp hostname bind failed for $hostName" }
  }
}

Write-Host "`n== Health" -ForegroundColor Cyan
$deadline = (Get-Date).AddMinutes(5)
do {
  try {
    $health = Invoke-RestMethod "$url/health/live" -TimeoutSec 20
    Write-Host ("{0}  version {1}" -f $health.status, $health.version) -ForegroundColor Green
    break
  } catch { Start-Sleep -Seconds 10 }
} while ((Get-Date) -lt $deadline)
Write-Host "`nLive at $url" -ForegroundColor Green

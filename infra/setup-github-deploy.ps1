<#
.SYNOPSIS
  Wires the GitHub environment "dev" to the Terraform-managed deploy identity, so a push to main deploys.

.DESCRIPTION
  Run once, after the deploy identity exists (./deploy.ps1 -SkipImage applies infra/terraform/github-deploy.tf).
  Creates or updates the GitHub environment "dev" (deployments from main only) and sets the variables the Build
  workflow's deploy job reads. Idempotent; prints no secret (none is involved: the job signs in over OIDC).
  Requires: terraform (initialised by deploy.ps1) and gh signed in with admin rights on the repository.
#>
param(
  [string]$Environment = "dev",
  [string]$Repository = "avlon-technologies/hash-relay"
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$tf = Join-Path $root "infra\terraform"

$terraform = Get-Command terraform -ErrorAction SilentlyContinue
if (-not $terraform) {
  $terraform = Get-ChildItem "$env:LOCALAPPDATA\Microsoft\WinGet\Packages" -Recurse -Filter terraform.exe -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty FullName
}
if (-not $terraform) { throw "terraform not found (winget install Hashicorp.Terraform)" }
function Out-TF([string]$Name) { (& "$terraform" -chdir="$tf" output -raw $Name).Trim() }

$clientId = Out-TF github_deploy_client_id
if (-not $clientId) { throw "Terraform output github_deploy_client_id is empty: set github_oidc_subject in $Environment.tfvars and run ./deploy.ps1 -SkipImage first." }
$vars = [ordered]@{
  AZURE_CLIENT_ID   = $clientId
  ACR_NAME          = Out-TF registry_name
  CONTAINERAPP_NAME = Out-TF container_app_name
  RESOURCE_GROUP    = Out-TF resource_group_name
  PUBLIC_URL        = Out-TF public_url
}

# The environment, restricted to deployments from main.
gh api -X PUT "repos/$Repository/environments/$Environment" `
  -F "deployment_branch_policy[protected_branches]=false" -F "deployment_branch_policy[custom_branch_policies]=true" | Out-Null
$policies = gh api "repos/$Repository/environments/$Environment/deployment-branch-policies" --jq ".branch_policies[].name"
if (-not ($policies -contains "main")) {
  gh api -X POST "repos/$Repository/environments/$Environment/deployment-branch-policies" -f name=main -f type=branch | Out-Null
}

foreach ($kv in $vars.GetEnumerator()) {
  gh variable set $kv.Key --env $Environment --body $kv.Value -R $Repository
  if ($LASTEXITCODE -ne 0) { throw "gh variable set $($kv.Key) failed" }
}
Write-Host "GitHub environment '$Environment' wired to $($vars.CONTAINERAPP_NAME) via identity $clientId." -ForegroundColor Green
Write-Host "Next push to main deploys; or run the Build workflow by hand on main." -ForegroundColor Green

# Azure Artifact Signing for HashRelay (and other Avlon Windows binaries).
#
#   Artifact Signing account (East US)          <- Microsoft-managed CA, short-lived certs, ~$10/month
#     identity validation                       <- portal only, done once by a human (see README)
#     certificate profile "hashrelay-public"    <- created here once identity_validation_id is known
#   Entra app + federated credential            <- GitHub Actions signs via OIDC, no stored secret
#   role assignments                            <- verifiers (portal) and signers (build/sign.ps1, CI)
#
# Separate Terraform root and state from infra/terraform: the dev environment can be torn down without
# touching the signing identity, and the account is organisation-wide rather than per environment.

locals {
  repo_slug = replace(var.github_repository, "/", "-")
}

resource "azurerm_resource_group" "signing" {
  name     = var.resource_group_name
  location = var.location
  tags     = var.tags
}

resource "azurerm_trusted_signing_account" "avlon" {
  name                = var.account_name
  resource_group_name = azurerm_resource_group.signing.name
  location            = azurerm_resource_group.signing.location
  sku_name            = var.sku_name
  tags                = var.tags
}

# ---- certificate profile (Public Trust) ----
# azurerm has no resource for profiles yet, so this goes through the ARM API directly.
resource "azapi_resource" "public_trust" {
  count = var.identity_validation_id == null ? 0 : 1

  type      = "Microsoft.CodeSigning/codeSigningAccounts/certificateProfiles@2025-10-13"
  name      = var.certificate_profile_name
  parent_id = azurerm_trusted_signing_account.avlon.id

  body = {
    properties = {
      profileType          = "PublicTrust"
      identityValidationId = var.identity_validation_id
      includeStreetAddress = false
      includePostalCode    = false
    }
  }

  schema_validation_enabled = false
}

# ---- GitHub Actions identity: OIDC federation, nothing to rotate ----
resource "azuread_application" "github" {
  display_name = "gh-${local.repo_slug}-signing"
  notes        = "GitHub Actions in ${var.github_repository} signs release binaries with Artifact Signing. No client secret: federated credentials only."
}

resource "azuread_service_principal" "github" {
  client_id = azuread_application.github.client_id
}

resource "azuread_application_federated_identity_credential" "github" {
  for_each = toset(var.github_signing_refs)

  application_id = azuread_application.github.id
  display_name   = "github-${replace(each.value, "/", "-")}"
  description    = "GitHub Actions, ${var.github_repository} @ ${each.value}"
  audiences      = ["api://AzureADTokenExchange"]
  issuer         = "https://token.actions.githubusercontent.com"
  subject        = "repo:${var.github_repository}:ref:${each.value}"
}

# ---- RBAC on the signing account ----
resource "azurerm_role_assignment" "verifier" {
  for_each = toset(var.verifier_object_ids)

  scope                = azurerm_trusted_signing_account.avlon.id
  role_definition_name = "Artifact Signing Identity Verifier"
  principal_id         = each.value
}

resource "azurerm_role_assignment" "signer_people" {
  for_each = toset(var.signer_object_ids)

  scope                = azurerm_trusted_signing_account.avlon.id
  role_definition_name = "Artifact Signing Certificate Profile Signer"
  principal_id         = each.value
}

resource "azurerm_role_assignment" "signer_github" {
  scope                = azurerm_trusted_signing_account.avlon.id
  role_definition_name = "Artifact Signing Certificate Profile Signer"
  principal_id         = azuread_service_principal.github.object_id
  principal_type       = "ServicePrincipal"
}

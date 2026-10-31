output "endpoint" {
  value       = azurerm_trusted_signing_account.avlon.account_uri
  description = "Regional signing endpoint; goes into build/signing.json as Endpoint."
}

output "account_name" {
  value = azurerm_trusted_signing_account.avlon.name
}

output "certificate_profile_name" {
  value       = length(azapi_resource.public_trust) > 0 ? azapi_resource.public_trust[0].name : null
  description = "null until identity_validation_id is set and applied."
}

output "github_client_id" {
  value       = azuread_application.github.client_id
  description = "GitHub Actions variable AZURE_SIGNING_CLIENT_ID."
}

output "tenant_id" {
  value = data.azurerm_client_config.current.tenant_id
}

output "subscription_id" {
  value = var.subscription_id
}

data "azurerm_client_config" "current" {}

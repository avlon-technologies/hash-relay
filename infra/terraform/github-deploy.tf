# ---- continuous deployment: the identity GitHub Actions signs in as ----
#
# The Build workflow's deploy job (.github/workflows/build.yml) runs in the GitHub environment named after
# var.environment and signs in to Azure over OIDC as this identity: no stored Azure credential. It may only
# build images in this registry and roll this resource group's container apps. Created only when
# var.github_oidc_subject is set; infra/setup-github-deploy.ps1 then writes the GitHub environment variables.

resource "azurerm_user_assigned_identity" "github_deploy" {
  count               = var.github_oidc_subject != "" ? 1 : 0
  name                = "id-github-${local.prefix}"
  resource_group_name = azurerm_resource_group.app.name
  location            = azurerm_resource_group.app.location
  tags                = local.tags
}

resource "azurerm_federated_identity_credential" "github_deploy" {
  count               = var.github_oidc_subject != "" ? 1 : 0
  name                = "github-environment-${var.environment}"
  resource_group_name = azurerm_resource_group.app.name
  parent_id           = azurerm_user_assigned_identity.github_deploy[0].id
  issuer              = "https://token.actions.githubusercontent.com"
  subject             = var.github_oidc_subject
  audience            = ["api://AzureADTokenExchange"]
}

locals {
  github_deploy_roles = var.github_oidc_subject == "" ? {} : {
    acr_build = { role = "Container Registry Tasks Contributor", scope = azurerm_container_registry.acr.id } # az acr build
    acr_pull  = { role = "AcrPull", scope = azurerm_container_registry.acr.id }                              # resolve tags
    app       = { role = "Container Apps Contributor", scope = azurerm_resource_group.app.id }               # az containerapp update
  }
}

resource "azurerm_role_assignment" "github_deploy" {
  for_each             = local.github_deploy_roles
  scope                = each.value.scope
  role_definition_name = each.value.role
  principal_id         = azurerm_user_assigned_identity.github_deploy[0].principal_id
  principal_type       = "ServicePrincipal"
}

output "github_deploy_client_id" {
  value = var.github_oidc_subject != "" ? azurerm_user_assigned_identity.github_deploy[0].client_id : ""
}

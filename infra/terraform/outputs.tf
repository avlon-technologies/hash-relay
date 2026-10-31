output "resource_group_name" {
  value = azurerm_resource_group.app.name
}
output "container_app_name" {
  value = azurerm_container_app.app.name
}
output "container_app_environment_name" {
  value = azurerm_container_app_environment.env.name
}
output "registry_name" {
  value = azurerm_container_registry.acr.name
}
output "registry_login_server" {
  value = azurerm_container_registry.acr.login_server
}
output "image_repository" {
  value = var.image_repository
}
output "default_url" {
  value = "https://${azurerm_container_app.app.ingress[0].fqdn}"
}
output "custom_domain" {
  value = local.custom_domain
}
output "public_url" {
  value = local.public_url
}

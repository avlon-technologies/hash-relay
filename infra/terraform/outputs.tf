output "resource_group_name" {
  value = azurerm_resource_group.app.name
}
output "web_app_url" {
  value = "https://${azurerm_linux_web_app.app.default_hostname}"
}
output "signalr_host" {
  value = azurerm_signalr_service.realtime.hostname
}
output "web_app_name" {
  value = azurerm_linux_web_app.app.name
}
output "public_url" {
  value = local.custom_domain != "" ? "https://${local.custom_domain}" : "https://${azurerm_linux_web_app.app.default_hostname}"
}

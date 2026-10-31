locals {
  prefix = "${var.name_prefix}-${var.environment}"
  tags = {
    application = "hash-relay"
    environment = var.environment
    managed-by  = "terraform"
  }
}

resource "azurerm_resource_group" "app" {
  name     = "rg-${local.prefix}"
  location = var.location
  tags     = local.tags
}

resource "azurerm_service_plan" "app" {
  name                = "asp-${local.prefix}"
  resource_group_name = azurerm_resource_group.app.name
  location            = azurerm_resource_group.app.location
  os_type             = "Linux"
  sku_name            = var.service_plan_sku
  tags                = local.tags
}

resource "azurerm_signalr_service" "realtime" {
  name                = "sigr-${local.prefix}"
  resource_group_name = azurerm_resource_group.app.name
  location            = azurerm_resource_group.app.location
  service_mode        = "Default"

  sku {
    name     = var.signalr_sku
    capacity = var.signalr_capacity
  }

  tags = local.tags
}

resource "azurerm_linux_web_app" "app" {
  name                = "app-${local.prefix}"
  resource_group_name = azurerm_resource_group.app.name
  location            = azurerm_resource_group.app.location
  service_plan_id     = azurerm_service_plan.app.id
  https_only          = true
  client_affinity_enabled = false

  identity {
    type = "SystemAssigned"
  }

  site_config {
    always_on = var.service_plan_sku != "F1" && var.service_plan_sku != "D1"
    application_stack {
      dotnet_version = "10.0"
    }
  }

  app_settings = {
    "ASPNETCORE_ENVIRONMENT"        = var.environment == "prod" ? "Production" : "Development"
    "Azure__SignalR__ConnectionString" = "Endpoint=https://${azurerm_signalr_service.realtime.hostname};AuthType=azure.msi;Version=1.0;"
  }

  tags = local.tags
}

resource "azurerm_role_assignment" "signalr_app_server" {
  scope                = azurerm_signalr_service.realtime.id
  role_definition_name = "SignalR App Server"
  principal_id         = azurerm_linux_web_app.app.identity[0].principal_id
}

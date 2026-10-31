locals {
  prefix = "${var.name_prefix}-${var.environment}"
  tags = {
    application = "hash-relay"
    environment = var.environment
    managed-by  = "terraform"
  }
  custom_domain = var.dns_zone_name != "" ? "${var.dns_record_name}.${var.dns_zone_name}" : ""
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
  name                    = "app-${local.prefix}"
  resource_group_name     = azurerm_resource_group.app.name
  location                = azurerm_resource_group.app.location
  service_plan_id         = azurerm_service_plan.app.id
  https_only              = true
  client_affinity_enabled = false

  identity {
    type = "SystemAssigned"
  }

  site_config {
    always_on                         = var.service_plan_sku != "F1" && var.service_plan_sku != "D1"
    websockets_enabled                = true
    http2_enabled                     = true
    health_check_path                 = "/health/live"
    health_check_eviction_time_in_min = 5
    application_stack {
      dotnet_version = "10.0"
    }
  }

  app_settings = {
    "ASPNETCORE_ENVIRONMENT"           = var.environment == "prod" ? "Production" : "Production"
    "Azure__SignalR__ConnectionString" = "Endpoint=https://${azurerm_signalr_service.realtime.hostname};AuthType=azure.msi;Version=1.0;"
    "HashRelay__PublicUrl"             = local.custom_domain != "" ? "https://${local.custom_domain}" : ""
    "HashRelay__DatabasePath"          = "/home/data/hashrelay.db"
    "HashRelay__AllowSignup"           = tostring(var.allow_signup)
  }

  logs {
    application_logs {
      file_system_level = "Information"
    }
    http_logs {
      file_system {
        retention_in_days = 3
        retention_in_mb   = 35
      }
    }
  }

  tags = local.tags
}

resource "azurerm_role_assignment" "signalr_app_server" {
  scope                = azurerm_signalr_service.realtime.id
  role_definition_name = "SignalR App Server"
  principal_id         = azurerm_linux_web_app.app.identity[0].principal_id
}

# ---- optional custom domain in an existing Azure DNS zone, with a free App Service managed certificate ----

data "azurerm_dns_zone" "zone" {
  count               = var.dns_zone_name != "" ? 1 : 0
  name                = var.dns_zone_name
  resource_group_name = var.dns_zone_resource_group
}

resource "azurerm_dns_cname_record" "app" {
  count               = var.dns_zone_name != "" ? 1 : 0
  name                = var.dns_record_name
  zone_name           = data.azurerm_dns_zone.zone[0].name
  resource_group_name = data.azurerm_dns_zone.zone[0].resource_group_name
  ttl                 = 300
  record              = azurerm_linux_web_app.app.default_hostname
  tags                = local.tags
}

resource "azurerm_dns_txt_record" "asuid" {
  count               = var.dns_zone_name != "" ? 1 : 0
  name                = "asuid.${var.dns_record_name}"
  zone_name           = data.azurerm_dns_zone.zone[0].name
  resource_group_name = data.azurerm_dns_zone.zone[0].resource_group_name
  ttl                 = 300
  record {
    value = azurerm_linux_web_app.app.custom_domain_verification_id
  }
  tags = local.tags
}

resource "azurerm_app_service_custom_hostname_binding" "app" {
  count               = var.dns_zone_name != "" ? 1 : 0
  hostname            = local.custom_domain
  app_service_name    = azurerm_linux_web_app.app.name
  resource_group_name = azurerm_resource_group.app.name
  depends_on          = [azurerm_dns_cname_record.app, azurerm_dns_txt_record.asuid]

  lifecycle {
    ignore_changes = [ssl_state, thumbprint]
  }
}

resource "azurerm_app_service_managed_certificate" "app" {
  count                      = var.dns_zone_name != "" ? 1 : 0
  custom_hostname_binding_id = azurerm_app_service_custom_hostname_binding.app[0].id
  tags                       = local.tags
}

resource "azurerm_app_service_certificate_binding" "app" {
  count               = var.dns_zone_name != "" ? 1 : 0
  hostname_binding_id = azurerm_app_service_custom_hostname_binding.app[0].id
  certificate_id      = azurerm_app_service_managed_certificate.app[0].id
  ssl_state           = "SniEnabled"
}

# HashRelay on Azure, scale-to-zero edition.
#
#   Container Apps (consumption, 0..1 replicas)  <- the server image, pulled from a Basic container registry
#   Azure Files share                            <- SQLite database + data-protection keys (/data)
#   Log Analytics (pay-per-GB, 1 GB/day cap)     <- container logs
#   Azure DNS records + Container Apps managed certificate for the custom domain
#
# Nothing here bills while idle except the registry (Basic, a few dollars a month) and a few MB of storage.
# When no browser or agent is connected the app scales to zero; the first request wakes it in a few seconds.

locals {
  prefix  = "${var.name_prefix}-${var.environment}"
  compact = "${var.name_prefix}${var.environment}"
  tags = {
    application = "hash-relay"
    environment = var.environment
    managed-by  = "terraform"
  }
  custom_domain = var.dns_zone_name != "" ? "${var.dns_record_name}.${var.dns_zone_name}" : ""
  image         = "${azurerm_container_registry.acr.login_server}/${var.image_repository}:${var.image_tag}"
  public_url    = local.custom_domain != "" ? "https://${local.custom_domain}" : "https://${azurerm_container_app.app.ingress[0].fqdn}"
}

resource "azurerm_resource_group" "app" {
  name     = "rg-${local.prefix}"
  location = var.location
  tags     = local.tags
}

# ---- image registry + pull identity (created first so the image can be pushed before the app exists) ----

resource "azurerm_container_registry" "acr" {
  name                = "cr${local.compact}"
  resource_group_name = azurerm_resource_group.app.name
  location            = azurerm_resource_group.app.location
  sku                 = "Basic"
  admin_enabled       = false
  tags                = local.tags
}

resource "azurerm_user_assigned_identity" "app" {
  name                = "id-${local.prefix}"
  resource_group_name = azurerm_resource_group.app.name
  location            = azurerm_resource_group.app.location
  tags                = local.tags
}

resource "azurerm_role_assignment" "acr_pull" {
  scope                = azurerm_container_registry.acr.id
  role_definition_name = "AcrPull"
  principal_id         = azurerm_user_assigned_identity.app.principal_id
}

# ---- persistent data: Azure Files share mounted at /data ----

resource "azurerm_storage_account" "data" {
  name                       = "st${local.compact}"
  resource_group_name        = azurerm_resource_group.app.name
  location                   = azurerm_resource_group.app.location
  account_tier               = "Standard"
  account_replication_type   = "LRS"
  min_tls_version            = "TLS1_2"
  https_traffic_only_enabled = true
  tags                       = local.tags
}

resource "azurerm_storage_share" "data" {
  name               = "hashrelay-data"
  storage_account_id = azurerm_storage_account.data.id
  quota              = 5
}

# ---- logs + container apps environment ----

resource "azurerm_log_analytics_workspace" "logs" {
  name                = "log-${local.prefix}"
  resource_group_name = azurerm_resource_group.app.name
  location            = azurerm_resource_group.app.location
  sku                 = "PerGB2018"
  retention_in_days   = 30
  daily_quota_gb      = 1
  tags                = local.tags
}

resource "azurerm_container_app_environment" "env" {
  name                       = "cae-${local.prefix}"
  resource_group_name        = azurerm_resource_group.app.name
  location                   = azurerm_resource_group.app.location
  log_analytics_workspace_id = azurerm_log_analytics_workspace.logs.id
  tags                       = local.tags
}

resource "azurerm_container_app_environment_storage" "data" {
  name                         = "data"
  container_app_environment_id = azurerm_container_app_environment.env.id
  account_name                 = azurerm_storage_account.data.name
  share_name                   = azurerm_storage_share.data.name
  access_key                   = azurerm_storage_account.data.primary_access_key
  access_mode                  = "ReadWrite"
}

# ---- the app ----

resource "azurerm_container_app" "app" {
  name                         = "ca-${local.prefix}"
  container_app_environment_id = azurerm_container_app_environment.env.id
  resource_group_name          = azurerm_resource_group.app.name
  revision_mode                = "Single"
  tags                         = local.tags

  identity {
    type         = "UserAssigned"
    identity_ids = [azurerm_user_assigned_identity.app.id]
  }

  registry {
    server   = azurerm_container_registry.acr.login_server
    identity = azurerm_user_assigned_identity.app.id
  }

  ingress {
    external_enabled = true
    target_port      = 8080
    transport        = "auto" # HTTP/1.1, HTTP/2 and WebSockets (SignalR)
    traffic_weight {
      latest_revision = true
      percentage      = 100
    }
  }

  template {
    min_replicas = 0 # scale to zero when idle
    max_replicas = 1 # SQLite = single writer; in-process SignalR = single node

    container {
      name   = "server"
      image  = local.image
      cpu    = var.container_cpu
      memory = var.container_memory

      env {
        name  = "ASPNETCORE_ENVIRONMENT"
        value = "Production"
      }
      env {
        name  = "HashRelay__PublicUrl"
        value = local.custom_domain != "" ? "https://${local.custom_domain}" : ""
      }
      env {
        name  = "HashRelay__DatabasePath"
        value = "/data/hashrelay.db"
      }
      env {
        name  = "HashRelay__UseWal"
        value = "false" # WAL is not safe on an SMB share
      }
      env {
        name  = "HashRelay__AllowSignup"
        value = tostring(var.allow_signup)
      }

      liveness_probe {
        transport        = "HTTP"
        port             = 8080
        path             = "/health/live"
        initial_delay    = 10
        interval_seconds = 30
      }

      volume_mounts {
        name = "data"
        path = "/data"
      }
    }

    volume {
      name          = "data"
      storage_name  = azurerm_container_app_environment_storage.data.name
      storage_type  = "AzureFile"
      mount_options = "nobrl" # SQLite locking does not work with SMB byte-range locks; single replica makes this safe
    }
  }

  lifecycle {
    # deploy.ps1 rolls new image tags with `az containerapp update`; Terraform owns everything else.
    ignore_changes = [template[0].container[0].image]
  }

  depends_on = [azurerm_role_assignment.acr_pull]
}

# ---- optional custom domain in an existing Azure DNS zone ----
# Terraform publishes the DNS records and attaches the hostname; the free managed certificate is created and
# bound by deploy.ps1 (`az containerapp hostname bind`), which is why the certificate fields are ignored below.

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
  record              = azurerm_container_app.app.ingress[0].fqdn
  tags                = local.tags
}

resource "azurerm_dns_txt_record" "asuid" {
  count               = var.dns_zone_name != "" ? 1 : 0
  name                = "asuid.${var.dns_record_name}"
  zone_name           = data.azurerm_dns_zone.zone[0].name
  resource_group_name = data.azurerm_dns_zone.zone[0].resource_group_name
  ttl                 = 300
  record {
    value = azurerm_container_app_environment.env.custom_domain_verification_id
  }
  tags = local.tags
}

resource "azurerm_container_app_custom_domain" "app" {
  count            = var.dns_zone_name != "" ? 1 : 0
  name             = local.custom_domain
  container_app_id = azurerm_container_app.app.id

  lifecycle {
    ignore_changes = [certificate_binding_type, container_app_environment_certificate_id]
  }

  depends_on = [azurerm_dns_cname_record.app, azurerm_dns_txt_record.asuid]
}

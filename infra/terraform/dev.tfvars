# Account-specific values (subscription_id, dns_zone_resource_group, github_oidc_subject) live in the
# git-ignored local.auto.tfvars next to this file; Terraform loads it automatically.
name_prefix             = "hashrelay"
environment             = "dev"
location                = "canadacentral"
container_cpu           = 0.25
container_memory        = "0.5Gi"
allow_signup            = true
dns_zone_name           = "dev.avlon.ca"
dns_record_name         = "hashrelay"
mcp_record_name         = "mcp.hashrelay"

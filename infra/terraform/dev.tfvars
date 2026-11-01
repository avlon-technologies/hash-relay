subscription_id         = "3a2ca6a1-e036-4794-920e-6eebe1b77eae"
name_prefix             = "hashrelay"
environment             = "dev"
location                = "canadacentral"
container_cpu           = 0.25
container_memory        = "0.5Gi"
allow_signup            = true
dns_zone_name           = "dev.avlon.ca"
dns_zone_resource_group = "rg-platform-dns-dev-cc"
dns_record_name         = "hashrelay"
mcp_record_name         = "mcp.hashrelay"
# Continuous deployment from the Build workflow's deploy job (GitHub environment "dev", immutable OIDC subject).
github_oidc_subject = "repo:avlon-technologies@300070876/hash-relay@1440557510:environment:dev"

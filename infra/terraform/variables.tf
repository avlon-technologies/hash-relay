variable "subscription_id" {
  type        = string
  description = "Azure subscription ID."
}
variable "name_prefix" {
  type        = string
  description = "Globally unique lowercase alphanumeric prefix for public resource names (registry, storage account)."
  validation {
    condition     = can(regex("^[a-z][a-z0-9]{3,14}$", var.name_prefix))
    error_message = "Use 4-15 lowercase letters/digits, starting with a letter."
  }
}
variable "environment" {
  type    = string
  default = "dev"
  validation {
    condition     = contains(["dev", "stg", "prod"], var.environment)
    error_message = "Environment must be dev, stg, or prod."
  }
}
variable "location" {
  type    = string
  default = "canadacentral"
}
variable "image_repository" {
  type        = string
  default     = "hashrelay-server"
  description = "Repository name inside the container registry."
}
variable "image_tag" {
  type        = string
  default     = "latest"
  description = "Tag used when the container app is first created; later tags are rolled by deploy.ps1."
}
variable "container_cpu" {
  type        = number
  default     = 0.25
  description = "vCPU per replica (consumption plan allows 0.25 .. 4 in 0.25 steps, paired with memory)."
}
variable "container_memory" {
  type        = string
  default     = "0.5Gi"
  description = "Memory per replica (2 GiB per vCPU, e.g. 0.25 -> 0.5Gi, 0.5 -> 1Gi)."
}
variable "allow_signup" {
  type        = bool
  default     = true
  description = "Whether the web dashboard accepts new account sign-ups."
}
variable "dns_zone_name" {
  type        = string
  default     = ""
  description = "Existing Azure DNS zone to publish the app under (empty = no custom domain)."
}
variable "dns_zone_resource_group" {
  type        = string
  default     = ""
  description = "Resource group of the DNS zone."
}
variable "dns_record_name" {
  type        = string
  default     = "hashrelay"
  description = "Record name inside the zone, e.g. 'hashrelay' for hashrelay.<zone>."
}
variable "mcp_record_name" {
  type        = string
  default     = "mcp.hashrelay"
  description = "Record name inside the zone for the MCP host, e.g. 'mcp.hashrelay' for mcp.hashrelay.<zone>. Empty = no separate MCP host."
}
variable "fcm_service_account_json" {
  type        = string
  default     = ""
  sensitive   = true
  description = "Firebase service-account key (JSON contents) used to send mobile push through FCM HTTP v1. Empty disables push; devices then receive on their next foreground connection. Pass with -var-file or TF_VAR_fcm_service_account_json; never commit it."
}
variable "email_from" {
  type        = string
  default     = ""
  description = "Sender for transactional email such as password-reset links, e.g. \"HashRelay <no-reply@example.com>\". Empty disables email: reset links are then only written to the server log."
}
variable "smtp_host" {
  type        = string
  default     = ""
  description = "SMTP submission host (STARTTLS on smtp_port). Empty disables outbound email."
}
variable "smtp_port" {
  type    = number
  default = 587
}
variable "smtp_username" {
  type    = string
  default = ""
}
variable "smtp_password" {
  type        = string
  default     = ""
  sensitive   = true
  description = "SMTP password or API key. Pass with TF_VAR_smtp_password or a git-ignored tfvars file; never commit it."
}

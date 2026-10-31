variable "subscription_id" {
  type        = string
  description = "Azure subscription ID."
}
variable "name_prefix" {
  type        = string
  description = "Globally unique lowercase alphanumeric prefix for public resource names."
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
variable "service_plan_sku" {
  type    = string
  default = "B1"
}
variable "signalr_sku" {
  type    = string
  default = "Free_F1"
}
variable "signalr_capacity" {
  type    = number
  default = 1
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

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

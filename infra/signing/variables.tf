variable "subscription_id" {
  type        = string
  description = "Azure subscription that owns the signing account (identity validation is scoped to it)."
}

variable "location" {
  type        = string
  default     = "eastus"
  description = "Artifact Signing is not offered in Canada; East US is the nearest region. The endpoint must match: https://eus.codesigning.azure.net."
}

variable "resource_group_name" {
  type    = string
  default = "rg-avlon-signing-eus"
}

variable "account_name" {
  type        = string
  default     = "avlonsigning"
  description = "Globally unique, 3-24 alphanumerics, starts with a letter. One account serves every Avlon product."
}

variable "sku_name" {
  type        = string
  default     = "Basic"
  description = "Basic: 5,000 signatures/month included. Premium only adds more profiles and quota."
}

variable "certificate_profile_name" {
  type    = string
  default = "hashrelay-public"
}

variable "identity_validation_id" {
  type        = string
  default     = null
  description = "GUID of the completed Public Trust organization identity validation (Azure portal > account > Identity validations). Leave null until validation is Completed; the certificate profile is created only when set."
}

variable "verifier_object_ids" {
  type        = list(string)
  description = "Entra object ids allowed to submit identity validation requests in the portal (Artifact Signing Identity Verifier)."
}

variable "signer_object_ids" {
  type        = list(string)
  default     = []
  description = "Entra object ids of people who sign from their own machine (Artifact Signing Certificate Profile Signer). The CI identity is added automatically."
}

variable "github_repository" {
  type    = string
  default = "avlon-technologies/hash-relay"
}

variable "github_signing_refs" {
  type        = list(string)
  default     = ["refs/heads/main"]
  description = "Git refs whose GitHub Actions runs may sign (one federated credential each; GitHub subjects do not support wildcards)."
}

variable "tags" {
  type    = map(string)
  default = { application = "hash-relay", "managed-by" = "terraform", purpose = "code-signing" }
}

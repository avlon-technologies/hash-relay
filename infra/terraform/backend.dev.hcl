# resource_group_name and storage_account_name for the state account live in the git-ignored backend.dev.local.hcl.
container_name       = "tfstate-dev"
key                  = "hash-relay.tfstate"
use_azuread_auth     = true

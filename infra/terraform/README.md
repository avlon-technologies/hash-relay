# HashRelay infrastructure — Terraform

Provision a minimal **Azure App Service (.NET 10, Linux)** and **Azure SignalR Service** for the Windows-first development integration. This is infrastructure only; no application code or Windows installer is deployed by Terraform.

## Resources

- Resource group, Linux App Service plan and Web App
- Azure SignalR Service (Default mode)
- System-assigned App Service managed identity and least-privilege **SignalR App Server** RBAC role assignment
- Nonsecret Azure SignalR endpoint configuration via `Azure__SignalR__ConnectionString` using `AuthType=azure.msi`

## Prerequisites and usage

Install Terraform, Azure CLI, and sign into an Azure subscription with permissions to create resources **and role assignments**.

```bash
az login
cp terraform.tfvars.example terraform.tfvars
# Set subscription_id, unique name_prefix and chosen SKUs
terraform init
terraform fmt -check
terraform validate
terraform plan -out=tfplan
terraform apply tfplan
terraform output web_app_url
```

**Costs:** The B1 App Service plan incurs charges. SignalR Free_F1 is intended for initial development only and subject to availability and limits. Confirm pricing before apply. No resources are provisioned until apply is run.

**App integration:** Configure ASP.NET Core SignalR with `AddSignalR().AddAzureSignalR()`; the Web App setting `Azure__SignalR__ConnectionString` is read automatically. Managed-identity RBAC permissions may take time to propagate. Deploy published web artifacts separately.

**Security:** This template does not implement user signup, tenant data persistence, publisher API-key verification, endpoint enrollment, secure dashboard authentication, or production network restrictions. Do not expose a public anonymous publish endpoint. Terraform state must be secured remotely before shared-team use. No SignalR access keys are stored in Terraform state.

**Scope:** Windows installer and CLI are distributed separately and connect over HTTPS to the deployed Web App. Terraform provisions the cloud transport, not a Windows host.

Official references:
- https://registry.terraform.io/providers/hashicorp/azurerm/latest/docs/resources/linux_web_app
- https://registry.terraform.io/providers/hashicorp/azurerm/latest/docs/resources/signalr_service
- https://learn.microsoft.com/en-us/azure/azure-signalr/signalr-howto-authorize-managed-identity

## Mobile push (optional)

Set `fcm_service_account_json` to the contents of a Firebase service-account key to let the server send push notifications to paired phones through Firebase Cloud Messaging (both Android and iOS). It becomes the Container App secret `fcm-service-account`, exposed to the server as `HashRelay__Push__Fcm__ServiceAccountJson`. Pass it as `TF_VAR_fcm_service_account_json` or via a git-ignored tfvars file; leave it empty to run without push (devices receive on their next foreground connection). See `src/HashRelay.Mobile/README.md` for the Firebase project setup.

## Outbound email (password reset links)

Set `allow_password_reset = true` to turn on the self-service forgot-password pages (they are off by default and answer 404), and set `email_from` and `smtp_host` (plus `smtp_username` / `smtp_password` when the provider needs them; port defaults to 587 with STARTTLS) so the server can email self-service password-reset links. They become `HashRelay__Email__*` on the container, with the password held as the Container App secret `smtp-password`. Pass the password as `TF_VAR_smtp_password` or via a git-ignored tfvars file. With `smtp_host` empty the forgot-password page still works, but the link is only written to the server log (`az containerapp logs show`), which is fine for dev and useless for real users.

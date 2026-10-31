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

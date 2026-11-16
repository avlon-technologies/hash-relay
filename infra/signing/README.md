# Code signing — Azure Artifact Signing

Signs the Windows agent, CLI, installer and uninstaller so downloads are not "Unknown publisher" and SmartScreen
reputation can accrue. Azure Artifact Signing is a managed CA: no certificate file, no HSM token, no renewal.
Each signature uses a certificate valid for three days, so everything is RFC 3161 timestamped.

This Terraform root is deliberately separate from `infra/terraform` (state key `hash-relay-signing.tfstate`):
the account is organisation-wide and must outlive any one environment.

## What it creates

| Resource | Purpose |
| --- | --- |
| `rg-avlon-signing-eus` + Artifact Signing account `avlonsigning` (East US, Basic) | The service is not offered in Canada; East US is the nearest region. Endpoint `https://eus.codesigning.azure.net`. |
| Certificate profile `hashrelay-public` (Public Trust) | Created only once `identity_validation_id` is set. |
| Entra app `gh-avlon-technologies-hash-relay-signing` + federated credential | GitHub Actions on `main` signs via OIDC. No client secret exists. |
| Role assignments on the account | Identity Verifier for people who run validation; Certificate Profile Signer for people and the CI identity. |

Cost: Basic SKU is about USD 10/month including 5,000 signatures.

## One-time setup

1. **Apply the account** (the certificate profile is skipped while `identity_validation_id` is unset).
   First create two git-ignored files in `infra/signing`: `local.auto.tfvars` with `subscription_id`,
   `verifier_object_ids` and `signer_object_ids`, and `backend.local.hcl` with the state account's
   `resource_group_name` and `storage_account_name`.

   ```powershell
   terraform -chdir=infra/signing init -backend-config=backend.hcl -backend-config=backend.local.hcl
   terraform -chdir=infra/signing plan -var-file=signing.tfvars -out=tfplan
   terraform -chdir=infra/signing apply tfplan
   ```

2. **Identity validation (portal only, done by a human).** Azure portal → Artifact Signing Accounts → `avlonsigning` →
   Identity validations → Organization → New identity → **Public**. Enter Avlon Technologies' legal name, website,
   a monitored primary e-mail on the avlon.ca domain, business identifier and registered address, and the name of the
   person who will complete the individual ID check exactly as on their government ID. Expect an e-mail verification
   link (expires in 7 days), then an "Action Required" step where that person verifies with Microsoft Entra Verified ID
   on a phone. Processing takes 1 to 20 business days. Status goes to **Completed**.

3. **Create the certificate profile.** Copy the *Identity validation Id* from the portal into `signing.tfvars`
   (`identity_validation_id = "..."`) and run plan/apply again.

4. **GitHub Actions variables** (not secrets; nothing here is sensitive):

   ```powershell
   gh variable set AZURE_SIGNING_CLIENT_ID --body (terraform -chdir=infra/signing output -raw github_client_id)
   gh variable set AZURE_TENANT_ID         --body (terraform -chdir=infra/signing output -raw tenant_id)
   gh variable set AZURE_SUBSCRIPTION_ID   --body (terraform -chdir=infra/signing output -raw subscription_id)
   ```

   The workflow signs on pushes to `main` once `AZURE_SIGNING_CLIENT_ID` exists; pull requests build unsigned.

## Signing locally

`./build.ps1 -Sign` signs `hashrelay.exe`, `HashRelay.Agent.exe` and the HashRelay DLLs, then Inno Setup signs
`HashRelay-Setup.exe` and its uninstaller through `build/sign.ps1`. Requirements on the machine: `az login` as someone
listed in `signer_object_ids`, and the .NET 8 runtime. `build/sign.ps1` downloads pinned copies of signtool and the
Artifact Signing dlib from nuget.org into `%LOCALAPPDATA%\HashRelay\signing-tools` on first use.

`build/signing.json` holds the endpoint, account and profile names. Change it if you rename anything here.

## Adding a signer or another repository

- Another person: add their Entra object id (`az ad user show --id them@avlon.ca --query id -o tsv`) to `signer_object_ids`.
- Another repository: this root is HashRelay-specific in its GitHub app; copy the `azuread_*` block with a new
  `github_repository`, or move the account into a platform repo and reference it with a data source.

## Notes

- Signing does not give instant SmartScreen trust. Reputation accrues per publisher as downloads are seen.
- `Microsoft.CodeSigning` must be registered on the subscription (`az provider register --namespace Microsoft.CodeSigning`).
- Deleting the account deletes its certificate profiles; identity validations are subscription-scoped and survive.

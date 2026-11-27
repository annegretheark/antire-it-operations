# Dev: dev-tenanten og abonnementet Aitrening (UTVIDELSESPLAN.md 8b, ca. 375 kr i måneden)
environment     = "dev"
tenant_id       = "3af5c92b-38f9-48ce-8746-82bb4c113032"
subscription_id = "d3f46a10-2d76-465d-837c-a283331837d5"

api_client_id           = "e33710da-36b0-4e4b-9696-bb4fbdff647f"
spa_client_id           = "73962775-c92f-4113-872b-234035c282cf"
spa_extra_redirect_uris = ["http://localhost:5173/auth-popup.html"]

app_service_sku     = "B1"
postgres_sku        = "B_Standard_B1ms"
postgres_storage_mb = 32768

# Abonnementet har ikke App Service-kvote (B1) i Norway East; databasen og resten blir der
app_location   = "swedencentral"
monthly_budget = 50

# Offentlig IP-adresse migreringer kjøres fra settes ved kjøring:
#   terraform apply -var-file=dev.tfvars -var='deployer_ip_addresses=["<din IP>"]'

# AI og ukentlig driftsrapport i dev (FASE5_DRIFTSRAPPORT.md, ENDRING_OG_RISIKO.md C8)
ai_enabled         = true
ops_report_enabled = true

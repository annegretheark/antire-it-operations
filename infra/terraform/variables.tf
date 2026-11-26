variable "environment" {
  description = "Miljø: dev, test eller prod"
  type        = string
}

variable "tenant_id" {
  description = "Entra-tenanten miljøet hører til"
  type        = string
}

variable "subscription_id" {
  description = "Abonnementet ressursene opprettes i"
  type        = string
}

variable "location" {
  description = "Azure-region"
  type        = string
  default     = "norwayeast"
}

variable "api_client_id" {
  description = "Client ID til API-app-registreringen (appens identitet mot Graph og Azure)"
  type        = string
}

variable "spa_client_id" {
  description = "Client ID til SPA-app-registreringen (innlogging i nettleseren)"
  type        = string
}

variable "spa_extra_redirect_uris" {
  description = "Andre redirect-URI-er SPA-appen skal beholde (f.eks. localhost for utvikling)"
  type        = list(string)
  default     = []
}

variable "app_service_sku" {
  description = "App Service-plan, f.eks. B1 (dev) eller P0v3 (prod)"
  type        = string
  default     = "B1"
}

variable "postgres_sku" {
  description = "PostgreSQL-størrelse, f.eks. B_Standard_B1ms (dev) eller B_Standard_B2s (prod)"
  type        = string
  default     = "B_Standard_B1ms"
}

variable "postgres_storage_mb" {
  description = "Lagring for PostgreSQL i MB"
  type        = number
  default     = 32768
}

variable "postgres_version" {
  description = "PostgreSQL-versjon"
  type        = string
  default     = "17"
}

variable "deployer_ip_addresses" {
  description = "Offentlige IP-adresser som får koble til databasen (for migreringer herfra)"
  type        = list(string)
  default     = []
}

variable "monthly_budget" {
  description = "Budsjett per måned for ressursgruppen, i abonnementets fakturavaluta; varsel til eiere ved 80 % (faktisk) og 100 % (prognose)"
  type        = number
  default     = 50
}

variable "tags" {
  description = "Tagger på alle ressurser"
  type        = map(string)
  default     = {}
}

variable "app_location" {
  description = "Region for App Service, hvis den må ligge et annet sted enn resten (f.eks. kvote). Tom = location."
  type        = string
  default     = null
}

# ---------- AI (Foundry) ----------

variable "ai_location" {
  description = "Region for Foundry-ressursen (EU)"
  type        = string
  default     = "swedencentral"
}

variable "ai_model" {
  type    = string
  default = "gpt-4.1-mini"
}

variable "ai_model_version" {
  type    = string
  default = "2025-04-14"
}

variable "ai_deployment_name" {
  type    = string
  default = "gpt-4.1-mini"
}

variable "ai_sku" {
  description = "DataZoneStandard holder behandlingen i EU; GlobalStandard bare hvis modellen ikke finnes som Data Zone"
  type        = string
  default     = "DataZoneStandard"
}

variable "ai_capacity" {
  description = "Kapasitet i tusen tokens per minutt"
  type        = number
  default     = 20
}

variable "ai_enabled" {
  description = "Slår på AI i appen (Ai__Enabled)"
  type        = bool
  default     = false
}

variable "ops_report_enabled" {
  description = "Slår på den ukentlige driftsrapporten (OpsReport__Enabled), mandag kl. 7 norsk tid"
  type        = bool
  default     = false
}

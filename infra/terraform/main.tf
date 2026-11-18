# Antire IT Operations i Azure (UTVIDELSESPLAN.md, kapittel 8b).
# Én App Service (API, frontend og bakgrunnsjobber), PostgreSQL med Entra-innlogging,
# og en brukertildelt managed identity som logger inn som API-app-registreringen
# (federated credential): ingen passord eller hemmeligheter i Azure.

data "azurerm_client_config" "current" {}

data "azuread_user" "deployer" {
  object_id = data.azurerm_client_config.current.object_id
}

data "azuread_application" "api" {
  client_id = var.api_client_id
}

data "azuread_application" "spa" {
  client_id = var.spa_client_id
}

# Globale navn (lagring, nøkkelhvelv, database, app) får en kort fast endelse
resource "random_string" "suffix" {
  length  = 6
  upper   = false
  special = false
}

locals {
  name     = "itops-${var.environment}-noe"
  suffix   = random_string.suffix.result
  app_name = "app-itops-${var.environment}-${local.suffix}"
  app_url  = "https://${local.app_name}.azurewebsites.net"
  # PostgreSQL kutter rollenavn ved 63 tegn (gjestebrukere har lange navn)
  deployer_db_user = substr(data.azuread_user.deployer.user_principal_name, 0, 63)

  # App Service kan ligge i en annen region enn resten (se app_location)
  app_location = coalesce(var.app_location, var.location)
  database     = "antire_it_operations"

  tags = merge({
    prosjekt = "antire-it-operations"
    miljo    = var.environment
  }, var.tags)
}

resource "azurerm_resource_group" "main" {
  name     = "rg-${local.name}"
  location = var.location
  tags     = local.tags
}

# ---------- Overvåking ----------

resource "azurerm_log_analytics_workspace" "main" {
  name                = "log-${local.name}"
  location            = azurerm_resource_group.main.location
  resource_group_name = azurerm_resource_group.main.name
  sku                 = "PerGB2018"
  retention_in_days   = 30
  tags                = local.tags
}

resource "azurerm_application_insights" "main" {
  name                = "appi-${local.name}"
  location            = azurerm_resource_group.main.location
  resource_group_name = azurerm_resource_group.main.name
  workspace_id        = azurerm_log_analytics_workspace.main.id
  application_type    = "web"
  tags                = local.tags
}

# ---------- Identitet ----------

resource "azurerm_user_assigned_identity" "app" {
  name                = "id-${local.name}"
  location            = azurerm_resource_group.main.location
  resource_group_name = azurerm_resource_group.main.name
  tags                = local.tags
}

# Identiteten logger inn som API-app-registreringen, så appen beholder rettighetene
# den har i dag (Graph og Reader i Azure), også i andre tenanter senere
resource "azuread_application_federated_identity_credential" "app" {
  application_id = data.azuread_application.api.id
  display_name   = "app-service-${var.environment}"
  description    = "Managed identity ${azurerm_user_assigned_identity.app.name} i ${azurerm_resource_group.main.name}"
  audiences      = ["api://AzureADTokenExchange"]
  issuer         = "https://login.microsoftonline.com/${var.tenant_id}/v2.0"
  subject        = azurerm_user_assigned_identity.app.principal_id
}

# ---------- Krypteringsnøkler (Data Protection) ----------

resource "azurerm_storage_account" "dataprotection" {
  name                            = "stitops${var.environment}${local.suffix}"
  location                        = azurerm_resource_group.main.location
  resource_group_name             = azurerm_resource_group.main.name
  account_tier                    = "Standard"
  account_replication_type        = "LRS"
  min_tls_version                 = "TLS1_2"
  shared_access_key_enabled       = false
  allow_nested_items_to_be_public = false
  tags                            = local.tags
}

resource "azurerm_storage_container" "dataprotection" {
  name                  = "dataprotection"
  storage_account_id    = azurerm_storage_account.dataprotection.id
  container_access_type = "private"
}

resource "azurerm_role_assignment" "app_blob" {
  scope                = azurerm_storage_account.dataprotection.id
  role_definition_name = "Storage Blob Data Contributor"
  principal_id         = azurerm_user_assigned_identity.app.principal_id
  principal_type       = "ServicePrincipal"
}

resource "azurerm_key_vault" "main" {
  name                       = "kv-itops-${var.environment}-${local.suffix}"
  location                   = azurerm_resource_group.main.location
  resource_group_name        = azurerm_resource_group.main.name
  tenant_id                  = var.tenant_id
  sku_name                   = "standard"
  rbac_authorization_enabled = true
  soft_delete_retention_days = 7
  purge_protection_enabled   = var.environment == "prod"
  tags                       = local.tags
}

# Den som kjører Terraform lager nøkkelen
resource "azurerm_role_assignment" "deployer_keys" {
  scope                = azurerm_key_vault.main.id
  role_definition_name = "Key Vault Crypto Officer"
  principal_id         = data.azurerm_client_config.current.object_id
}

resource "azurerm_role_assignment" "app_keys" {
  scope                = azurerm_key_vault.main.id
  role_definition_name = "Key Vault Crypto User"
  principal_id         = azurerm_user_assigned_identity.app.principal_id
  principal_type       = "ServicePrincipal"
}

# Rolletildelinger bruker litt tid på å slå gjennom
resource "time_sleep" "deployer_keys" {
  depends_on      = [azurerm_role_assignment.deployer_keys]
  create_duration = "60s"
}

resource "azurerm_key_vault_key" "dataprotection" {
  name         = "dataprotection"
  key_vault_id = azurerm_key_vault.main.id
  key_type     = "RSA"
  key_size     = 2048
  key_opts     = ["wrapKey", "unwrapKey"]
  depends_on   = [time_sleep.deployer_keys]
}

# ---------- Database ----------

resource "azurerm_postgresql_flexible_server" "main" {
  name                          = "psql-itops-${var.environment}-${local.suffix}"
  location                      = azurerm_resource_group.main.location
  resource_group_name           = azurerm_resource_group.main.name
  version                       = var.postgres_version
  sku_name                      = var.postgres_sku
  storage_mb                    = var.postgres_storage_mb
  backup_retention_days         = 7
  public_network_access_enabled = true

  # Bare Entra-innlogging; ingen databasepassord
  authentication {
    active_directory_auth_enabled = true
    password_auth_enabled         = false
    tenant_id                     = var.tenant_id
  }

  tags = local.tags

  lifecycle {
    ignore_changes = [zone]
  }
}

resource "azurerm_postgresql_flexible_server_database" "app" {
  name      = local.database
  server_id = azurerm_postgresql_flexible_server.main.id
  charset   = "UTF8"
  collation = "en_US.utf8"
}

# Administratorer: den som kjører migreringer, og appens identitet.
# Dev: appen er administrator. Prod: egen databaserolle med bare de rettighetene appen trenger.
resource "azurerm_postgresql_flexible_server_active_directory_administrator" "deployer" {
  server_name         = azurerm_postgresql_flexible_server.main.name
  resource_group_name = azurerm_resource_group.main.name
  tenant_id           = var.tenant_id
  object_id           = data.azurerm_client_config.current.object_id
  principal_name      = local.deployer_db_user
  principal_type      = "User"
}

resource "azurerm_postgresql_flexible_server_active_directory_administrator" "app" {
  server_name         = azurerm_postgresql_flexible_server.main.name
  resource_group_name = azurerm_resource_group.main.name
  tenant_id           = var.tenant_id
  object_id           = azurerm_user_assigned_identity.app.principal_id
  principal_name      = azurerm_user_assigned_identity.app.name
  principal_type      = "ServicePrincipal"

  depends_on = [azurerm_postgresql_flexible_server_active_directory_administrator.deployer]
}

# Azure-tjenester (App Service) og adressene migreringer kjøres fra
resource "azurerm_postgresql_flexible_server_firewall_rule" "azure" {
  name             = "AllowAzureServices"
  server_id        = azurerm_postgresql_flexible_server.main.id
  start_ip_address = "0.0.0.0"
  end_ip_address   = "0.0.0.0"
}

resource "azurerm_postgresql_flexible_server_firewall_rule" "deployer" {
  for_each         = toset(var.deployer_ip_addresses)
  name             = "Deployer-${replace(each.value, ".", "-")}"
  server_id        = azurerm_postgresql_flexible_server.main.id
  start_ip_address = each.value
  end_ip_address   = each.value
}

# ---------- App ----------

resource "azurerm_service_plan" "main" {
  name                = "asp-${local.name}"
  location            = local.app_location
  resource_group_name = azurerm_resource_group.main.name
  os_type             = "Linux"
  sku_name            = var.app_service_sku
  tags                = local.tags
}

resource "azurerm_linux_web_app" "main" {
  name                = local.app_name
  location            = local.app_location
  resource_group_name = azurerm_resource_group.main.name
  service_plan_id     = azurerm_service_plan.main.id
  https_only          = true

  identity {
    type         = "UserAssigned"
    identity_ids = [azurerm_user_assigned_identity.app.id]
  }

  site_config {
    # Bakgrunnsjobbene (SLA, varsler, GDPR) må kjøre hele tiden
    always_on                         = true
    health_check_path                 = "/health"
    health_check_eviction_time_in_min = 10
    ftps_state                        = "Disabled"
    minimum_tls_version               = "1.2"

    application_stack {
      dotnet_version = "10.0"
    }
  }

  # Innstillinger for Azure (backend/Services/AzureHosting.cs og AppCredentials.cs)
  app_settings = {
    ASPNETCORE_ENVIRONMENT                     = "Production"
    APPLICATIONINSIGHTS_CONNECTION_STRING      = azurerm_application_insights.main.connection_string
    ApplicationInsightsAgent_EXTENSION_VERSION = "~3"

    ConnectionStrings__DefaultConnection = "Host=${azurerm_postgresql_flexible_server.main.fqdn};Database=${local.database};Username=${azurerm_user_assigned_identity.app.name};SSL Mode=Require"
    Database__UseEntraAuth               = "true"

    Azure__ManagedIdentityClientId              = azurerm_user_assigned_identity.app.client_id
    MicrosoftGraph__ClientId                    = var.api_client_id
    MicrosoftGraph__UseFederatedManagedIdentity = "true"

    DataProtection__BlobUri        = "${azurerm_storage_account.dataprotection.primary_blob_endpoint}${azurerm_storage_container.dataprotection.name}/keys.xml"
    DataProtection__KeyVaultKeyUri = azurerm_key_vault_key.dataprotection.versionless_id

    App__FrontendUrl = local.app_url
  }

  logs {
    http_logs {
      file_system {
        retention_in_days = 7
        retention_in_mb   = 35
      }
    }
  }

  tags = local.tags

  depends_on = [
    azurerm_role_assignment.app_blob,
    azurerm_role_assignment.app_keys,
    azurerm_postgresql_flexible_server_active_directory_administrator.app,
  ]
}

# Innloggingen i nettleseren må få komme tilbake til appens adresse.
# Ressursen eier hele listen over SPA-adresser, så localhost står i spa_extra_redirect_uris.
resource "azuread_application_redirect_uris" "spa" {
  application_id = data.azuread_application.spa.id
  type           = "SPA"
  redirect_uris  = concat(var.spa_extra_redirect_uris, ["${local.app_url}/auth-popup.html"])
}

# ---------- Budsjett ----------

resource "azurerm_consumption_budget_resource_group" "main" {
  name              = "budget-${local.name}"
  resource_group_id = azurerm_resource_group.main.id
  amount            = var.monthly_budget
  time_grain        = "Monthly"

  time_period {
    start_date = "2026-11-01T00:00:00Z"
  }

  notification {
    enabled        = true
    threshold      = 80
    operator       = "GreaterThan"
    threshold_type = "Actual"
    contact_roles  = ["Owner"]
  }

  notification {
    enabled        = true
    threshold      = 100
    operator       = "GreaterThan"
    threshold_type = "Forecasted"
    contact_roles  = ["Owner"]
  }

  lifecycle {
    ignore_changes = [time_period]
  }
}

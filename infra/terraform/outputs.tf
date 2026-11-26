output "app_url" {
  description = "Adressen til appen"
  value       = local.app_url
}

output "app_name" {
  value = azurerm_linux_web_app.main.name
}

output "resource_group" {
  value = azurerm_resource_group.main.name
}

output "postgres_fqdn" {
  value = azurerm_postgresql_flexible_server.main.fqdn
}

output "postgres_database" {
  value = local.database
}

output "app_identity_name" {
  description = "Databasebrukeren appen logger inn som"
  value       = azurerm_user_assigned_identity.app.name
}

output "deployer_db_user" {
  description = "Databasebrukeren migreringer kjøres som (UPN, kuttet ved 63 tegn)"
  value       = local.deployer_db_user
}

output "ai_endpoint" {
  description = "Endepunkt for Foundry-ressursen (Ai:Endpoint lokalt)"
  value       = azurerm_cognitive_account.ai.endpoint
}

output "ai_deployment" {
  value = azurerm_cognitive_deployment.ai.name
}

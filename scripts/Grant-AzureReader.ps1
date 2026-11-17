<#
.SYNOPSIS
  Gir appens tjenestekonto rollen Reader på Azure-abonnementer (fase 3).

.DESCRIPTION
  Appen ser bare abonnementer der tjenestekontoen (service principal) til
  app-registreringen har Reader. Rollen gir lesetilgang til ressursene, ikke til
  innhold som nøkler, hemmeligheter eller data i lagringskontoer.

  Skriptet kan kjøres flere ganger: finnes tildelingen fra før, hoppes den over.
  Krever Azure CLI innlogget i riktig tenant, med rollen Owner eller
  User Access Administrator på abonnementene:
    az login --tenant <tenant-id>

  Etterpå: Azure → Abonnementer → Synk i appen.

.EXAMPLE
  ./scripts/Grant-AzureReader.ps1 -TenantId 3af5c92b-38f9-48ce-8746-82bb4c113032 `
                                  -ApiClientId e33710da-36b0-4e4b-9696-bb4fbdff647f `
                                  -SubscriptionId <abonnements-id>
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)] [string] $TenantId,
    [Parameter(Mandatory)] [string] $ApiClientId,
    [Parameter(Mandatory)] [string[]] $SubscriptionId
)

$ErrorActionPreference = 'Stop'

# Stopp hvis Azure CLI står på en annen tenant enn den som er oppgitt
$currentTenant = az account show --query tenantId -o tsv
if ($currentTenant -ne $TenantId) {
    throw "Azure CLI er logget inn i tenant '$currentTenant', ikke '$TenantId'. Kjør: az login --tenant $TenantId"
}

$sp = az ad sp show --id $ApiClientId --query '{id:id, name:displayName}' -o json | ConvertFrom-Json
if (-not $sp) {
    throw "Fant ingen tjenestekonto for app-registreringen $ApiClientId i tenanten."
}
Write-Host "Tjenestekonto: $($sp.name) (objekt-ID $($sp.id))"

foreach ($subscription in $SubscriptionId) {
    $scope = "/subscriptions/$subscription"

    $existing = az role assignment list --assignee $sp.id --role Reader --scope $scope --query '[].id' -o tsv
    if ($existing) {
        Write-Host "Reader finnes allerede på $scope"
        continue
    }

    if ($PSCmdlet.ShouldProcess($scope, "Gi $($sp.name) rollen Reader")) {
        az role assignment create `
            --assignee-object-id $sp.id `
            --assignee-principal-type ServicePrincipal `
            --role Reader `
            --scope $scope `
            --output none
        Write-Host "Reader gitt på $scope"
    }
}

Write-Host "Ferdig. Det kan ta noen minutter før tilgangen virker. Synk deretter abonnementene i appen."

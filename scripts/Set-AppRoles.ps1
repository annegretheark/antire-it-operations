<#
.SYNOPSIS
  Sørger for at API-app-registreringen har alle app-rollene løsningen bruker.

.DESCRIPTION
  Legger til roller som mangler (identifisert på verdi, f.eks. "Helpdesk.Agent").
  Eksisterende roller og ID-ene deres endres ikke, så skriptet kan kjøres flere
  ganger og i hver tenant løsningen brukes i (f.eks. test og prod).

  Krever Azure CLI innlogget i riktig tenant:
    az login --tenant <tenant-id> --allow-no-subscriptions

.EXAMPLE
  ./scripts/Set-AppRoles.ps1 -TenantId 3af5c92b-38f9-48ce-8746-82bb4c113032 `
                             -ApiClientId e33710da-36b0-4e4b-9696-bb4fbdff647f
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)] [string] $TenantId,
    [Parameter(Mandatory)] [string] $ApiClientId
)

$ErrorActionPreference = 'Stop'

# Rollene løsningen bruker (se backend/Services/AuthPolicies.cs)
$wantedRoles = @(
    @{ value = 'Helpdesk.Admin';     displayName = 'Helpdesk Admin';      description = 'Can manage all tickets and helpdesk configuration (categories, catalog, SLA)' }
    @{ value = 'Helpdesk.Agent';     displayName = 'Helpdesk Agent';      description = 'Can handle tickets and complete access requests, but not change configuration' }
    @{ value = 'Approver';           displayName = 'Approver';            description = 'Can approve or reject access requests' }
    @{ value = 'EmployeeSync.Admin'; displayName = 'Employee Sync Admin'; description = 'Can synchronize employees from Entra ID' }
)

# Stopp hvis Azure CLI står på en annen tenant enn den som er oppgitt
$currentTenant = az account show --query tenantId -o tsv
if ($currentTenant -ne $TenantId) {
    throw "Azure CLI er logget inn i tenant '$currentTenant', ikke '$TenantId'. Kjør: az login --tenant $TenantId --allow-no-subscriptions"
}

$app = az ad app show --id $ApiClientId --query '{id:id, name:displayName, appRoles:appRoles}' -o json | ConvertFrom-Json
Write-Host "App-registrering: $($app.name) (objekt-ID $($app.id))"

$roles = [System.Collections.Generic.List[object]]::new()
foreach ($role in $app.appRoles) { $roles.Add($role) }

$added = @()
foreach ($wanted in $wantedRoles) {
    if ($roles | Where-Object { $_.value -eq $wanted.value }) {
        Write-Host "  Finnes:     $($wanted.value)"
        continue
    }

    $roles.Add([pscustomobject]@{
        id                 = [guid]::NewGuid().ToString()
        value              = $wanted.value
        displayName        = $wanted.displayName
        description        = $wanted.description
        allowedMemberTypes = @('User')
        isEnabled          = $true
    })
    $added += $wanted.value
    Write-Host "  Legges til: $($wanted.value)"
}

if ($added.Count -eq 0) {
    Write-Host 'Alle roller finnes allerede. Ingen endringer.'
    return
}

if ($PSCmdlet.ShouldProcess($app.name, "Legg til app-roller: $($added -join ', ')")) {
    $body = @{ appRoles = $roles } | ConvertTo-Json -Depth 5 -Compress
    $bodyFile = New-TemporaryFile
    try {
        Set-Content -Path $bodyFile -Value $body -Encoding utf8
        az rest --method patch `
            --url "https://graph.microsoft.com/v1.0/applications/$($app.id)" `
            --headers 'Content-Type=application/json' `
            --body "@$bodyFile" | Out-Null
    }
    finally {
        Remove-Item $bodyFile -ErrorAction SilentlyContinue
    }

    Write-Host "Ferdig. Tildel rollene til brukere under Enterprise applications → $($app.name) → Users and groups."
}

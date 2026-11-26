<#
.SYNOPSIS
  Gir API-app-registreringen Graph-rettighetene løsningen bruker, med admin-samtykke.

.DESCRIPTION
  1. Legger rettighetene til under API permissions i app-registreringen
     (applikasjonsrettigheter, ikke delegerte).
  2. Gir admin-samtykke: én app-rolletildeling per rettighet på appens
     service principal (Enterprise application).

  Rettigheter som finnes, eller som allerede har samtykke, hoppes over, så
  skriptet kan kjøres flere ganger og i hver tenant løsningen brukes i
  (f.eks. test og prod). Ingen rettigheter fjernes.

  Krever Azure CLI innlogget i riktig tenant som en som kan gi admin-samtykke
  (Global Administrator eller Privileged Role Administrator):
    az login --tenant <tenant-id> --allow-no-subscriptions

  Med -IncludeUserAdmin legges også de DELEGERTE rettighetene til for å
  deaktivere, aktivere og slette brukere fra Microsoft 365-siden, med
  admin-samtykke for alle brukere (oauth2PermissionGrant). Disse brukes på vegne
  av den innloggede personen (on-behalf-of), så Entra sjekker i tillegg
  personens egen rolle (f.eks. User Administrator). Appen får ingen ny
  skrivetilgang på egen hånd.

  Backend lagrer Graph-tokenet sitt i opptil en time. Start appen på nytt for at
  nye rettigheter skal gjelde med en gang.

.EXAMPLE
  ./scripts/Grant-GraphPermissions.ps1 -TenantId 3af5c92b-38f9-48ce-8746-82bb4c113032 `
                                       -ApiClientId e33710da-36b0-4e4b-9696-bb4fbdff647f

.EXAMPLE
  # Bare de påkrevde rettighetene, uten de valgfrie
  ./scripts/Grant-GraphPermissions.ps1 -TenantId <tenant-id> -ApiClientId <client-id> -SkipOptional

.EXAMPLE
  # Også delegerte rettigheter for å deaktivere og slette brukere (med personens egne rettigheter)
  ./scripts/Grant-GraphPermissions.ps1 -TenantId <tenant-id> -ApiClientId <client-id> -IncludeUserAdmin
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)] [string] $TenantId,
    [Parameter(Mandatory)] [string] $ApiClientId,
    [switch] $SkipOptional,
    [switch] $IncludeUserAdmin
)

$ErrorActionPreference = 'Stop'

# Graph-rettighetene løsningen bruker (se backend/Services/GraphService.cs og README)
$wantedPermissions = @(
    @{ value = 'User.Read.All';             optional = $false }  # Synk av ansatte, brukeroversikten
    @{ value = 'GroupMember.Read.All';      optional = $false }  # Grupper, medlemmer, automatisk tildeling
    @{ value = 'GroupMember.ReadWrite.All'; optional = $false }  # Tilgang via gruppe (legge til og fjerne)
    @{ value = 'Organization.Read.All';     optional = $true }   # Lisensoversikten
    @{ value = 'AuditLog.Read.All';         optional = $true }   # Siste pålogging og MFA (krever Entra ID P1)
    @{ value = 'SecurityAlert.Read.All';    optional = $true }   # Defender-varsler (krever Defender-lisens)
)
if ($SkipOptional) { $wantedPermissions = $wantedPermissions | Where-Object { -not $_.optional } }

# Delegerte rettigheter (on-behalf-of), bare med -IncludeUserAdmin
$wantedDelegated = @(
    'User.EnableDisableAccount.All'  # Deaktivere og aktivere brukere (Microsoft 365 → Brukere)
    'User.DeleteRestore.All'         # Slette brukere (Microsoft 365 → Brukere)
)

$graphAppId = '00000003-0000-0000-c000-000000000000'

# Stopp hvis Azure CLI står på en annen tenant enn den som er oppgitt
$currentTenant = az account show --query tenantId -o tsv
if ($currentTenant -ne $TenantId) {
    throw "Azure CLI er logget inn i tenant '$currentTenant', ikke '$TenantId'. Kjør: az login --tenant $TenantId --allow-no-subscriptions"
}

# Microsoft Graph i denne tenanten, og ID-ene til app-rollene (rettighetene)
$graph = az ad sp show --id $graphAppId `
    --query '{id:id, appRoles:appRoles[].{id:id, value:value}, scopes:oauth2PermissionScopes[].{id:id, value:value}}' `
    -o json | ConvertFrom-Json

$app = az ad app show --id $ApiClientId --query '{id:id, name:displayName, rra:requiredResourceAccess}' -o json | ConvertFrom-Json
Write-Host "App-registrering: $($app.name) ($ApiClientId)"

$appSpId = az ad sp list --filter "appId eq '$ApiClientId'" --query '[0].id' -o tsv
if (-not $appSpId) {
    throw "Fant ingen service principal (Enterprise application) for $ApiClientId. Kjør: az ad sp create --id $ApiClientId"
}

$configured = @(($app.rra | Where-Object resourceAppId -eq $graphAppId).resourceAccess |
    Where-Object type -eq 'Role' | ForEach-Object { $_.id })
$assignments = (az rest --method GET `
    --url "https://graph.microsoft.com/v1.0/servicePrincipals/$appSpId/appRoleAssignments" -o json | ConvertFrom-Json).value
$consented = @($assignments | Where-Object resourceId -eq $graph.id | ForEach-Object { $_.appRoleId })

$toConfigure = @()
$toConsent = @()
foreach ($wanted in $wantedPermissions) {
    $role = $graph.appRoles | Where-Object value -eq $wanted.value
    if (-not $role) { throw "Fant ikke rettigheten $($wanted.value) i Microsoft Graph." }

    $needsConfig = $configured -notcontains $role.id
    $needsConsent = $consented -notcontains $role.id
    if ($needsConfig) { $toConfigure += $role }
    if ($needsConsent) { $toConsent += $role }

    $status = if (-not $needsConfig -and -not $needsConsent) { 'Finnes:     ' } else { 'Legges til: ' }
    Write-Host "  $status$($wanted.value)$(if ($wanted.optional) { ' (valgfri)' })"
}

if ($toConfigure.Count -eq 0 -and $toConsent.Count -eq 0) {
    Write-Host 'Alle applikasjonsrettigheter finnes og har admin-samtykke.'
}

# 1) API permissions i app-registreringen
if ($toConfigure.Count -gt 0 -and
    $PSCmdlet.ShouldProcess($app.name, "Legg til Graph-rettigheter: $($toConfigure.value -join ', ')")) {
    az ad app permission add --id $ApiClientId --api $graphAppId `
        --api-permissions ($toConfigure | ForEach-Object { "$($_.id)=Role" }) --only-show-errors
    if ($LASTEXITCODE -ne 0) { throw 'Kunne ikke legge rettighetene til i app-registreringen.' }
}

# 2) Admin-samtykke
foreach ($role in $toConsent) {
    if (-not $PSCmdlet.ShouldProcess($app.name, "Gi admin-samtykke: $($role.value)")) { continue }

    $body = @{ principalId = $appSpId; resourceId = $graph.id; appRoleId = $role.id } | ConvertTo-Json -Compress
    $bodyFile = New-TemporaryFile
    try {
        Set-Content -Path $bodyFile -Value $body -Encoding utf8
        az rest --method post `
            --url "https://graph.microsoft.com/v1.0/servicePrincipals/$appSpId/appRoleAssignments" `
            --headers 'Content-Type=application/json' `
            --body "@$bodyFile" -o none
        if ($LASTEXITCODE -ne 0) { throw "Admin-samtykke feilet for $($role.value)." }
    }
    finally {
        Remove-Item $bodyFile -ErrorAction SilentlyContinue
    }
}

# 3) Delegerte rettigheter med admin-samtykke for alle brukere (bare -IncludeUserAdmin)
if ($IncludeUserAdmin) {
    $configuredScopes = @(($app.rra | Where-Object resourceAppId -eq $graphAppId).resourceAccess |
        Where-Object type -eq 'Scope' | ForEach-Object { $_.id })

    $grant = (az rest --method GET `
        --url "https://graph.microsoft.com/v1.0/oauth2PermissionGrants?`$filter=clientId eq '$appSpId' and resourceId eq '$($graph.id)' and consentType eq 'AllPrincipals'" `
        -o json | ConvertFrom-Json).value | Select-Object -First 1
    $grantedScopes = @(if ($grant) { $grant.scope -split ' ' | Where-Object { $_ } })

    $scopesToConfigure = @()
    $scopesToConsent = @()
    foreach ($value in $wantedDelegated) {
        $scope = $graph.scopes | Where-Object value -eq $value
        if (-not $scope) { throw "Fant ikke den delegerte rettigheten $value i Microsoft Graph." }

        $needsConfig = $configuredScopes -notcontains $scope.id
        $needsConsent = $grantedScopes -notcontains $value
        if ($needsConfig) { $scopesToConfigure += $scope }
        if ($needsConsent) { $scopesToConsent += $value }

        $status = if (-not $needsConfig -and -not $needsConsent) { 'Finnes:     ' } else { 'Legges til: ' }
        Write-Host "  $status$value (delegert)"
    }

    if ($scopesToConfigure.Count -gt 0 -and
        $PSCmdlet.ShouldProcess($app.name, "Legg til delegerte Graph-rettigheter: $($scopesToConfigure.value -join ', ')")) {
        az ad app permission add --id $ApiClientId --api $graphAppId `
            --api-permissions ($scopesToConfigure | ForEach-Object { "$($_.id)=Scope" }) --only-show-errors
        if ($LASTEXITCODE -ne 0) { throw 'Kunne ikke legge de delegerte rettighetene til i app-registreringen.' }
    }

    if ($scopesToConsent.Count -gt 0 -and
        $PSCmdlet.ShouldProcess($app.name, "Gi admin-samtykke (alle brukere): $($scopesToConsent -join ', ')")) {
        $allScopes = (@($grantedScopes) + $scopesToConsent | Select-Object -Unique) -join ' '
        $bodyFile = New-TemporaryFile
        try {
            if ($grant) {
                Set-Content -Path $bodyFile -Value (@{ scope = $allScopes } | ConvertTo-Json -Compress) -Encoding utf8
                az rest --method patch `
                    --url "https://graph.microsoft.com/v1.0/oauth2PermissionGrants/$($grant.id)" `
                    --headers 'Content-Type=application/json' --body "@$bodyFile" -o none
            }
            else {
                $body = @{
                    clientId    = $appSpId
                    consentType = 'AllPrincipals'
                    resourceId  = $graph.id
                    scope       = $allScopes
                } | ConvertTo-Json -Compress
                Set-Content -Path $bodyFile -Value $body -Encoding utf8
                az rest --method post `
                    --url 'https://graph.microsoft.com/v1.0/oauth2PermissionGrants' `
                    --headers 'Content-Type=application/json' --body "@$bodyFile" -o none
            }
            if ($LASTEXITCODE -ne 0) { throw 'Admin-samtykke feilet for de delegerte rettighetene.' }
        }
        finally {
            Remove-Item $bodyFile -ErrorAction SilentlyContinue
        }
    }
}

Write-Host 'Ferdig. Start appen på nytt for at nye rettigheter skal gjelde med en gang (az webapp restart).'

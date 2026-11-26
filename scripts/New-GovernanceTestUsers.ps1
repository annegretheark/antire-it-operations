<#
.SYNOPSIS
  Oppretter syntetiske testbrukere for Change Management i UTVIKLINGSTENANTEN.

.DESCRIPTION
  Bare for prototypen (ENDRING_OG_RISIKO.md, C2), slik at separasjon av plikter kan
  prøves med ekte innlogging. Stopper hvis Azure CLI ikke er logget inn i
  utviklingstenanten 3af5c92b. Antire-tenanten røres aldri.

  For hver bruker (test.requester, test.changemanager, test.systemowner,
  test.implementer, test.reviewer):
    1. Oppretter brukeren på tenantens onmicrosoft.com-domene, uten lisenser, med
       tilfeldig passord og krav om nytt passord ved første pålogging.
    2. Gir app-rollen Helpdesk.Agent på API-appen.
  Brukere som finnes, hoppes over (passordet endres ikke).

  Passord for nye brukere skrives til en fil i brukerprofilen din (ikke i repoet).
  Endringsrollene tildeles i appen etter synk av ansatte fra Entra.

.EXAMPLE
  ./scripts/New-GovernanceTestUsers.ps1 -ApiClientId e33710da-36b0-4e4b-9696-bb4fbdff647f -WhatIf
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)] [string] $ApiClientId,
    [string] $PasswordFile = (Join-Path $HOME 'itops-governance-test-users.txt')
)

$ErrorActionPreference = 'Stop'

# Eneste tenant skriptet kan brukes i
$devTenant = '3af5c92b-38f9-48ce-8746-82bb4c113032'

$currentTenant = az account show --query tenantId -o tsv
if ($currentTenant -ne $devTenant) {
    throw "Azure CLI er logget inn i tenant '$currentTenant'. Skriptet kjører bare i utviklingstenanten $devTenant."
}

$users = @(
    @{ alias = 'test.requester';     name = 'Test Change Requester' }
    @{ alias = 'test.changemanager'; name = 'Test Change Manager' }
    @{ alias = 'test.systemowner';   name = 'Test System Owner' }
    @{ alias = 'test.implementer';   name = 'Test Implementer' }
    @{ alias = 'test.reviewer';      name = 'Test Reviewer' }
)

# Tenantens første onmicrosoft.com-domene
$domain = (az rest --method GET --url 'https://graph.microsoft.com/v1.0/organization?$select=verifiedDomains' -o json |
    ConvertFrom-Json).value[0].verifiedDomains |
    Where-Object { $_.isInitial } | Select-Object -First 1 -ExpandProperty name
if (-not $domain) { throw 'Fant ikke tenantens onmicrosoft.com-domene.' }

# API-appen og app-rollen Helpdesk.Agent
$sp = az ad sp list --filter "appId eq '$ApiClientId'" --query '[0].{id:id, roles:appRoles[].{id:id, value:value}}' -o json |
    ConvertFrom-Json
if (-not $sp) { throw "Fant ingen service principal for $ApiClientId i tenanten." }
$agentRole = $sp.roles | Where-Object value -eq 'Helpdesk.Agent'
if (-not $agentRole) { throw 'Fant ikke app-rollen Helpdesk.Agent på API-appen.' }

function New-RandomPassword {
    $chars = 'abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789!#%+?'
    $bytes = [System.Security.Cryptography.RandomNumberGenerator]::GetBytes(24)
    -join ($bytes | ForEach-Object { $chars[$_ % $chars.Length] })
}

$created = @()

foreach ($user in $users) {
    $upn = "$($user.alias)@$domain"
    $existing = az ad user list --filter "userPrincipalName eq '$upn'" --query '[0].id' -o tsv

    if ($existing) {
        Write-Host "  Finnes:      $upn"
        $userId = $existing
    }
    elseif ($PSCmdlet.ShouldProcess($upn, 'Opprett syntetisk testbruker')) {
        $password = New-RandomPassword
        $userId = az ad user create --display-name $user.name --user-principal-name $upn `
            --password $password --force-change-password-next-sign-in true --query id -o tsv
        if ($LASTEXITCODE -ne 0 -or -not $userId) { throw "Kunne ikke opprette $upn." }

        $created += "$upn`t$password"
        Write-Host "  Opprettet:   $upn"
    }
    else {
        continue
    }

    # App-rollen Helpdesk.Agent
    $assigned = (az rest --method GET `
        --url "https://graph.microsoft.com/v1.0/users/$userId/appRoleAssignments" -o json | ConvertFrom-Json).value |
        Where-Object { $_.resourceId -eq $sp.id -and $_.appRoleId -eq $agentRole.id }

    if (-not $assigned -and $PSCmdlet.ShouldProcess($upn, 'Gi app-rollen Helpdesk.Agent')) {
        $bodyFile = New-TemporaryFile
        try {
            @{ principalId = $userId; resourceId = $sp.id; appRoleId = $agentRole.id } |
                ConvertTo-Json -Compress | Set-Content -Path $bodyFile -Encoding utf8
            az rest --method post --url "https://graph.microsoft.com/v1.0/users/$userId/appRoleAssignments" `
                --headers 'Content-Type=application/json' --body "@$bodyFile" -o none
            if ($LASTEXITCODE -ne 0) { throw "Kunne ikke gi Helpdesk.Agent til $upn." }
        }
        finally {
            Remove-Item $bodyFile -ErrorAction SilentlyContinue
        }
    }
}

if ($created.Count -gt 0) {
    Add-Content -Path $PasswordFile -Value ("# Opprettet $(Get-Date -Format s) i $devTenant (passordet må byttes ved første pålogging)")
    Add-Content -Path $PasswordFile -Value $created
    Write-Host "Passord for nye brukere er skrevet til $PasswordFile"
}

Write-Host 'Ferdig. Synk ansatte fra Entra i appen, og tildel endringsrollene under Innstillinger (C4) eller via /api/governance/roles.'

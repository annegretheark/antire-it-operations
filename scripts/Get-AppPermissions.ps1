<#
.SYNOPSIS
  Revisjon av API-rettigheter på alle app-registreringer og enterprise apps i en tenant.

.DESCRIPTION
  Leser (endrer ingenting) og lager én rad per rettighet:
    - App-registreringer: rettigheter som er konfigurert (API permissions).
    - Enterprise apps (service principals): rettigheter som faktisk er gitt,
      både applikasjonsrettigheter (app-rolletildelinger) og delegerte
      (OAuth2-samtykker, admin eller per bruker).

  Kolonnen Status viser avvik mellom konfigurert og gitt:
    Gitt                 konfigurert og har samtykke
    Mangler samtykke     konfigurert, men ikke gitt (admin-samtykke mangler)
    Gitt, ikke konfig.   gitt uten å stå i app-registreringen (f.eks. tredjepartsapp
                         eller dynamisk samtykke)

  Kolonnen Risiko er en grov vurdering ut fra rettighetens navn og type:
    Høy      skrivetilgang (ReadWrite, FullControl, Send, RoleManagement, ...) som
             applikasjon, eller delegert med admin-samtykke for alle brukere
    Middels  lesetilgang til hele tenanten (*.Read.All) som applikasjon, eller
             delegert skrivetilgang
    Lav      resten (f.eks. User.Read, openid, profile)

  Siste pålogging per app (samme verdi på alle radene til appen), fra rapporten
  servicePrincipalSignInActivities i Graph (beta):
    SistePålogging   siste aktivitet av noe slag, også når andre apper kaller den
    SisteMedBruker   siste gang appen logget inn på vegne av en bruker (delegert)
    SisteSomApp      siste gang appen logget inn som seg selv (applikasjonsrettigheter)
    DagerSiden       dager siden SistePålogging
  «Ingen registrert» betyr at appen ikke har logget inn så lenge Entra har målt det.
  Rapporten krever Entra ID P1 og en rolle som kan lese påloggingsdata (f.eks.
  Global Reader eller Reports Reader). Uten det står kolonnene som «Ukjent».

  Microsofts egne apper er utelatt som standard, unntatt de som har fått delegert
  samtykke i tenanten (f.eks. Microsoft Graph Command Line Tools). Ta med alle med
  -IncludeMicrosoftApps.

  Krever Azure CLI innlogget i riktig tenant som en bruker som kan lese
  katalogen (f.eks. Global Reader):
    az login --tenant <tenant-id> --allow-no-subscriptions

  Resultatet skrives til en CSV-fil (semikolon og UTF-8, åpnes rett i Excel).
  Uten -CsvPath lagres den i gjeldende mappe som
  app-rettigheter-<tenant-id>-<dato og tid>.csv.

.EXAMPLE
  # Skriver app-rettigheter-3af5c92b-...-20261120-1015.csv i gjeldende mappe
  ./scripts/Get-AppPermissions.ps1 -TenantId 3af5c92b-38f9-48ce-8746-82bb4c113032

.EXAMPLE
  ./scripts/Get-AppPermissions.ps1 -TenantId <tenant-id> -CsvPath C:\Rapporter\app-rettigheter.csv

.EXAMPLE
  # Også radene i terminalen: bare det som bør ses på
  ./scripts/Get-AppPermissions.ps1 -TenantId <tenant-id> -PassThru |
      Where-Object { $_.Risiko -eq 'Høy' -or $_.Status -ne 'Gitt' } | Format-Table
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $TenantId,
    [string] $CsvPath,
    [switch] $IncludeMicrosoftApps,
    # Sender radene videre i pipelinen i tillegg til CSV-filen
    [switch] $PassThru
)

$ErrorActionPreference = 'Stop'

# Microsofts egne tenanter; apper eid av dem er Microsoft-tjenester
$microsoftTenants = @(
    'f8cdef31-a31e-4b4a-93e4-5f571e91255a'
    '72f988bf-86f1-41af-91ab-2d7cd011db47'
    'cdc5aeea-15c5-4db6-b079-fcadd2505dc2'
)

# Standard innloggingsrettigheter (OpenID Connect); trenger ikke stå i registreringen
$signInScopes = @('openid', 'profile', 'email', 'offline_access')

# Stopp hvis Azure CLI står på en annen tenant enn den som er oppgitt
$currentTenant = az account show --query tenantId -o tsv
if ($currentTenant -ne $TenantId) {
    throw "Azure CLI er logget inn i tenant '$currentTenant', ikke '$TenantId'. Kjør: az login --tenant $TenantId --allow-no-subscriptions"
}

# Ett Graph-token fra Azure CLI; kallene gjøres direkte (raskere enn az rest per kall)
$token = az account get-access-token --resource-type ms-graph --query accessToken -o tsv
if (-not $token) { throw 'Fikk ikke Graph-token fra Azure CLI.' }
$headers = @{ Authorization = "Bearer $token" }

# GET med sideveksling (@odata.nextLink)
function Get-GraphAll([string] $url) {
    $items = [System.Collections.Generic.List[object]]::new()
    while ($url) {
        $response = Invoke-RestMethod -Uri $url -Headers $headers -Method Get
        foreach ($item in $response.value) { $items.Add($item) }
        $url = $response.'@odata.nextLink'
    }
    $items
}

Write-Host 'Henter app-registreringer, enterprise apps og samtykker...'
$graph = 'https://graph.microsoft.com/v1.0'
$servicePrincipals = Get-GraphAll "$graph/servicePrincipals?`$top=999&`$select=id,appId,displayName,appOwnerOrganizationId,servicePrincipalType,accountEnabled,appRoles,oauth2PermissionScopes"
$applications = Get-GraphAll "$graph/applications?`$top=999&`$select=id,appId,displayName,requiredResourceAccess"
$grants = Get-GraphAll "$graph/oauth2PermissionGrants?`$top=999"

$spById = @{}; $spByAppId = @{}
foreach ($sp in $servicePrincipals) { $spById[$sp.id] = $sp; $spByAppId[$sp.appId] = $sp }

function Test-MicrosoftApp($sp) { $sp -and $microsoftTenants -contains $sp.appOwnerOrganizationId }

# Microsoft-apper med delegert samtykke i tenanten tas alltid med
$consentedClients = @{}
foreach ($grant in $grants) { $consentedClients[$grant.clientId] = $true }

# Apper som er med i revisjonen (appId -> navn og hva slags app)
$inScope = @{}
foreach ($app in $applications) {
    $inScope[$app.appId] = @{ Name = $app.displayName; Registration = $true; Enterprise = $spByAppId.ContainsKey($app.appId) }
}
foreach ($sp in $servicePrincipals) {
    if ($sp.servicePrincipalType -ne 'Application') { continue }          # managed identities o.l.
    if ((Test-MicrosoftApp $sp) -and -not $IncludeMicrosoftApps -and -not $consentedClients[$sp.id]) { continue }
    if (-not $inScope.ContainsKey($sp.appId)) {
        $inScope[$sp.appId] = @{ Name = $sp.displayName; Registration = $false; Enterprise = $true }
    }
}

# Én rad per (app, type, ressurs, rettighet)
$rows = @{}
function Get-Row($appId, $kind, $resourceSp, $resourceAppId, $permission) {
    $key = "$appId|$kind|$resourceAppId|$permission"
    if (-not $rows.ContainsKey($key)) {
        $app = $inScope[$appId]
        $rows[$key] = [pscustomobject]@{
            App          = $app.Name
            AppId        = $appId
            Apptype      = if ($app.Registration -and $app.Enterprise) { 'Registrering + enterprise' }
                           elseif ($app.Registration) { 'Bare registrering' } else { 'Bare enterprise (ekstern)' }
            Type         = $kind
            Ressurs      = if ($resourceSp) { $resourceSp.displayName } else { $resourceAppId }
            Rettighet    = $permission
            Konfigurert  = 'Nei'
            Samtykke     = ''
            Status       = ''
            Risiko       = ''
            SistePålogging = ''
            SisteMedBruker = ''
            SisteSomApp    = ''
            DagerSiden     = ''
        }
    }
    $rows[$key]
}

function Resolve-Permission($resourceSp, $kind, $id) {
    $list = if ($kind -eq 'Application') { $resourceSp.appRoles } else { $resourceSp.oauth2PermissionScopes }
    $match = $list | Where-Object id -eq $id | Select-Object -First 1
    if ($match) { $match.value } else { $id }
}

# 1) Konfigurert i app-registreringene
foreach ($app in $applications) {
    foreach ($resource in $app.requiredResourceAccess) {
        $resourceSp = $spByAppId[$resource.resourceAppId]
        foreach ($access in $resource.resourceAccess) {
            $kind = if ($access.type -eq 'Role') { 'Application' } else { 'Delegated' }
            $permission = if ($resourceSp) { Resolve-Permission $resourceSp $kind $access.id } else { $access.id }
            (Get-Row $app.appId $kind $resourceSp $resource.resourceAppId $permission).Konfigurert = 'Ja'
        }
    }
}

# 2) Gitte applikasjonsrettigheter (app-rolletildelinger på hver enterprise app)
$enterprise = $servicePrincipals | Where-Object { $inScope.ContainsKey($_.appId) -and $inScope[$_.appId].Enterprise }
$i = 0
foreach ($sp in $enterprise) {
    $i++
    Write-Progress -Activity 'Leser applikasjonsrettigheter' -Status $sp.displayName -PercentComplete (100 * $i / [Math]::Max(1, @($enterprise).Count))
    foreach ($assignment in (Get-GraphAll "$graph/servicePrincipals/$($sp.id)/appRoleAssignments")) {
        $resourceSp = $spById[$assignment.resourceId]
        $permission = if ($resourceSp) { Resolve-Permission $resourceSp 'Application' $assignment.appRoleId } else { $assignment.appRoleId }
        if ($assignment.appRoleId -eq '00000000-0000-0000-0000-000000000000') { $permission = '(standardtilgang)' }
        $row = Get-Row $sp.appId 'Application' $resourceSp $resourceSp.appId $permission
        $row.Samtykke = 'Admin'
    }
}
Write-Progress -Activity 'Leser applikasjonsrettigheter' -Completed

# 3) Gitte delegerte rettigheter (OAuth2-samtykker)
foreach ($grant in $grants) {
    $clientSp = $spById[$grant.clientId]
    if (-not $clientSp -or -not $inScope.ContainsKey($clientSp.appId)) { continue }
    $resourceSp = $spById[$grant.resourceId]
    foreach ($scope in ($grant.scope -split '\s+' | Where-Object { $_ })) {
        $row = Get-Row $clientSp.appId 'Delegated' $resourceSp $resourceSp.appId $scope
        if ($grant.consentType -eq 'AllPrincipals') {
            $row.Samtykke = 'Admin (alle brukere)'
        }
        elseif ($row.Samtykke -ne 'Admin (alle brukere)') {
            # Teller brukere som har samtykket selv
            $count = if ($row.Samtykke -match '^(\d+) bruker') { [int]$Matches[1] + 1 } else { 1 }
            $row.Samtykke = "$count bruker$(if ($count -ne 1) { 'e' })"
        }
    }
}

# Status og risiko
$writePattern = 'ReadWrite|FullControl|\.Send|Manage|RoleManagement|AppRoleAssignment|full_access|user_impersonation|\.Write\b|Write\.All'
foreach ($row in $rows.Values) {
    $row.Status = if ($row.Samtykke -and ($row.Konfigurert -eq 'Ja' -or $signInScopes -contains $row.Rettighet)) { 'Gitt' }
                  elseif ($row.Samtykke) { 'Gitt, ikke konfig.' }
                  else { 'Mangler samtykke' }

    $writes = $row.Rettighet -match $writePattern
    $row.Risiko = if ($row.Type -eq 'Application') {
        if ($writes) { 'Høy' } elseif ($row.Rettighet -match '\.All$') { 'Middels' } else { 'Lav' }
    } else {
        if ($writes -and $row.Samtykke -eq 'Admin (alle brukere)') { 'Høy' }
        elseif ($writes -or $row.Rettighet -match '\.All$') { 'Middels' } else { 'Lav' }
    }
}

# Siste pålogging per app (krever Entra ID P1 og rolle som kan lese påloggingsdata)
Write-Host 'Henter siste pålogging per app...'
$signIns = $null
try {
    $signIns = @{}
    foreach ($s in (Get-GraphAll "https://graph.microsoft.com/beta/reports/servicePrincipalSignInActivities?`$top=999")) {
        $signIns[$s.appId] = $s
    }
    # Helt tom rapport betyr nesten alltid at data mangler (f.eks. uten Entra ID P1),
    # ikke at ingen apper har logget inn
    if ($signIns.Count -eq 0) {
        $signIns = $null
        Write-Warning 'Påloggingsrapporten er tom (mangler tenanten Entra ID P1?). Kolonnene blir «Ukjent».'
    }
}
catch {
    $signIns = $null
    $message = if ($_.ErrorDetails.Message) { ($_.ErrorDetails.Message | ConvertFrom-Json).error.message } else { $_.Exception.Message }
    Write-Warning "Fikk ikke lest påloggingsrapporten, kolonnene blir «Ukjent»: $message"
}

function Format-SignIn($activity) {
    $time = $activity.lastSignInDateTime
    if ($time) { ([datetime]$time).ToLocalTime().ToString('yyyy-MM-dd HH:mm') } else { '' }
}

$now = Get-Date
foreach ($row in $rows.Values) {
    if ($null -eq $signIns) {
        $row.SistePålogging = 'Ukjent'; $row.SisteMedBruker = 'Ukjent'; $row.SisteSomApp = 'Ukjent'
        continue
    }
    $s = $signIns[$row.AppId]
    $last = $s.lastSignInActivity.lastSignInDateTime
    if (-not $last) { $row.SistePålogging = 'Ingen registrert'; continue }
    $row.SistePålogging = Format-SignIn $s.lastSignInActivity
    $row.SisteMedBruker = Format-SignIn $s.delegatedClientSignInActivity
    $row.SisteSomApp = Format-SignIn $s.applicationAuthenticationClientSignInActivity
    $row.DagerSiden = [int]($now - ([datetime]$last).ToLocalTime()).TotalDays
}

$result = $rows.Values | Sort-Object App, Type, Ressurs, Rettighet

if (-not $CsvPath) {
    $CsvPath = Join-Path (Get-Location) "app-rettigheter-$TenantId-$(Get-Date -Format 'yyyyMMdd-HHmm').csv"
}
$result | Export-Csv -Path $CsvPath -Encoding utf8BOM -Delimiter ';'
$CsvPath = (Resolve-Path $CsvPath).Path

# Oppsummering
$apps = @($result | Select-Object -ExpandProperty AppId -Unique)
Write-Host ''
Write-Host "Apper med rettigheter: $($apps.Count)   Rettigheter: $(@($result).Count)"
Write-Host ("  Applikasjon: {0}   Delegert: {1}" -f @($result | Where-Object Type -eq 'Application').Count, @($result | Where-Object Type -eq 'Delegated').Count)
Write-Host ("  Høy risiko: {0}   Mangler samtykke: {1}   Gitt, ikke konfig.: {2}" -f `
    @($result | Where-Object Risiko -eq 'Høy').Count,
    @($result | Where-Object Status -eq 'Mangler samtykke').Count,
    @($result | Where-Object Status -eq 'Gitt, ikke konfig.').Count)
if ($null -ne $signIns) {
    $appRows = $result | Group-Object AppId | ForEach-Object { $_.Group[0] }
    $unused = @($appRows | Where-Object { $_.SistePålogging -eq 'Ingen registrert' -or ($_.DagerSiden -ne '' -and [int]$_.DagerSiden -gt 90) })
    $unusedHigh = @($unused | Where-Object { $id = $_.AppId; $result | Where-Object { $_.AppId -eq $id -and $_.Risiko -eq 'Høy' } })
    Write-Host ("  Apper uten pålogging siste 90 dager: {0} (av dem med høy risiko: {1})" -f $unused.Count, $unusedHigh.Count)
}
if (-not $IncludeMicrosoftApps) { Write-Host '  (Microsofts egne apper uten samtykke i tenanten er utelatt; bruk -IncludeMicrosoftApps for å ta dem med)' }
Write-Host ''
Write-Host "Lagret: $CsvPath"

if ($PassThru) { $result }

<#
.SYNOPSIS
  Finner apper som har tilgang til SharePoint og OneDrive, særlig de som kan lese
  (eller mer) i alle områder i tenanten ("SharePoint all read").

.DESCRIPTION
  Bygger på Get-AppPermissions.ps1 (samme innlogging og krav) og tar bare med
  SharePoint- og OneDrive-rettigheter som faktisk er gitt, fra både Microsoft Graph
  (Sites.*, Files.*) og SharePoint-API-et (Sites.*, AllSites.*, MyFiles.*).
  Leser bare, og endrer ingenting.

  Hver rettighet får:
    Omfang   Hele tenanten      *.All og AllSites.*: alle områder og OneDrive-er
             Utvalgte områder   Sites.Selected o.l.: bare områder appen er tildelt
             Brukerens filer    Files.Read, MyFiles.* o.l.: bare den innloggede brukerens egne filer
    Tilgang  Lese / Skrive / Administrere / Full kontroll / Tildelt per område
    Alvor    Høy      applikasjonsrettighet til hele tenanten: appen kan nå ALLE områder
                      uten en innlogget bruker
             Middels  delegert rettighet til hele tenanten: appen når det brukeren selv
                      har tilgang til, i alle områder
             Lav      resten

  Hvilke områder apper med Sites.Selected er tildelt, vises ikke. Det krever
  Sites.FullControl.All for å lese.

  Krever Azure CLI innlogget i riktig tenant som en bruker som kan lese katalogen
  (f.eks. Global Reader). Siste pålogging krever i tillegg Entra ID P1.
    az login --tenant <tenant-id> --allow-no-subscriptions

  Resultatet skrives til en CSV-fil (semikolon og UTF-8). Uten -CsvPath lagres den i
  gjeldende mappe som sharepoint-tilgang-<tenant-id>-<dato og tid>.csv.

.EXAMPLE
  ./scripts/Get-SharePointAppAccess.ps1 -TenantId 3af5c92b-38f9-48ce-8746-82bb4c113032

.EXAMPLE
  # Bare apper som kan nå alle områder uten bruker
  ./scripts/Get-SharePointAppAccess.ps1 -TenantId <tenant-id> -PassThru |
      Where-Object Alvor -eq 'Høy' | Format-Table App, Rettighet, Tilgang, SistePålogging
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $TenantId,
    [string] $CsvPath,
    [switch] $IncludeMicrosoftApps,
    # Sender radene videre i pipelinen i tillegg til CSV-filen
    [switch] $PassThru,
    # Rader fra Get-AppPermissions.ps1 -PassThru som allerede er hentet (hentes ellers på nytt)
    [object[]] $AppPermissions,
    # Ikke skriv CSV-fil (f.eks. når Show-AccessReview.ps1 viser resultatet)
    [switch] $NoCsv
)

$ErrorActionPreference = 'Stop'

if ($AppPermissions) {
    $all = $AppPermissions
}
else {
    # Alle app-rettigheter (tenant-sjekken gjøres der). CSV-en derfra trengs ikke.
    $allCsv = Join-Path ([System.IO.Path]::GetTempPath()) "app-rettigheter-$([guid]::NewGuid().ToString('N')).csv"
    try {
        $all = & (Join-Path $PSScriptRoot 'Get-AppPermissions.ps1') -TenantId $TenantId -CsvPath $allCsv `
            -IncludeMicrosoftApps:$IncludeMicrosoftApps -PassThru 6>$null
    }
    finally {
        Remove-Item $allCsv -ErrorAction SilentlyContinue
    }
}

$sharePointResources = @('Microsoft Graph', 'Office 365 SharePoint Online')
$sharePointPattern = '^(Sites|Files|AllSites|MyFiles)\.'

function Get-Scope([string] $permission) {
    if ($permission -match 'Selected') { 'Utvalgte områder' }
    elseif ($permission -match '\.All$' -or $permission -match '^AllSites\.') { 'Hele tenanten' }
    else { 'Brukerens filer' }
}

function Get-Access([string] $permission) {
    if ($permission -match 'FullControl') { 'Full kontroll' }
    elseif ($permission -match 'Manage') { 'Administrere' }
    elseif ($permission -match 'ReadWrite|Write') { 'Skrive' }
    elseif ($permission -match 'Selected') { 'Tildelt per område' }
    else { 'Lese' }
}

$result = foreach ($row in $all) {
    if ($row.Ressurs -notin $sharePointResources) { continue }
    if ($row.Rettighet -notmatch $sharePointPattern) { continue }
    if (-not $row.Samtykke) { continue }   # bare det som faktisk er gitt

    $scope = Get-Scope $row.Rettighet
    [pscustomobject]@{
        App            = $row.App
        AppId          = $row.AppId
        Type           = $row.Type
        Ressurs        = $row.Ressurs
        Rettighet      = $row.Rettighet
        Omfang         = $scope
        Tilgang        = Get-Access $row.Rettighet
        Alvor          = if ($scope -eq 'Hele tenanten' -and $row.Type -eq 'Application') { 'Høy' }
                         elseif ($scope -eq 'Hele tenanten') { 'Middels' }
                         else { 'Lav' }
        Samtykke       = $row.Samtykke
        SistePålogging = $row.SistePålogging
        DagerSiden     = $row.DagerSiden
        Apptype        = $row.Apptype
        Aktiv          = $row.Aktiv
        Status         = $row.Status
        EnterpriseAppId = $row.EnterpriseAppId
        RegistreringId  = $row.RegistreringId
        RessursId       = $row.RessursId
    }
}

$severityOrder = @{ 'Høy' = 0; 'Middels' = 1; 'Lav' = 2 }
$result = @($result | Sort-Object { $severityOrder[$_.Alvor] }, App, Rettighet)

if ($NoCsv) { if ($PassThru) { $result }; return }

if (-not $CsvPath) {
    $CsvPath = Join-Path (Get-Location) "sharepoint-tilgang-$TenantId-$(Get-Date -Format 'yyyyMMdd-HHmm').csv"
}
$result | Export-Csv -Path $CsvPath -Encoding utf8BOM -Delimiter ';'
$CsvPath = (Resolve-Path $CsvPath).Path

# Oppsummering per app (høyeste alvor per app)
$apps = $result | Group-Object AppId | ForEach-Object { $_.Group[0] }
$wideApp = @($result | Where-Object Alvor -eq 'Høy' | Select-Object -ExpandProperty AppId -Unique)
$wideDelegated = @($result | Where-Object Alvor -eq 'Middels' | Select-Object -ExpandProperty AppId -Unique |
    Where-Object { $_ -notin $wideApp })
$selected = @($result | Where-Object Omfang -eq 'Utvalgte områder' | Select-Object -ExpandProperty AppId -Unique)

Write-Host ''
Write-Host "Apper med tilgang til SharePoint/OneDrive: $(@($apps).Count)   Rettigheter: $($result.Count)"
Write-Host "  Alle områder uten bruker (Høy):     $($wideApp.Count)"
Write-Host "  Alle områder via bruker (Middels):  $($wideDelegated.Count)"
Write-Host "  Bare utvalgte områder (Selected):   $($selected.Count)"

$high = $result | Where-Object Alvor -eq 'Høy' | Group-Object App
if ($high) {
    Write-Host ''
    Write-Host 'Apper som kan nå alle områder uten en innlogget bruker:'
    foreach ($group in $high) {
        $first = $group.Group[0]
        $lastSignIn = if ($first.DagerSiden -ne '') { "$($first.SistePålogging) ($($first.DagerSiden) dager)" } else { $first.SistePålogging }
        Write-Host ("  {0,-45} {1,-55} sist: {2}" -f $group.Name, (($group.Group.Rettighet | Sort-Object -Unique) -join ', '), $lastSignIn)
    }
}
Write-Host ''
Write-Host "Lagret: $CsvPath"

if ($PassThru) { $result }

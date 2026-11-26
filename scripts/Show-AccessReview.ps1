<#
.SYNOPSIS
  Vindu for tilgangssjekk i en tenant: app-rettigheter, SharePoint-tilgang, inaktive brukere
  og delte postbokser.

.DESCRIPTION
  Startsiden gir disse valgene:
    App-rettigheter      alle rettigheter på app-registreringer og enterprise apps
                         (Get-AppPermissions.ps1), med siste pålogging og en anbefaling per rad
    SharePoint-tilgang   apper som når SharePoint og OneDrive, særlig de som kan lese alt
                         (Get-SharePointAppAccess.ps1)
    Inaktive brukere     starter Rydd-InaktiveBrukere.ps1 (antire-entra-admin-scripts) i eget vindu
    Delte postbokser     Oversikt: starter Hent-Postbokstilgang.ps1 (CSV over hvem som har tilgang)
                         Gi eller fjern tilgang: skjema som starter Endre-Postbokstilgang.ps1,
                         med «Vis hva som skjer» (-WhatIf) eller «Utfør» (ber om bekreftelse)
                         Begge ligger i antire-entra-admin-scripts og kjører i eget vindu.
                         Mangler modulen ExchangeOnlineManagement, får du beskjed og kan
                         installere den derfra.

  Alt kjører med din egen innlogging i Azure CLI. Står Azure CLI på en annen tenant,
  byttes det til riktig tenant mens vinduet er åpent, og tilbake når det lukkes.

  Knappene Deaktiver app, Slett app og Fjern rettighet vises bare hvis du har en av
  rollene Global Administrator, Application Administrator eller Cloud Application
  Administrator. Ellers kan du bare se og eksportere listen.
    Deaktiver app    blokkerer pålogging for enterprise-appen (kan slås på igjen i Entra)
    Slett app        sletter app-registreringen (og dermed enterprise-appen) hvis den er
                     registrert i tenanten, ellers enterprise-appen. Kan gjenopprettes fra
                     «Deleted applications» i Entra i 30 dager
    Fjern rettighet  fjerner valgte rettigheter (applikasjonsrettighet eller delegert
                     samtykke) og lar appen ellers være
  Microsofts egne apper kan ikke deaktiveres eller slettes herfra. Alle handlinger
  logges til en CSV-fil i Dokumenter\Antire tilgangssjekk.

  Krever PowerShell 7, Azure CLI, og Get-AppPermissions.ps1 og Get-SharePointAppAccess.ps1
  i samme mappe. For å lese trengs en rolle som kan lese katalogen (f.eks. Global Reader);
  siste pålogging krever i tillegg Entra ID P1.

.PARAMETER TenantId
  Tenanten som sjekkes. Uten denne spør vinduet ved oppstart: Antire
  (94f56205-4854-45cd-b54c-3383e8abfb3f) eller AGK, som er dev
  (3af5c92b-38f9-48ce-8746-82bb4c113032). Alt annet enn Antire gir AGK.

.PARAMETER InactiveUsersScript
  Stien til Rydd-InaktiveBrukere.ps1. Standard: ..\..\antire-entra-admin-scripts\ fra denne mappen.
  Postboksskriptene hentes fra samme mappe.

.EXAMPLE
  # Spør om Antire eller AGK
  ./scripts/Show-AccessReview.ps1

.EXAMPLE
  # Rett mot AGK (dev), uten spørsmål
  ./scripts/Show-AccessReview.ps1 -TenantId 3af5c92b-38f9-48ce-8746-82bb4c113032
#>
#Requires -Version 7
[CmdletBinding()]
param(
    [string] $TenantId,
    [string] $InactiveUsersScript = (Join-Path $PSScriptRoot '..\..\antire-entra-admin-scripts\Rydd-InaktiveBrukere.ps1'),
    [string] $LogFolder = (Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'Antire tilgangssjekk')
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()

# Kjente tenanter: navn -> tenant-ID
$knownTenants = [ordered]@{
    'Antire' = '94f56205-4854-45cd-b54c-3383e8abfb3f'
    'AGK'    = '3af5c92b-38f9-48ce-8746-82bb4c113032'   # dev
}

# Uten -TenantId: spør om Antire eller AGK. Alt annet enn Antire (også å lukke) gir AGK.
function Select-Tenant {
    $dialog = New-Object System.Windows.Forms.Form
    $dialog.Text = 'Tilgangssjekk – velg tenant'
    $dialog.Size = New-Object System.Drawing.Size(420, 190)
    $dialog.StartPosition = 'CenterScreen'
    $dialog.FormBorderStyle = 'FixedDialog'
    $dialog.MaximizeBox = $false
    $dialog.MinimizeBox = $false
    $dialog.Font = New-Object System.Drawing.Font('Segoe UI', 9)

    $label = New-Object System.Windows.Forms.Label
    $label.Text = 'Hvilken tenant skal sjekkes?'
    $label.Location = New-Object System.Drawing.Point(20, 20)
    $label.AutoSize = $true
    $label.Font = New-Object System.Drawing.Font('Segoe UI', 11)

    $btnAntire = New-Object System.Windows.Forms.Button
    $btnAntire.Text = 'Antire'
    $btnAntire.Location = New-Object System.Drawing.Point(20, 65)
    $btnAntire.Size = New-Object System.Drawing.Size(170, 40)
    $btnAntire.DialogResult = 'Yes'

    $btnAgk = New-Object System.Windows.Forms.Button
    $btnAgk.Text = 'AGK (dev)'
    $btnAgk.Location = New-Object System.Drawing.Point(210, 65)
    $btnAgk.Size = New-Object System.Drawing.Size(170, 40)
    $btnAgk.DialogResult = 'No'

    $dialog.Controls.AddRange(@($label, $btnAntire, $btnAgk))
    $dialog.AcceptButton = $btnAgk
    $dialog.CancelButton = $btnAgk
    $answer = $dialog.ShowDialog()
    $dialog.Dispose()
    if ($answer -eq 'Yes') { 'Antire' } else { 'AGK' }
}

if (-not $TenantId) {
    $TenantId = $knownTenants[(Select-Tenant)]
}
$tenantName = ($knownTenants.GetEnumerator() | Where-Object Value -eq $TenantId | Select-Object -First 1).Key
$tenantLabel = if ($tenantName) { "$tenantName ($TenantId)" } else { $TenantId }

# Entra-roller (roleTemplateId) som kan deaktivere og slette apper og fjerne rettigheter
$writeRoles = @{
    '62e90394-69f5-4237-9190-012177145e10' = 'Global Administrator'
    '9b895d92-2cd3-44c7-9d02-a6ac2d5ea5c3' = 'Application Administrator'
    '158c047a-c907-4556-b7ef-446551a6b5f7' = 'Cloud Application Administrator'
}

$script:connected = $false
$script:canWrite = $false
$script:me = $null
$script:myWriteRoles = @()
$script:originalSubscription = $null
$script:token = $null
$script:tokenTime = [datetime]::MinValue
$script:appRows = $null      # rader fra Get-AppPermissions.ps1 (hentes én gang)
$script:view = $null         # 'apps' eller 'sharepoint'
$script:viewRows = @()       # radene i tabellen nå
$script:table = $null
$script:fetchNote = ''

# ---------------------------------------------------------------------------
# Azure CLI og Graph (personlig innlogging)
# ---------------------------------------------------------------------------
function Get-Token {
    if (-not $script:token -or ((Get-Date) - $script:tokenTime).TotalMinutes -gt 30) {
        $script:token = az account get-access-token --resource-type ms-graph --query accessToken -o tsv
        if ($LASTEXITCODE -ne 0 -or -not $script:token) { throw 'Fikk ikke Graph-token fra Azure CLI.' }
        $script:tokenTime = Get-Date
    }
    $script:token
}

function Invoke-Graph([string] $Method, [string] $Path, $Body) {
    $uri = if ($Path -like 'https://*') { $Path } else { "https://graph.microsoft.com/v1.0$Path" }
    $params = @{ Method = $Method; Uri = $uri; Headers = @{ Authorization = "Bearer $(Get-Token)" } }
    if ($null -ne $Body) {
        $params.Body = $Body | ConvertTo-Json -Depth 5 -Compress
        $params.ContentType = 'application/json'
    }
    Invoke-RestMethod @params
}

function Get-GraphAll([string] $Path) {
    $items = [System.Collections.Generic.List[object]]::new()
    $url = $Path
    while ($url) {
        $response = Invoke-Graph GET $url
        foreach ($item in $response.value) { $items.Add($item) }
        $url = $response.'@odata.nextLink'
    }
    $items
}

function Get-ErrorText($errorRecord) {
    $details = $errorRecord.ErrorDetails.Message
    if ($details) {
        try { return ($details | ConvertFrom-Json).error.message } catch { return $details }
    }
    $errorRecord.Exception.Message
}

function Connect-Tenant([bool] $AllowLogin) {
    if (-not $script:originalSubscription) {
        $script:originalSubscription = az account show --query id -o tsv 2>$null
    }
    $current = az account show --query tenantId -o tsv 2>$null
    if ($current -ne $TenantId) {
        $account = az account list --all --query "[?tenantId=='$TenantId'] | [0].id" -o tsv 2>$null
        if ($account) {
            az account set --subscription $account
        }
        elseif ($AllowLogin) {
            az login --tenant $TenantId --allow-no-subscriptions --output none
        }
        else {
            return $false
        }
        $current = az account show --query tenantId -o tsv 2>$null
        if ($current -ne $TenantId) { throw "Azure CLI står i tenant '$current', ikke '$TenantId'." }
        $script:token = $null
    }

    $script:me = Invoke-Graph GET '/me?$select=id,displayName,userPrincipalName'
    $roles = @(Get-GraphAll '/me/transitiveMemberOf/microsoft.graph.directoryRole?$select=displayName,roleTemplateId')
    $script:myWriteRoles = @($roles | Where-Object { $writeRoles.ContainsKey([string]$_.roleTemplateId) } |
        ForEach-Object { $writeRoles[[string]$_.roleTemplateId] })
    $script:canWrite = $script:myWriteRoles.Count -gt 0
    $script:connected = $true
    $true
}

function Restore-AzAccount {
    if ($script:originalSubscription) {
        $current = az account show --query id -o tsv 2>$null
        if ($current -ne $script:originalSubscription) {
            az account set --subscription $script:originalSubscription 2>$null
        }
    }
}

# ---------------------------------------------------------------------------
# Anbefaling per rad
# ---------------------------------------------------------------------------
$signInScopes = @('openid', 'profile', 'email', 'offline_access')

function Get-Recommendation($row) {
    $risk = if ($row.PSObject.Properties['Risiko']) { $row.Risiko } else { $row.Alvor }
    $days = if ("$($row.DagerSiden)" -ne '') { [int]$row.DagerSiden } else { $null }
    $never = $row.SistePålogging -eq 'Ingen registrert'
    $unknown = -not $row.SistePålogging -or $row.SistePålogging -eq 'Ukjent'
    $microsoft = $row.Apptype -eq 'Microsoft'

    if (-not $row.Samtykke) {
        return 'Ikke gitt, bare satt opp i registreringen. Fjern den fra registreringen hvis den ikke trengs.'
    }
    if ($row.Aktiv -eq 'Nei' -and -not $microsoft) {
        return 'Appen er deaktivert. Slett den hvis ingen har savnet den.'
    }
    if ($microsoft) {
        if ($risk -eq 'Høy' -or ($risk -eq 'Middels' -and $row.Samtykke -eq 'Admin (alle brukere)')) {
            return 'Microsoft-app: ikke slett. Vurder å fjerne rettigheten hvis ingen trenger den.'
        }
        return ''
    }
    if ($never) {
        if ($risk -eq 'Høy') { return 'Aldri brukt: slett appen.' }
        return 'Aldri brukt: deaktiver appen, og slett den hvis ingen savner den.'
    }
    if ($null -ne $days -and $days -gt 180) {
        if ($risk -eq 'Høy') { return "Ikke brukt på $days dager: slett appen." }
        return "Ikke brukt på $days dager: deaktiver appen."
    }
    if ($null -ne $days -and $days -gt 90) {
        return "Lite brukt ($days dager siden): deaktiver appen og følg med."
    }
    if ($row.Type -eq 'Application' -and $row.Rettighet -match '^(Sites|Files)\.(Read|ReadWrite|Manage|FullControl)\.All$') {
        return 'Bytt til Sites.Selected, så appen bare når områdene den trenger.'
    }
    if ($row.Type -eq 'Application' -and $row.Rettighet -match '^(Mail|Calendars|Contacts|MailboxSettings)\.|full_access_as_app') {
        return 'Begrens til bestemte postbokser (RBAC for Applications i Exchange).'
    }
    if ($row.Type -eq 'Delegated' -and $risk -eq 'Høy' -and $row.Samtykke -eq 'Admin (alle brukere)') {
        return 'Krev tildeling, så bare utvalgte brukere kan bruke appen.'
    }
    if ($row.Status -eq 'Gitt, ikke konfig.' -and $row.Apptype -ne 'Bare enterprise (ekstern)' -and $row.Rettighet -notin $signInScopes) {
        return 'Gitt uten å stå i registreringen: fjern rettigheten hvis appen ikke trenger den.'
    }
    if ($unknown -and $risk -eq 'Høy') {
        return 'Sjekk om appen er i bruk (siste pålogging er ukjent).'
    }
    ''
}

# ---------------------------------------------------------------------------
# Handlinger (bare med skriverolle)
# ---------------------------------------------------------------------------
function Write-ActionLog([object[]] $Entries) {
    if (-not $Entries) { return $null }
    New-Item -ItemType Directory -Force -Path $LogFolder | Out-Null
    $file = Join-Path $LogFolder "tilgangslogg-$(Get-Date -Format 'yyyyMMdd-HHmmss').csv"
    $Entries | Export-Csv -Path $file -Encoding utf8BOM -Delimiter ';'
    $file
}

function New-LogEntry($Action, $Row, $Status, $ErrorText = '') {
    [pscustomobject]@{
        Tid       = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
        Handling  = $Action
        App       = $Row.App
        AppId     = $Row.AppId
        Rettighet = if ($Action -eq 'Fjern rettighet') { "$($Row.Rettighet) ($($Row.Type), $($Row.Ressurs))" } else { '' }
        Status    = $Status
        Feil      = $ErrorText
        UtfortAv  = $script:me.userPrincipalName
        Tenant    = $TenantId
    }
}

function Disable-App($Row) {
    Invoke-Graph PATCH "/servicePrincipals/$($Row.EnterpriseAppId)" @{ accountEnabled = $false } | Out-Null
}

function Remove-App($Row) {
    if ($Row.RegistreringId) { Invoke-Graph DELETE "/applications/$($Row.RegistreringId)" | Out-Null }
    else { Invoke-Graph DELETE "/servicePrincipals/$($Row.EnterpriseAppId)" | Out-Null }
}

function Remove-Permission($Row) {
    $spId = $Row.EnterpriseAppId
    if ($Row.Type -eq 'Application') {
        $roleId = if ($Row.Rettighet -eq '(standardtilgang)') { '00000000-0000-0000-0000-000000000000' }
                  else {
                      $resource = Invoke-Graph GET "/servicePrincipals/$($Row.RessursId)?`$select=appRoles"
                      ($resource.appRoles | Where-Object value -eq $Row.Rettighet | Select-Object -First 1).id
                  }
        if (-not $roleId) { throw "Fant ikke rettigheten $($Row.Rettighet) hos $($Row.Ressurs)." }
        $assignments = @(Get-GraphAll "/servicePrincipals/$spId/appRoleAssignments" |
            Where-Object { $_.resourceId -eq $Row.RessursId -and $_.appRoleId -eq $roleId })
        if (-not $assignments) { throw 'Rettigheten er allerede fjernet.' }
        foreach ($a in $assignments) { Invoke-Graph DELETE "/servicePrincipals/$spId/appRoleAssignments/$($a.id)" | Out-Null }
    }
    else {
        $grants = @(Get-GraphAll "/oauth2PermissionGrants?`$filter=clientId eq '$spId' and resourceId eq '$($Row.RessursId)'" |
            Where-Object { ($_.scope -split '\s+') -contains $Row.Rettighet })
        if (-not $grants) { throw 'Samtykket er allerede fjernet.' }
        foreach ($g in $grants) {
            $remaining = @($g.scope -split '\s+' | Where-Object { $_ -and $_ -ne $Row.Rettighet })
            if ($remaining.Count -eq 0) { Invoke-Graph DELETE "/oauth2PermissionGrants/$($g.id)" | Out-Null }
            else { Invoke-Graph PATCH "/oauth2PermissionGrants/$($g.id)" @{ scope = ($remaining -join ' ') } | Out-Null }
        }
    }
}

# ---------------------------------------------------------------------------
# Vindu
# ---------------------------------------------------------------------------
function New-Control([string] $Type, [int] $X, [int] $Y, [int] $W = 0, [int] $H = 0, [string] $Text) {
    $c = New-Object "System.Windows.Forms.$Type"
    $c.Location = New-Object System.Drawing.Point($X, $Y)
    if ($W -gt 0) { $c.Size = New-Object System.Drawing.Size($W, $(if ($H -gt 0) { $H } else { $c.Height })) }
    elseif ($Type -in 'Label', 'CheckBox') { $c.AutoSize = $true }
    if ($Text) { $c.Text = $Text }
    $c
}

$form = New-Object System.Windows.Forms.Form
$form.Text = "Tilgangssjekk – $tenantLabel"
$form.Size = New-Object System.Drawing.Size(1400, 860)
$form.StartPosition = 'CenterScreen'
$form.Font = New-Object System.Drawing.Font('Segoe UI', 9)

# --- Startside -------------------------------------------------------------
$startPanel = New-Object System.Windows.Forms.Panel
$startPanel.Dock = 'Fill'

$lblTitle = New-Control Label 40 30 -Text 'Tilgangssjekk'
$lblTitle.Font = New-Object System.Drawing.Font('Segoe UI', 20, [System.Drawing.FontStyle]::Bold)
$lblTenant = New-Control Label 42 82 -Text "Tenant: $tenantLabel"
$lblUser = New-Control Label 42 104 -Text 'Kobler til...'
$lblUser.ForeColor = [System.Drawing.Color]::DimGray
$btnLogin = New-Control Button 42 130 220 32 'Logg inn med Azure CLI'
$btnLogin.Visible = $false

function New-Choice([int] $Y, [string] $Title, [string] $Description) {
    $button = New-Control Button 42 $Y 260 56 $Title
    $button.Font = New-Object System.Drawing.Font('Segoe UI', 11, [System.Drawing.FontStyle]::Bold)
    $label = New-Control Label 320 ($Y + 8) 760 48 $Description
    $label.AutoSize = $false
    $startPanel.Controls.AddRange(@($button, $label))
    $button
}

$btnApps = New-Choice 190 'App-rettigheter' 'Alle rettigheter på app-registreringer og enterprise apps: delegert eller applikasjon, samtykke, risiko, siste pålogging og en anbefaling per rettighet.'
$btnSharePoint = New-Choice 266 'SharePoint-tilgang' 'Apper som når SharePoint og OneDrive. Viser særlig apper som kan lese (eller mer) i alle områder, og om det skjer med eller uten en innlogget bruker.'
$btnInactive = New-Choice 342 'Inaktive brukere' 'Starter Rydd-InaktiveBrukere.ps1 i eget vindu: inaktive gjester og medlemmer, grupper og eiere, beskyttelse, og deaktivering eller sletting.'
$lblInactivePath = New-Control Label 320 392 -Text ''
$lblInactivePath.ForeColor = [System.Drawing.Color]::DimGray

$lblMailboxHeading = New-Control Label 42 432 -Text 'Delte postbokser'
$lblMailboxHeading.Font = New-Object System.Drawing.Font('Segoe UI', 12, [System.Drawing.FontStyle]::Bold)
$btnMailboxOverview = New-Choice 466 'Postbokser: oversikt' 'Starter Hent-Postbokstilgang.ps1 i eget vindu: hvem som har FullAccess, SendAs og Send on behalf på delte postbokser. Lagrer en CSV som eieren kan gå gjennom (Beholde, Godkjent av).'
$btnMailboxChange = New-Choice 542 'Postbokser: gi/fjern' 'For IT: gi eller fjern tilgang til en delt postboks når eieren har godkjent, eller fjern det eieren har merket Beholde = Nei i oversikten. Starter Endre-Postbokstilgang.ps1.'
$lblExchangeModule = New-Control Label 42 610 -Text ''

$lblStartInfo = New-Control Label 42 650 1000 60 ''
$lblStartInfo.AutoSize = $false
$lblStartInfo.ForeColor = [System.Drawing.Color]::DimGray

$startPanel.Controls.AddRange(@($lblTitle, $lblTenant, $lblUser, $btnLogin, $lblInactivePath, $lblMailboxHeading, $lblExchangeModule, $lblStartInfo))

# --- Resultatside ----------------------------------------------------------
$resultPanel = New-Object System.Windows.Forms.Panel
$resultPanel.Dock = 'Fill'
$resultPanel.Visible = $false

$btnBack = New-Control Button 12 10 100 30 '← Tilbake'
$lblView = New-Control Label 125 12 -Text ''
$lblView.Font = New-Object System.Drawing.Font('Segoe UI', 13, [System.Drawing.FontStyle]::Bold)
$btnRefresh = New-Control Button 1150 10 110 30 'Hent på nytt'
$btnRefresh.Anchor = 'Top,Right'
$btnExport = New-Control Button 1268 10 110 30 'Eksporter CSV'
$btnExport.Anchor = 'Top,Right'

$lblFilter = New-Control Label 12 56 -Text 'Vis:'
$cmbFilter = New-Control ComboBox 45 52 230
$cmbFilter.DropDownStyle = 'DropDownList'
[void]$cmbFilter.Items.AddRange(@('Alle', 'Har anbefaling', 'Høy risiko', 'Ikke brukt siste 90 dager', 'Deaktiverte apper', 'Bare applikasjonsrettigheter', 'Bare delegerte rettigheter'))
$cmbFilter.SelectedIndex = 0
$lblSearch = New-Control Label 295 56 -Text 'Søk:'
$txtSearch = New-Control TextBox 333 52 300
$btnSelectVisible = New-Control Button 650 50 140 28 'Velg alle synlige'
$btnClearSelection = New-Control Button 798 50 110 28 'Fjern valg'

$btnDisable = New-Control Button 12 88 150 32 'Deaktiver app'
$btnDisable.ForeColor = [System.Drawing.Color]::DarkOrange
$btnDelete = New-Control Button 170 88 150 32 'Slett app'
$btnDelete.ForeColor = [System.Drawing.Color]::DarkRed
$btnRemovePermission = New-Control Button 328 88 150 32 'Fjern rettighet'
$btnRemovePermission.ForeColor = [System.Drawing.Color]::DarkRed
$lblRights = New-Control Label 490 96 -Text ''
$lblRights.ForeColor = [System.Drawing.Color]::DimGray

$lblStatus = New-Control Label 12 128 -Text ''
$progress = New-Control ProgressBar 1150 128 228 14
$progress.Anchor = 'Top,Right'
$progress.Style = 'Marquee'
$progress.Visible = $false

$grid = New-Control DataGridView 12 150 1366 660
$grid.Anchor = 'Top,Bottom,Left,Right'
$grid.AllowUserToAddRows = $false
$grid.AllowUserToDeleteRows = $false
$grid.SelectionMode = 'FullRowSelect'
$grid.AutoSizeColumnsMode = 'DisplayedCells'
$grid.ShowCellToolTips = $true

$resultPanel.Controls.AddRange(@($btnBack, $lblView, $btnRefresh, $btnExport, $lblFilter, $cmbFilter, $lblSearch, $txtSearch,
    $btnSelectVisible, $btnClearSelection, $btnDisable, $btnDelete, $btnRemovePermission, $lblRights, $lblStatus, $progress, $grid))

$form.Controls.AddRange(@($resultPanel, $startPanel))

# Kolonner per visning (Valgt først, interne ID-er skjult)
$columns = @{
    apps       = 'Valgt', 'App', 'Rettighetstype', 'Rettighet', 'Ressurs', 'Anbefaling', 'Risiko', 'Samtykke', 'Status', 'Aktiv', 'SistePålogging', 'DagerSiden', 'SisteMedBruker', 'SisteSomApp', 'Apptype', 'Konfigurert', 'AppId'
    sharepoint = 'Valgt', 'App', 'Rettighetstype', 'Rettighet', 'Ressurs', 'Anbefaling', 'Alvor', 'Omfang', 'Tilgang', 'Samtykke', 'Aktiv', 'SistePålogging', 'DagerSiden', 'Apptype', 'AppId'
}
$titles = @{ apps = 'App-rettigheter'; sharepoint = 'SharePoint-tilgang' }

# ---------------------------------------------------------------------------
# Visning
# ---------------------------------------------------------------------------
function Update-StartPage {
    if ($script:connected) {
        $roles = if ($script:myWriteRoles) { "Skrivetilgang: $($script:myWriteRoles -join ', ')" } else { 'Kun lesing (ingen rolle som kan endre apper)' }
        $lblUser.Text = "Innlogget som $($script:me.userPrincipalName)   |   $roles"
        $btnLogin.Visible = $false
    }
    $btnApps.Enabled = $script:connected
    $btnSharePoint.Enabled = $script:connected

    if (Test-Path $InactiveUsersScript) {
        $lblInactivePath.Text = (Resolve-Path $InactiveUsersScript).Path
        $btnInactive.Text = 'Inaktive brukere'
    }
    else {
        $lblInactivePath.Text = "Fant ikke $InactiveUsersScript – klikk for å velge filen."
        $btnInactive.Text = 'Inaktive brukere...'
    }

    $exo = Get-Module -ListAvailable -Name ExchangeOnlineManagement | Sort-Object Version -Descending | Select-Object -First 1
    if ($exo) {
        $lblExchangeModule.Text = "Exchange Online-modulen er installert (versjon $($exo.Version))."
        $lblExchangeModule.ForeColor = [System.Drawing.Color]::DimGray
    }
    else {
        $lblExchangeModule.Text = 'Exchange Online-modulen er ikke installert. Du får hjelp til å installere den når du klikker en postboksknapp.'
        $lblExchangeModule.ForeColor = [System.Drawing.Color]::DarkOrange
    }
}

function Update-Rights {
    $btnDisable.Visible = $script:canWrite
    $btnDelete.Visible = $script:canWrite
    $btnRemovePermission.Visible = $script:canWrite
    $lblRights.Left = if ($script:canWrite) { 490 } else { 12 }
    $lblRights.Text = if ($script:canWrite) { "Skrivetilgang: $($script:myWriteRoles -join ', '). Kryss av i «Valgt» og velg handling." }
                      else { 'Kun lesing: du har ingen rolle som kan endre apper (Application Administrator, Cloud Application Administrator eller Global Administrator).' }
}

function Show-View([string] $View) {
    $script:view = $View
    $lblView.Text = $titles[$View]
    $startPanel.Visible = $false
    $resultPanel.Visible = $true
    Update-Rights
    if ($null -eq $script:appRows) { Start-Fetch } else { Show-Rows }
}

function Get-ViewRows {
    if ($script:view -eq 'sharepoint') {
        @(& (Join-Path $PSScriptRoot 'Get-SharePointAppAccess.ps1') -TenantId $TenantId -AppPermissions $script:appRows -PassThru -NoCsv 6>$null)
    }
    else { @($script:appRows) }
}

function Show-Rows {
    $script:viewRows = @(Get-ViewRows)
    $table = New-Object System.Data.DataTable
    foreach ($name in $columns[$script:view]) {
        $type = switch ($name) { 'Valgt' { [bool] } 'DagerSiden' { [int] } default { [string] } }
        [void]$table.Columns.Add($name, $type)
    }
    [void]$table.Columns.Add('Indeks', [int])
    for ($i = 0; $i -lt $script:viewRows.Count; $i++) {
        $source = $script:viewRows[$i]
        $row = $table.NewRow()
        foreach ($name in $columns[$script:view]) {
            $row[$name] = switch ($name) {
                'Valgt' { $false }
                'Anbefaling' { Get-Recommendation $source }
                'Rettighetstype' { if ($source.Type -eq 'Application') { 'Applikasjon (uten bruker)' } else { 'Delegert (på vegne av bruker)' } }
                'DagerSiden' { if ("$($source.DagerSiden)" -ne '') { [int]$source.DagerSiden } else { [DBNull]::Value } }
                default { if ($null -ne $source.$name) { [string]$source.$name } else { '' } }
            }
        }
        $row['Indeks'] = $i
        [void]$table.Rows.Add($row)
    }
    $script:table = $table
    $grid.DataSource = $table.DefaultView
    Set-Filter
}

function Set-Filter {
    if (-not $script:table) { return }
    $riskColumn = if ($script:view -eq 'sharepoint') { 'Alvor' } else { 'Risiko' }
    $parts = @()
    switch ($cmbFilter.SelectedIndex) {
        1 { $parts += "Anbefaling <> ''" }
        2 { $parts += "$riskColumn = 'Høy'" }
        3 { $parts += "(DagerSiden > 90 OR SistePålogging = 'Ingen registrert')" }
        4 { $parts += "Aktiv = 'Nei'" }
        5 { $parts += "Rettighetstype LIKE 'Applikasjon%'" }
        6 { $parts += "Rettighetstype LIKE 'Delegert%'" }
    }
    $text = $txtSearch.Text.Trim() -replace "'", "''" -replace '([\[\]%*])', '[$1]'
    if ($text) {
        $parts += "(App LIKE '%$text%' OR Rettighet LIKE '%$text%' OR Ressurs LIKE '%$text%' OR Anbefaling LIKE '%$text%' OR AppId LIKE '%$text%')"
    }
    $script:table.DefaultView.RowFilter = $parts -join ' AND '
    Update-Status
}

function Update-Status {
    if (-not $script:table) { return }
    $selected = @($script:table.Rows | Where-Object { $_['Valgt'] -eq $true }).Count
    $apps = @($script:viewRows | Select-Object -ExpandProperty AppId -Unique).Count
    $lblStatus.Text = "{0} av {1} rader vist ({2} apper)   |   {3} valgt" -f $script:table.DefaultView.Count, $script:table.Rows.Count, $apps, $selected
    if ($script:fetchNote) { $lblStatus.Text += "   |   Merk: $($script:fetchNote)" }
    $lblStatus.ForeColor = if ($script:fetchNote) { [System.Drawing.Color]::DarkGoldenrod } else { [System.Drawing.SystemColors]::ControlText }
    $any = $selected -gt 0
    $btnDisable.Enabled = $any
    $btnDelete.Enabled = $any
    $btnRemovePermission.Enabled = $any
}

# Henting i bakgrunnen, så vinduet ikke fryser (tar 1–2 minutter i en stor tenant)
$fetchTimer = New-Object System.Windows.Forms.Timer
$fetchTimer.Interval = 500
$script:fetch = $null

function Start-Fetch {
    if ($script:fetch) { return }
    $ps = [powershell]::Create()
    [void]$ps.AddScript({
        param($ScriptDir, $Tenant)
        $csv = Join-Path ([System.IO.Path]::GetTempPath()) "tilgangssjekk-$([guid]::NewGuid().ToString('N')).csv"
        try { & (Join-Path $ScriptDir 'Get-AppPermissions.ps1') -TenantId $Tenant -CsvPath $csv -PassThru 6>$null }
        finally { Remove-Item $csv -ErrorAction SilentlyContinue }
    }).AddArgument($PSScriptRoot).AddArgument($TenantId)
    $script:fetch = @{ PowerShell = $ps; Handle = $ps.BeginInvoke() }
    $grid.DataSource = $null
    $script:table = $null
    $lblStatus.Text = 'Henter app-registreringer, enterprise apps, samtykker og pålogginger... (kan ta et par minutter)'
    $progress.Visible = $true
    $btnRefresh.Enabled = $false
    $btnBack.Enabled = $false
    $fetchTimer.Start()
}

$fetchTimer.Add_Tick({
    if (-not $script:fetch.Handle.IsCompleted) { return }
    $fetchTimer.Stop()
    $ps = $script:fetch.PowerShell
    try {
        $script:appRows = @($ps.EndInvoke($script:fetch.Handle) | Where-Object { $_ -is [pscustomobject] })
        # Merknader (f.eks. tom påloggingsrapport) vises i statuslinjen, ikke som dialog
        $script:fetchNote = (@($ps.Streams.Warning | ForEach-Object Message) -join ' ')
        Show-Rows
    }
    catch {
        $lblStatus.Text = 'Henting feilet.'
        $message = if ($_.Exception.InnerException) { $_.Exception.InnerException.Message } else { $_.Exception.Message }
        [System.Windows.Forms.MessageBox]::Show("Klarte ikke å hente rettighetene:`n`n$message", 'Feil', 'OK', 'Error') | Out-Null
    }
    finally {
        $ps.Dispose()
        $script:fetch = $null
        $progress.Visible = $false
        $btnRefresh.Enabled = $true
        $btnBack.Enabled = $true
    }
})

function Get-SelectedRows {
    @($script:table.Rows | Where-Object { $_['Valgt'] -eq $true } | ForEach-Object { $script:viewRows[[int]$_['Indeks']] })
}

function Format-Bullets([string[]] $Lines) {
    $text = ($Lines | Select-Object -First 15 | ForEach-Object { " • $_" }) -join "`n"
    if ($Lines.Count -gt 15) { $text += "`n ... og $($Lines.Count - 15) til" }
    $text
}

# Oppdaterer de hentede radene etter en handling, uten å hente alt på nytt
function Update-AppRows([string] $AppId, [scriptblock] $Change) {
    foreach ($r in @($script:appRows | Where-Object AppId -eq $AppId)) { & $Change $r }
}

function Invoke-AppAction([ValidateSet('Deaktiver app', 'Slett app')] [string] $Action) {
    $grid.EndEdit() | Out-Null
    $apps = @(Get-SelectedRows | Group-Object AppId | ForEach-Object { $_.Group[0] })
    $skipped = @()
    $targets = foreach ($app in $apps) {
        if ($app.Apptype -eq 'Microsoft') { $skipped += "$($app.App): Microsoft-app"; continue }
        if ($Action -eq 'Deaktiver app') {
            if (-not $app.EnterpriseAppId) { $skipped += "$($app.App): ingen enterprise app å deaktivere"; continue }
            if ($app.Aktiv -eq 'Nei') { $skipped += "$($app.App): allerede deaktivert"; continue }
        }
        elseif (-not $app.EnterpriseAppId -and -not $app.RegistreringId) { $skipped += "$($app.App): finnes ikke i tenanten"; continue }
        $app
    }
    $targets = @($targets)
    $skipText = if ($skipped) { "`n`nHoppes over:`n$(Format-Bullets $skipped)" } else { '' }
    if (-not $targets) {
        [System.Windows.Forms.MessageBox]::Show("Ingen av de valgte appene kan behandles.$skipText", $Action) | Out-Null
        return
    }

    $names = @($targets | ForEach-Object { "$($_.App)$(if ($_.RegistreringId) { ' (registrering + enterprise app)' })" })
    $explain = if ($Action -eq 'Deaktiver app') {
        'Appene kan ikke logge inn eller brukes før de slås på igjen (Entra → Enterprise applications → Properties → Enabled for users to sign-in).'
    } else {
        'Apper registrert i tenanten slettes med registreringen. Alle kan gjenopprettes fra «Deleted applications» i Entra i 30 dager.'
    }
    $answer = [System.Windows.Forms.MessageBox]::Show(
        "$($Action.ToUpper()): $($targets.Count) app(er)`n`n$(Format-Bullets $names)`n`n$explain$skipText`n`nFortsette?",
        "Bekreft: $Action", 'YesNo', 'Warning')
    if ($answer -ne 'Yes') { return }

    $form.Cursor = 'WaitCursor'
    $log = foreach ($app in $targets) {
        try {
            if ($Action -eq 'Deaktiver app') {
                Disable-App $app
                Update-AppRows $app.AppId { param($r) $r.Aktiv = 'Nei' }
                New-LogEntry $Action $app 'Deaktivert'
            }
            else {
                Remove-App $app
                $script:appRows = @($script:appRows | Where-Object AppId -ne $app.AppId)
                New-LogEntry $Action $app 'Slettet'
            }
        }
        catch { New-LogEntry $Action $app 'Feilet' (Get-ErrorText $_) }
    }
    $form.Cursor = 'Default'
    Show-ActionResult $Action @($log)
}

function Invoke-RemovePermissions {
    $grid.EndEdit() | Out-Null
    $rows = @(Get-SelectedRows)
    $targets = @($rows | Where-Object { $_.Samtykke -and $_.EnterpriseAppId })
    $skipped = @($rows | Where-Object { -not ($_.Samtykke -and $_.EnterpriseAppId) } | ForEach-Object { "$($_.App): $($_.Rettighet) (ikke gitt)" })
    $skipText = if ($skipped) { "`n`nHoppes over:`n$(Format-Bullets $skipped)" } else { '' }
    if (-not $targets) {
        [System.Windows.Forms.MessageBox]::Show("Ingen av de valgte rettighetene er gitt.$skipText", 'Fjern rettighet') | Out-Null
        return
    }
    $names = @($targets | ForEach-Object { "$($_.App): $($_.Rettighet) ($($_.Type), $($_.Ressurs))" })
    $answer = [System.Windows.Forms.MessageBox]::Show(
        "FJERN $($targets.Count) rettighet(er):`n`n$(Format-Bullets $names)`n`nAppene beholdes. Står rettigheten fortsatt i app-registreringen, kan en administrator gi den på nytt.$skipText`n`nFortsette?",
        'Bekreft: Fjern rettighet', 'YesNo', 'Warning')
    if ($answer -ne 'Yes') { return }

    $form.Cursor = 'WaitCursor'
    $log = foreach ($row in $targets) {
        try {
            Remove-Permission $row
            # Raden blir «Mangler samtykke» hvis den står i registreringen, ellers forsvinner den
            $match = $script:appRows | Where-Object { $_.AppId -eq $row.AppId -and $_.Type -eq $row.Type -and $_.RessursId -eq $row.RessursId -and $_.Rettighet -eq $row.Rettighet }
            foreach ($m in @($match)) {
                if ($m.Konfigurert -eq 'Ja') { $m.Samtykke = ''; $m.Status = 'Mangler samtykke' }
                else { $script:appRows = @($script:appRows | Where-Object { $_ -ne $m }) }
            }
            New-LogEntry 'Fjern rettighet' $row 'Fjernet'
        }
        catch { New-LogEntry 'Fjern rettighet' $row 'Feilet' (Get-ErrorText $_) }
    }
    $form.Cursor = 'Default'
    Show-ActionResult 'Fjern rettighet' @($log)
}

function Show-ActionResult([string] $Action, [object[]] $Log) {
    $file = Write-ActionLog $Log
    $ok = @($Log | Where-Object Status -ne 'Feilet').Count
    $failed = @($Log | Where-Object Status -eq 'Feilet')
    $text = "${Action}: $ok fullført, $($failed.Count) feilet."
    if ($failed) { $text += "`n`nFeil:`n$(Format-Bullets @($failed | ForEach-Object { "$($_.App) $($_.Rettighet): $($_.Feil)" }))" }
    $text += "`n`nLogg: $file"
    [System.Windows.Forms.MessageBox]::Show($text, 'Ferdig', 'OK', $(if ($failed) { 'Warning' } else { 'Information' })) | Out-Null
    Show-Rows
}

function Start-InactiveUsers {
    if (-not (Test-Path $InactiveUsersScript)) {
        $dialog = New-Object System.Windows.Forms.OpenFileDialog
        $dialog.Filter = 'Rydd-InaktiveBrukere.ps1|Rydd-InaktiveBrukere.ps1|PowerShell (*.ps1)|*.ps1'
        if ($dialog.ShowDialog() -ne 'OK') { return }
        $script:InactiveUsersScript = $dialog.FileName
        Update-StartPage
    }
    if (-not (Get-Module -ListAvailable -Name Microsoft.Graph.Users)) {
        [System.Windows.Forms.MessageBox]::Show("Rydd-InaktiveBrukere.ps1 trenger modulen Microsoft.Graph.Users. Installer den med:`n`nInstall-Module Microsoft.Graph -Scope CurrentUser",
            'Inaktive brukere', 'OK', 'Information') | Out-Null
        return
    }
    $path = (Resolve-Path $InactiveUsersScript).Path
    Start-Process -FilePath (Get-Process -Id $PID).Path `
        -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$path`"", '-TenantId', $TenantId)
}

# ---------------------------------------------------------------------------
# Delte postbokser (skriptene ligger i samme mappe som Rydd-InaktiveBrukere.ps1)
# ---------------------------------------------------------------------------
function Get-AdminScript([string] $Name) {
    Join-Path (Split-Path $InactiveUsersScript) $Name
}

# Sjekker at ExchangeOnlineManagement er installert, og tilbyr å installere den
function Test-ExchangeModule {
    if (Get-Module -ListAvailable -Name ExchangeOnlineManagement) { return $true }
    $answer = [System.Windows.Forms.MessageBox]::Show(
        "Postboksskriptene trenger modulen ExchangeOnlineManagement, og den er ikke installert.`n`n" +
        "Installer den med:`n`n    Install-Module ExchangeOnlineManagement -Scope CurrentUser`n`n" +
        "Vil du installere den nå? Det åpnes et PowerShell-vindu. Svar «Y» hvis du blir spurt om å stole på PSGallery, " +
        "og klikk postboksknappen igjen når installasjonen er ferdig.",
        'Exchange Online-modulen mangler', 'YesNo', 'Warning')
    if ($answer -eq 'Yes') {
        $command = "Install-Module ExchangeOnlineManagement -Scope CurrentUser; Write-Host ''; Write-Host 'Ferdig. Lukk dette vinduet og klikk postboksknappen igjen i Tilgangssjekk.'"
        Start-Process -FilePath (Get-Process -Id $PID).Path -ArgumentList "-NoProfile -NoExit -Command `"$command`""
    }
    $false
}

function Format-Arg([string] $Value) { '"' + ($Value -replace '"', '') + '"' }

# Starter et skript i eget PowerShell-vindu som blir stående åpent, så resultatet kan leses
function Start-AdminScript([string] $Name, [string[]] $Arguments) {
    $path = Get-AdminScript $Name
    if (-not (Test-Path $path)) {
        [System.Windows.Forms.MessageBox]::Show("Fant ikke $path.`n`nSkriptet ligger i antire-entra-admin-scripts, i samme mappe som Rydd-InaktiveBrukere.ps1.",
            'Fant ikke skriptet', 'OK', 'Error') | Out-Null
        return
    }
    New-Item -ItemType Directory -Force -Path $LogFolder | Out-Null
    $all = @('-NoProfile', '-NoExit', '-ExecutionPolicy', 'Bypass', '-File', (Format-Arg (Resolve-Path $path).Path)) + $Arguments
    # Arbeidsmappen er Dokumenter\Antire tilgangssjekk, så oversikten (CSV) havner der
    Start-Process -FilePath (Get-Process -Id $PID).Path -ArgumentList ($all -join ' ') -WorkingDirectory $LogFolder
}

function Start-MailboxOverview {
    if (-not (Test-ExchangeModule)) { return }
    Start-AdminScript 'Hent-Postbokstilgang.ps1' @('-TenantId', $TenantId)
}

function Show-MailboxChangeDialog {
    if (-not (Test-ExchangeModule)) { return }

    $dialog = New-Object System.Windows.Forms.Form
    $dialog.Text = 'Delte postbokser – gi eller fjern tilgang'
    $dialog.Size = New-Object System.Drawing.Size(640, 520)
    $dialog.StartPosition = 'CenterParent'
    $dialog.FormBorderStyle = 'FixedDialog'
    $dialog.MaximizeBox = $false
    $dialog.MinimizeBox = $false
    $dialog.Font = $form.Font

    $y = 18
    function Add-Field([string] $Label, [System.Windows.Forms.Control] $Control, [int] $Width = 380) {
        $l = New-Control Label 18 ($script:fieldY + 3) -Text $Label
        $Control.Location = New-Object System.Drawing.Point(190, $script:fieldY)
        $Control.Width = $Width
        $dialog.Controls.AddRange(@($l, $Control))
        $script:fieldY += 34
    }
    $script:fieldY = $y

    $txtMailbox = New-Object System.Windows.Forms.TextBox
    $txtUser = New-Object System.Windows.Forms.TextBox
    $cmbAccess = New-Object System.Windows.Forms.ComboBox
    $cmbAccess.DropDownStyle = 'DropDownList'
    [void]$cmbAccess.Items.AddRange(@('FullAccess', 'SendAs', 'SendOnBehalf', 'FullAccessAndSendAs'))
    $cmbAccess.SelectedIndex = 0
    $cmbAction = New-Object System.Windows.Forms.ComboBox
    $cmbAction.DropDownStyle = 'DropDownList'
    [void]$cmbAction.Items.AddRange(@('Gi tilgang', 'Fjern tilgang'))
    $cmbAction.SelectedIndex = 0
    $txtApproved = New-Object System.Windows.Forms.TextBox
    $txtRequested = New-Object System.Windows.Forms.TextBox
    $txtTicket = New-Object System.Windows.Forms.TextBox

    Add-Field 'Delt postboks' $txtMailbox
    Add-Field 'Bruker(e), komma mellom' $txtUser
    Add-Field 'Tilgang' $cmbAccess 200
    Add-Field 'Handling' $cmbAction 200
    Add-Field 'Godkjent av (eier)' $txtApproved
    Add-Field 'Bestilt av (valgfritt)' $txtRequested
    Add-Field 'Saksnummer (valgfritt)' $txtTicket 200

    $lblOr = New-Control Label 18 ($script:fieldY + 8) -Text 'Eller: fjern det eieren har merket Beholde = Nei i oversikten (CSV)'
    $lblOr.Font = New-Object System.Drawing.Font('Segoe UI', 9, [System.Drawing.FontStyle]::Bold)
    $script:fieldY += 34
    $txtReview = New-Object System.Windows.Forms.TextBox
    Add-Field 'Gjennomgangsfil' $txtReview 300
    $btnBrowse = New-Control Button 496 ($script:fieldY - 35) 74 26 'Bla...'
    $btnBrowse.Add_Click({
        $open = New-Object System.Windows.Forms.OpenFileDialog
        $open.Filter = 'CSV (*.csv)|*.csv'
        $open.InitialDirectory = $LogFolder
        if ($open.ShowDialog() -eq 'OK') { $txtReview.Text = $open.FileName }
    })

    $lblHelp = New-Control Label 18 ($script:fieldY + 4) 590 40 'Skriptet kjører i eget vindu mot valgt tenant. «Utfør» ber om bekreftelse før hver endring, og alt logges. Bare delte postbokser kan endres.'
    $lblHelp.AutoSize = $false
    $lblHelp.ForeColor = [System.Drawing.Color]::DimGray

    $btnWhatIf = New-Control Button 190 ($script:fieldY + 54) 170 34 'Vis hva som skjer'
    $btnRun = New-Control Button 370 ($script:fieldY + 54) 110 34 'Utfør'
    $btnRun.ForeColor = [System.Drawing.Color]::DarkRed
    $btnCancel = New-Control Button 490 ($script:fieldY + 54) 80 34 'Lukk'
    $btnCancel.DialogResult = 'Cancel'
    $dialog.CancelButton = $btnCancel
    $dialog.Controls.AddRange(@($lblOr, $btnBrowse, $lblHelp, $btnWhatIf, $btnRun, $btnCancel))

    # Bygger argumentene til Endre-Postbokstilgang.ps1, eller viser hva som mangler
    $buildArgs = {
        param([bool] $WhatIf)
        $a = @('-TenantId', $TenantId)
        if ($txtReview.Text.Trim()) {
            if (-not (Test-Path $txtReview.Text.Trim())) { throw "Fant ikke filen $($txtReview.Text.Trim())." }
            $a += @('-FromReviewCsv', (Format-Arg $txtReview.Text.Trim()))
        }
        else {
            $missing = @()
            if (-not $txtMailbox.Text.Trim()) { $missing += 'Delt postboks' }
            if (-not $txtUser.Text.Trim()) { $missing += 'Bruker' }
            if (-not $txtApproved.Text.Trim()) { $missing += 'Godkjent av' }
            if ($missing) { throw "Fyll ut: $($missing -join ', '). Eller velg en gjennomgangsfil." }
            $a += @('-Mailbox', (Format-Arg $txtMailbox.Text.Trim()), '-User', (Format-Arg $txtUser.Text.Trim()),
                '-Access', $cmbAccess.SelectedItem, '-Action', $(if ($cmbAction.SelectedIndex -eq 1) { 'Remove' } else { 'Add' }),
                '-ApprovedBy', (Format-Arg $txtApproved.Text.Trim()))
            if ($txtRequested.Text.Trim()) { $a += @('-RequestedBy', (Format-Arg $txtRequested.Text.Trim())) }
        }
        if ($txtTicket.Text.Trim()) { $a += @('-Ticket', (Format-Arg $txtTicket.Text.Trim())) }
        if ($WhatIf) { $a += '-WhatIf' }
        , $a
    }
    $btnWhatIf.Add_Click({
        try { Start-AdminScript 'Endre-Postbokstilgang.ps1' (& $buildArgs $true) }
        catch { [System.Windows.Forms.MessageBox]::Show($_.Exception.Message, 'Mangler noe', 'OK', 'Information') | Out-Null }
    })
    $btnRun.Add_Click({
        try { Start-AdminScript 'Endre-Postbokstilgang.ps1' (& $buildArgs $false); $dialog.Close() }
        catch { [System.Windows.Forms.MessageBox]::Show($_.Exception.Message, 'Mangler noe', 'OK', 'Information') | Out-Null }
    })

    [void]$dialog.ShowDialog($form)
    $dialog.Dispose()
}

# ---------------------------------------------------------------------------
# Hendelser
# ---------------------------------------------------------------------------
$grid.Add_DataBindingComplete({
    foreach ($col in $grid.Columns) { $col.ReadOnly = $col.Name -ne 'Valgt' }
    if ($grid.Columns['Indeks']) { $grid.Columns['Indeks'].Visible = $false }
    if ($grid.Columns['Anbefaling']) { $grid.Columns['Anbefaling'].AutoSizeMode = 'None'; $grid.Columns['Anbefaling'].Width = 380 }
    if ($grid.Columns['App']) { $grid.Columns['App'].AutoSizeMode = 'None'; $grid.Columns['App'].Width = 260 }
    if ($grid.Columns['DagerSiden']) { $grid.Columns['DagerSiden'].HeaderText = 'Dager siden' }
    if ($grid.Columns['SistePålogging']) { $grid.Columns['SistePålogging'].HeaderText = 'Siste pålogging' }
    if ($grid.Columns['SisteMedBruker']) { $grid.Columns['SisteMedBruker'].HeaderText = 'Siste med bruker' }
    if ($grid.Columns['SisteSomApp']) { $grid.Columns['SisteSomApp'].HeaderText = 'Siste som app' }
})
$grid.Add_CurrentCellDirtyStateChanged({ if ($grid.IsCurrentCellDirty) { [void]$grid.CommitEdit('CurrentCellChange') } })
$grid.Add_CellValueChanged({ param($s, $e) if ($e.RowIndex -ge 0 -and $grid.Columns[$e.ColumnIndex].Name -eq 'Valgt') { Update-Status } })
$grid.Add_CellFormatting({
    param($s, $e)
    if ($e.RowIndex -lt 0) { return }
    $view = $grid.Rows[$e.RowIndex].DataBoundItem
    if (-not $view) { return }
    $risk = if ($script:view -eq 'sharepoint') { $view.Row['Alvor'] } else { $view.Row['Risiko'] }
    if ($view.Row['Valgt'] -eq $true) { $e.CellStyle.BackColor = [System.Drawing.Color]::FromArgb(250, 219, 216) }
    elseif ($risk -eq 'Høy') { $e.CellStyle.BackColor = [System.Drawing.Color]::FromArgb(253, 236, 234) }
    elseif ($risk -eq 'Middels') { $e.CellStyle.BackColor = [System.Drawing.Color]::FromArgb(255, 248, 225) }
    if ($view.Row['Aktiv'] -eq 'Nei') { $e.CellStyle.ForeColor = [System.Drawing.Color]::Gray }
})

$cmbFilter.Add_SelectedIndexChanged({ Set-Filter })
$txtSearch.Add_TextChanged({ Set-Filter })
$btnSelectVisible.Add_Click({
    if (-not $script:table) { return }
    foreach ($v in $script:table.DefaultView) { $v.Row['Valgt'] = $true }
    $grid.Invalidate(); Update-Status
})
$btnClearSelection.Add_Click({
    if (-not $script:table) { return }
    foreach ($r in $script:table.Rows) { $r['Valgt'] = $false }
    $grid.Invalidate(); Update-Status
})

$btnBack.Add_Click({ $resultPanel.Visible = $false; $startPanel.Visible = $true })
$btnRefresh.Add_Click({ $script:appRows = $null; Start-Fetch })
$btnExport.Add_Click({
    if (-not $script:table) { return }
    $dialog = New-Object System.Windows.Forms.SaveFileDialog
    $dialog.Filter = 'CSV (*.csv)|*.csv'
    $prefix = if ($script:view -eq 'sharepoint') { 'sharepoint-tilgang' } else { 'app-rettigheter' }
    $dialog.FileName = "$prefix-$TenantId-$(Get-Date -Format 'yyyyMMdd-HHmm').csv"
    if ($dialog.ShowDialog() -ne 'OK') { return }
    $names = @($columns[$script:view] | Where-Object { $_ -ne 'Valgt' })
    $props = foreach ($n in $names) { @{ Name = $n; Expression = [scriptblock]::Create("`$_.Row['$n']") } }
    @($script:table.DefaultView) | Select-Object -Property $props | Export-Csv -Path $dialog.FileName -Encoding utf8BOM -Delimiter ';'
    $lblStatus.Text = "Eksportert til $($dialog.FileName)"
})

$btnDisable.Add_Click({ Invoke-AppAction 'Deaktiver app' })
$btnDelete.Add_Click({ Invoke-AppAction 'Slett app' })
$btnRemovePermission.Add_Click({ Invoke-RemovePermissions })

$btnApps.Add_Click({ Show-View 'apps' })
$btnSharePoint.Add_Click({ Show-View 'sharepoint' })
$btnInactive.Add_Click({ Start-InactiveUsers })
$btnMailboxOverview.Add_Click({ Start-MailboxOverview })
$btnMailboxChange.Add_Click({ Show-MailboxChangeDialog })

function Invoke-Connect([bool] $AllowLogin) {
    try {
        $form.Cursor = 'WaitCursor'
        $lblUser.Text = if ($AllowLogin) { 'Logger inn – fullfør innloggingen i nettleseren...' } else { 'Kobler til...' }
        $form.Refresh()
        if (-not (Connect-Tenant $AllowLogin)) {
            $lblUser.Text = 'Azure CLI er ikke logget inn i denne tenanten.'
            $btnLogin.Visible = $true
        }
    }
    catch {
        $lblUser.Text = "Klarte ikke å koble til: $(Get-ErrorText $_)"
        $btnLogin.Visible = $true
    }
    finally {
        $form.Cursor = 'Default'
        Update-StartPage
    }
}
$btnLogin.Add_Click({ Invoke-Connect $true })

$lblStartInfo.Text = 'Alt kjører med din egen innlogging (Azure CLI for apper, Exchange Online for postbokser), og ingenting endres uten at du bekrefter det. ' +
    'Deaktiver, Slett og Fjern rettighet vises bare for Global Administrator, Application Administrator og Cloud Application Administrator. ' +
    'Endring av postbokstilgang krever Exchange Administrator eller Recipient Management.'

$form.Add_Shown({ Update-StartPage; Invoke-Connect $false })
$form.Add_FormClosed({ Restore-AzAccount })

[void]$form.ShowDialog()

<#
.SYNOPSIS
  Lager et sertifikat appen logger inn med mot Graph og Azure (2.1), i stedet for
  klienthemmeligheten.

.DESCRIPTION
  - Lager et selvsignert sertifikat (RSA 2048, SHA-256) i Cert:\CurrentUser\My.
    Den private nøkkelen blir på maskinen og eksporteres ikke.
  - Legger den offentlige delen til på app-registreringen (--append: eksisterende
    sertifikater og hemmeligheter beholdes, så ingenting slutter å virke).
  - Med -SetUserSecret settes MicrosoftGraph:CertificateThumbprint i backendens
    user secrets. Backend bruker da sertifikatet foran hemmeligheten (se
    backend/Services/AppCredentials.cs).

  Når sertifikatet virker, kan hemmeligheten fjernes fra user secrets og fra
  app-registreringen (Certificates & secrets).

  Krever Azure CLI innlogget i riktig tenant:
    az login --tenant <tenant-id>

.EXAMPLE
  ./scripts/New-AppCertificate.ps1 -TenantId 3af5c92b-38f9-48ce-8746-82bb4c113032 `
                                   -ApiClientId e33710da-36b0-4e4b-9696-bb4fbdff647f `
                                   -SetUserSecret
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)] [string] $TenantId,
    [Parameter(Mandatory)] [string] $ApiClientId,
    [string] $Subject = 'CN=Antire IT Operations',
    [int] $ValidMonths = 12,
    [switch] $SetUserSecret
)

$ErrorActionPreference = 'Stop'

# Stopp hvis Azure CLI står på en annen tenant enn den som er oppgitt
$currentTenant = az account show --query tenantId -o tsv
if ($currentTenant -ne $TenantId) {
    throw "Azure CLI er logget inn i tenant '$currentTenant', ikke '$TenantId'. Kjør: az login --tenant $TenantId"
}

$appName = az ad app show --id $ApiClientId --query displayName -o tsv
if (-not $appName) {
    throw "Fant ingen app-registrering $ApiClientId i tenanten."
}
Write-Host "App-registrering: $appName"

if (-not $PSCmdlet.ShouldProcess($appName, "Lage sertifikat og legge det til på app-registreringen")) {
    return
}

$certificate = New-SelfSignedCertificate `
    -Subject $Subject `
    -CertStoreLocation 'Cert:\CurrentUser\My' `
    -KeyAlgorithm RSA -KeyLength 2048 -HashAlgorithm SHA256 `
    -KeySpec Signature -KeyExportPolicy NonExportable `
    -NotAfter (Get-Date).AddMonths($ValidMonths)

Write-Host "Sertifikat laget: $($certificate.Thumbprint) (gyldig til $($certificate.NotAfter.ToString('yyyy-MM-dd')))"

# Bare den offentlige delen lastes opp
$publicKeyFile = Join-Path ([System.IO.Path]::GetTempPath()) "$($certificate.Thumbprint).cer"
try {
    Export-Certificate -Cert $certificate -FilePath $publicKeyFile -Type CERT | Out-Null
    az ad app credential reset --id $ApiClientId --cert "@$publicKeyFile" --append --output none
}
finally {
    Remove-Item $publicKeyFile -ErrorAction SilentlyContinue
}
Write-Host "Den offentlige nøkkelen er lagt til på app-registreringen."

if ($SetUserSecret) {
    $backend = Join-Path $PSScriptRoot '..\backend'
    dotnet user-secrets set 'MicrosoftGraph:CertificateThumbprint' $certificate.Thumbprint --project $backend | Out-Null
    Write-Host "MicrosoftGraph:CertificateThumbprint er satt i backendens user secrets."
}
else {
    Write-Host "Sett tommelavtrykket i backend:"
    Write-Host "  dotnet user-secrets set MicrosoftGraph:CertificateThumbprint $($certificate.Thumbprint) --project backend"
}

Write-Host "Restart backend. Når sertifikatet virker, kan klienthemmeligheten fjernes."

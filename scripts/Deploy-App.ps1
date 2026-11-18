<#
.SYNOPSIS
  Bygger og ruller ut Antire IT Operations til App Service, og kjører
  databasemigreringene (UTVIDELSESPLAN.md 8b).

.DESCRIPTION
  1. Leser adresser og navn fra Terraform (infra/terraform, terraform output).
  2. Bygger frontend (npm run build) og backend (dotnet publish), og legger
     frontend i backendens wwwroot.
  3. Kjører migreringene med en migreringsbunt (dotnet ef migrations bundle) som
     innlogget Entra-bruker (token som passord), og gir appens identitet
     rettigheter til tabellene.
  4. Ruller ut zip-pakken til App Service og venter på /health.

  Krever Azure CLI innlogget i riktig tenant, at du er Entra-administrator på
  databasen (Terraform setter det opp), at IP-adressen din er åpnet i brannmuren
  (deployer_ip_addresses), og psql i PATH eller i standardmappen.

.EXAMPLE
  ./scripts/Deploy-App.ps1 -TenantId 3af5c92b-38f9-48ce-8746-82bb4c113032
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)] [string] $TenantId,
    [string] $TerraformDir = (Join-Path $PSScriptRoot '..\infra\terraform'),
    [switch] $SkipMigrations
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')

# Stopp hvis Azure CLI står på en annen tenant enn den som er oppgitt
$currentTenant = az account show --query tenantId -o tsv
if ($currentTenant -ne $TenantId) {
    throw "Azure CLI er logget inn i tenant '$currentTenant', ikke '$TenantId'. Kjør: az login --tenant $TenantId"
}

# Verdier fra Terraform
$tf = terraform "-chdir=$TerraformDir" output -json | ConvertFrom-Json
$appName = $tf.app_name.value
$resourceGroup = $tf.resource_group.value
$appUrl = $tf.app_url.value
$pgHost = $tf.postgres_fqdn.value
$database = $tf.postgres_database.value
$appIdentity = $tf.app_identity_name.value
$deployer = $tf.deployer_db_user.value
Write-Host "App: $appName ($appUrl)"

$work = Join-Path ([System.IO.Path]::GetTempPath()) "itops-deploy-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
$publish = Join-Path $work 'publish'
New-Item -ItemType Directory -Force $publish | Out-Null

try {
    # ---------- Bygg ----------
    Write-Host 'Bygger frontend...'
    Push-Location (Join-Path $root 'frontend')
    try {
        # npm install, ikke npm ci: ci sletter node_modules, og det feiler når
        # en lokal Vite-server holder filer åpne
        npm install --no-audit --no-fund | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'npm install feilet.' }
        npm run build | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Frontend-bygget feilet.' }
    }
    finally { Pop-Location }

    Write-Host 'Bygger backend...'
    dotnet publish (Join-Path $root 'backend\backend.csproj') -c Release -o $publish --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw 'Backend-bygget feilet.' }

    # Frontend serveres av backend fra wwwroot (AzureHosting.UseFrontend)
    $wwwroot = Join-Path $publish 'wwwroot'
    New-Item -ItemType Directory -Force $wwwroot | Out-Null
    Copy-Item (Join-Path $root 'frontend\dist\*') $wwwroot -Recurse -Force

    # ---------- Migreringer ----------
    if (-not $SkipMigrations) {
        Write-Host 'Lager migreringsbunt...'
        $bundle = Join-Path $work 'efbundle.exe'
        # Release: Debug-mappen kan være låst av en lokal backend som kjører
        dotnet ef migrations bundle --project (Join-Path $root 'backend') --configuration Release -o $bundle --force | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Migreringsbunten feilet.' }

        # Entra-token som passord (varer ca. en time)
        $token = az account get-access-token --resource-type oss-rdbms --query accessToken -o tsv
        $connection = "Host=$pgHost;Database=$database;Username=$deployer;Password=$token;SSL Mode=Require"

        if ($PSCmdlet.ShouldProcess($database, 'Kjøre migreringer')) {
            Write-Host 'Kjører migreringer...'
            & $bundle --connection $connection
            if ($LASTEXITCODE -ne 0) { throw 'Migreringene feilet.' }

            # Tabellene eies av den som kjørte migreringene; appens identitet får
            # rettigheter til dem og til det som lages senere
            $psql = (Get-Command psql -ErrorAction SilentlyContinue)?.Source ??
                (Get-ChildItem 'C:\Program Files\PostgreSQL\*\bin\psql.exe' | Sort-Object FullName -Descending | Select-Object -First 1).FullName
            if (-not $psql) { throw 'Fant ikke psql.' }

            $grants = @"
GRANT USAGE ON SCHEMA public TO "$appIdentity";
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO "$appIdentity";
GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA public TO "$appIdentity";
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO "$appIdentity";
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO "$appIdentity";
"@
            $env:PGPASSWORD = $token
            try {
                $grants | & $psql "host=$pgHost dbname=$database user=$deployer sslmode=require" -v ON_ERROR_STOP=1 -q
                if ($LASTEXITCODE -ne 0) { throw 'Rettighetene for appens identitet feilet.' }
            }
            finally { Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue }
            Write-Host "Appens identitet ($appIdentity) har rettigheter til tabellene."
        }
    }

    # ---------- Utrulling ----------
    $zip = Join-Path $work 'app.zip'
    Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $zip -Force

    if ($PSCmdlet.ShouldProcess($appName, 'Rulle ut')) {
        Write-Host 'Ruller ut...'
        # --track-status false: az sin oppstartssporing gir ofte feil før appen er
        # oppe; vi venter på /health selv under
        az webapp deploy --resource-group $resourceGroup --name $appName --src-path $zip --type zip --track-status false --output none
        if ($LASTEXITCODE -ne 0) { throw 'Utrullingen feilet.' }

        Write-Host 'Venter på /health...'
        $healthy = $false
        foreach ($attempt in 1..30) {
            try {
                $response = Invoke-WebRequest "$appUrl/health" -UseBasicParsing -TimeoutSec 20
                if ($response.StatusCode -eq 200) { $healthy = $true; break }
            }
            catch { }
            Start-Sleep -Seconds 10
        }

        if ($healthy) { Write-Host "Ferdig: $appUrl" }
        else { throw "Appen svarer ikke friskt på $appUrl/health. Se loggen: az webapp log tail -g $resourceGroup -n $appName" }
    }
}
finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}

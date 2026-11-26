# Antire IT Operations

Intern IT-driftsplattform: helpdesk med SLA, tilgangsbestillinger, IT-utstyr, onboarding og offboarding, GDPR, og oversikt over Microsoft 365 og Azure. Varsler fra Azure Monitor og Microsoft Defender blir saker automatisk, og IT kan varsles i Teams.

Appen er laget for én organisasjon med egne Entra-tenanter (f.eks. test og prod). Alle data er adskilt per tenant: brukeren ser bare data fra tenanten de er logget inn i.

## Innhold

- [Funksjoner](#funksjoner)
- [Arkitektur](#arkitektur)
- [Kom i gang](#kom-i-gang)
- [Entra ID og Azure](#entra-id-og-azure)
- [Roller](#roller)
- [Konfigurasjon](#konfigurasjon)
- [Tester](#tester)
- [Databasemigreringer](#databasemigreringer)
- [Prosjektstruktur](#prosjektstruktur)
- [Dokumentasjon](#dokumentasjon)

## Funksjoner

| Område | Hva |
|---|---|
| **Helpdesk** | Saker meldt inn fra tjenestekatalogen, status, prioritet og ansvarlig, automatisk tildeling ut fra ansvarsområder og Entra-grupper, og historikk per sak (hvem endret hva og når) |
| **SLA** | Frister for respons og løsning per prioritet i arbeidstid (08–16, norske helligdager) eller døgnet rundt, merkene «I fare» og «Brudd», og bakgrunnsjobb som logger brudd |
| **Tilgangsbestillinger** | Bestilling fra tjenestekatalogen, godkjenning, og automatisk innmelding i Entra-gruppen når bestillingen godkjennes |
| **IT-utstyr** | Registrering, tildeling og innlevering, saker per utstyr, og CSV-import (Excel-vennlig) |
| **Onboarding / Offboarding** | Sjekklister fra maler som starter automatisk ved startdato og sluttdato. Offboarding viser utstyr som skal leveres inn, og fjerner tilganger fra Entra-grupper |
| **GDPR** | Innsyn (alt om én person som fil), anonymisering av ansatte som har sluttet (manuelt eller etter frist), og behandlingsprotokoll |
| **Varsler** | Azure Monitor og Microsoft Defender blir saker med prioritet fra alvorlighetsgraden, deduplisering og gjenåpning, regler per kilde, og varsling i Teams |
| **Microsoft 365** | Brukere (kontostatus, lisenser, siste pålogging og MFA når lisens finnes), inaktive gjester eller medlemmer (ingen vellykket pålogging siden en valgt dato, med CSV-eksport) og grupper med medlemmer |
| **Azure** | Abonnementer og ressurser på tvers av abonnementene (Resource Graph) |
| **Revisjonslogg** | Alle endringer logges automatisk med hvem og når, og kan ikke endres eller slettes |
| **Innstillinger** | Tjenestekatalog, ansvarsområder, kategorigrupper, SLA, varselregler, Teams, onboarding-maler, pålogginger (hvem som har brukt appen og når) og revisjonslogg |

## Arkitektur

```
Nettleser (React + MSAL)
   │  Entra ID-token (api://…/access_as_user)
   ▼
Backend (ASP.NET Core, .NET 10)
   ├── API-kontrollere           rollebasert tilgang, tenant per innlogging
   ├── Bakgrunnsjobber           SLA-brudd, varsler og Teams-kø, GDPR-anonymisering
   ├── Microsoft Graph           brukere, grupper, Defender
   ├── Azure Resource Manager    abonnementer, Resource Graph, Azure Monitor-varsler
   └── PostgreSQL (EF Core)      tenant-filter på alle tabeller, revisjonslogg
```

| Del | Teknologi |
|---|---|
| Backend | ASP.NET Core 10, Entity Framework Core 10 med Npgsql, Microsoft.Identity.Web, Microsoft Graph SDK, Azure.Identity |
| Frontend | React 19, TypeScript, Vite, MSAL (`@azure/msal-react`) |
| Database | PostgreSQL (lokalt 18; Azure Database for PostgreSQL Flexible Server i Azure) |
| Tester | xUnit, WebApplicationFactory, SQLite i minnet og ekte PostgreSQL |

## Kom i gang

### Forutsetninger

- [.NET SDK 10](https://dotnet.microsoft.com/download)
- [Node.js](https://nodejs.org/) 22 eller nyere
- PostgreSQL 16 eller nyere, lokalt
- [Azure CLI](https://learn.microsoft.com/cli/azure/install-azure-cli) (for Entra-oppsett og skriptene)
- EF Core-verktøyet: `dotnet tool install --global dotnet-ef`
- Tilgang til en Entra-tenant med app-registreringene (se [Entra ID og Azure](#entra-id-og-azure))

### 1. Database

Lag en databasebruker og en database (som PostgreSQL-administrator, én gang):

```sql
CREATE ROLE antire_app LOGIN PASSWORD '<velg et passord>';
CREATE DATABASE antire_it_operations OWNER antire_app;
```

### 2. Hemmeligheter for backend

Hemmeligheter ligger i [user secrets](https://learn.microsoft.com/aspnet/core/security/app-secrets), aldri i koden:

```powershell
cd backend
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Host=127.0.0.1;Port=5432;Database=antire_it_operations;Username=antire_app;Password=<passord>"
dotnet user-secrets set "MicrosoftGraph:TenantId" "<tenant-id>"
dotnet user-secrets set "MicrosoftGraph:ClientId" "<API-appens client ID>"
```

Appen logger inn mot Graph og Azure med et sertifikat. Lag det og registrer det på app-registreringen med skriptet (det setter også tommelavtrykket i user secrets):

```powershell
./scripts/New-AppCertificate.ps1 -TenantId <tenant-id> -ApiClientId <API-appens client ID> -SetUserSecret
```

### 3. Migreringer

```powershell
cd backend
dotnet ef database update
```

### 4. Kjør backend

```powershell
cd backend
dotnet run
```

API-et svarer på `http://localhost:5086`. Helsesjekk: `http://localhost:5086/health`.

### 5. Kjør frontend

```powershell
cd frontend
npm install
npm run dev
```

Åpne `http://localhost:5173` og logg inn med Microsoft. Adressen til API-et og innloggingen kan overstyres i `frontend/.env.local` (se `frontend/.env.example`).

## Entra ID og Azure

Løsningen bruker to app-registreringer i hver tenant:

| App-registrering | Brukes til |
|---|---|
| **API** (`AzureAd:ClientId`) | Validerer tokenet fra frontend, har app-rollene, og er appens egen identitet mot Graph og Azure |
| **SPA** (`clientId` i `frontend/src/msalConfig.ts`) | Innlogging i nettleseren; ber om `api://<API>/access_as_user` |

**Skript** (krever Azure CLI innlogget i riktig tenant; alle stopper hvis tenanten ikke stemmer):

| Skript | Hva |
|---|---|
| `scripts/Set-AppRoles.ps1` | Legger app-rollene på API-registreringen |
| `scripts/New-AppCertificate.ps1` | Sertifikat for appens innlogging mot Graph og Azure (i stedet for klienthemmelighet) |
| `scripts/Grant-AzureReader.ps1` | Gir appen rollen Reader på Azure-abonnementer |
| `scripts/Get-AppPermissions.ps1` | Revisjon: alle rettigheter på alle app-registreringer og enterprise apps i tenanten (delegert/applikasjon, samtykke, risiko), med CSV-eksport. Endrer ingenting |
| `scripts/Show-AccessReview.ps1` | Vindu (PowerShell 7) med startside for app-rettigheter, SharePoint-tilgang, inaktive brukere og delte postbokser (oversikt, og gi/fjern tilgang for IT; sier fra og hjelper med å installere ExchangeOnlineManagement hvis den mangler). Anbefaling per rad, filtre og CSV-eksport. Deaktiver app, Slett app og Fjern rettighet vises bare for Global Administrator, Application Administrator og Cloud Application Administrator. Spør ved oppstart om Antire eller AGK (dev), eller bruk `-TenantId` |
| `scripts/Get-SharePointAppAccess.ps1` | Apper med tilgang til SharePoint og OneDrive: hele tenanten eller utvalgte områder, med eller uten bruker, og siste pålogging. CSV-eksport. Endrer ingenting |
| `scripts/Grant-GraphPermissions.ps1` | Gir API-appen Graph-rettighetene under, med admin-samtykke (`-SkipOptional` for bare de påkrevde). `-IncludeUserAdmin` legger også til de delegerte rettighetene `User.EnableDisableAccount.All` og `User.DeleteRestore.All` for å deaktivere og slette brukere under Microsoft 365 → Brukere med den innloggede personens egne rettigheter |

**Graph-rettigheter (applikasjon, med admin-samtykke):**

| Rettighet | Brukes til |
|---|---|
| `User.Read.All` | Synk av ansatte og brukeroversikten |
| `GroupMember.Read.All` | Grupper, medlemmer og automatisk tildeling |
| `GroupMember.ReadWrite.All` | Tilgang via gruppe (legge til og fjerne medlemmer) |
| `Organization.Read.All` *(valgfri)* | Lisensoversikten |
| `AuditLog.Read.All` *(valgfri)* | Siste pålogging og MFA-status (krever Entra ID P1) |
| `SecurityAlert.Read.All` *(valgfri)* | Defender-varsler (krever Defender-lisens) |

Funksjoner som mangler rettighet eller lisens, viser hva som mangler i stedet for å feile.

## Roller

App-rollene tildeles brukere under **Enterprise applications → API-appen → Users and groups**.

| Rolle | Kan |
|---|---|
| *(ingen rolle)* | Melde inn og se egne saker, bestille tilgang, se eget utstyr |
| `Helpdesk.Agent` | Behandle alle saker, fullføre tilgangsbestillinger, IT-utstyr, varsler, onboarding og offboarding |
| `Approver` | Godkjenne og avslå tilgangsbestillinger |
| `Helpdesk.Admin` | Alt over, pluss Innstillinger, revisjonslogg, Microsoft 365, Azure og GDPR |
| `EmployeeSync.Admin` | Registrere og endre ansatte, og synke ansatte fra Entra |

## Konfigurasjon

| Innstilling | Standard | Hva |
|---|---|---|
| `AzureAd:*` | i `appsettings.json` | Tokenvalidering for API-et |
| `Tenants:Allowed` | i `appsettings.json` | Tenantene appen kan brukes fra; andre avvises |
| `Cors:AllowedOrigins` | `http://localhost:5173` (Development) | Frontend-adresser som får kalle API-et |
| `ConnectionStrings:DefaultConnection` | user secrets | PostgreSQL |
| `MicrosoftGraph:ClientId` | user secrets | API-appens client ID (appens egen identitet) |
| `MicrosoftGraph:CertificateThumbprint` | user secrets | Sertifikat i Windows-sertifikatlageret |
| `MicrosoftGraph:UseFederatedManagedIdentity` | `false` | I Azure: managed identity med federated credential, uten hemmeligheter |
| `Database:UseEntraAuth` | `false` | I Azure: PostgreSQL med Entra-token i stedet for passord |
| `DataProtection:BlobUri`, `DataProtection:KeyVaultKeyUri` | av | I Azure: nøkler som krypterer Teams-adressen |
| `Sla:MonitorEnabled`, `Alerts:MonitorEnabled`, `Gdpr:MonitorEnabled` | `true` | Bakgrunnsjobbene (SLA hvert 5. min, varsler hvert 5. min, GDPR daglig) |

## Tester

```powershell
dotnet test
```

Testene kjører API-et i minnet med test-innlogging og falske tjenester for Graph, Azure og Teams, mot SQLite i minnet. De dekker tilgangskontroll, tenant-skille og forretningsreglene.

**Mot ekte PostgreSQL:** sett `ITOPS_TEST_POSTGRES` til en tilkobling med et eget testskjema som slutter på `_test`. Testene sletter og bygger opp skjemaet med de ekte migreringene; appens eget skjema (`public`) røres aldri:

```powershell
$env:ITOPS_TEST_POSTGRES = "Host=127.0.0.1;Database=antire_it_operations;Username=antire_app;Password=<passord>;Search Path=itops_test"
dotnet test
```

Uten variabelen hoppes PostgreSQL-testene over.

**Frontend:**

```powershell
cd frontend
npm run build   # typesjekk og bygg
npm run lint
```

## Databasemigreringer

```powershell
cd backend
dotnet ef migrations add <Navn>     # ny migrering
dotnet ef database update           # kjør mot databasen i user secrets
dotnet ef migrations bundle         # kjørbar migreringsbunt for utrulling
```

Nye tabeller implementerer `ITenantOwned` (tenant-filter og automatisk `TenantId`) og normalt `IAudited` (revisjonslogg).

## Prosjektstruktur

```
backend/
├── Controllers/     API-endepunkter
├── Data/            AppDbContext (tenant-filter) og AuditInterceptor (revisjonslogg)
├── Migrations/      EF Core-migreringer
├── Models/          Entiteter og koder
└── Services/        Forretningslogikk, Graph, Azure, Teams og bakgrunnsjobber
backend.Tests/       Tester (SQLite og PostgreSQL)
frontend/
└── src/
    ├── components/  Sider (Helpdesk, IT-utstyr, Varsler …) og settings/ (Innstillinger)
    └── *.ts         Felles typer, etiketter og API-klient
scripts/             PowerShell-skript for Entra og Azure
```

## Dokumentasjon

| Dokument | Innhold |
|---|---|
| [`UTVIDELSESPLAN.md`](UTVIDELSESPLAN.md) | Planen og status for hvert steg, inkludert drift i Azure (kapittel 8b) |
| [`FASE5_DRIFTSRAPPORT.md`](FASE5_DRIFTSRAPPORT.md) | Ukentlig driftsrapport med AI |
| [`ENDRING_OG_RISIKO.md`](ENDRING_OG_RISIKO.md) | Plan og logg for Change Management og Risk Management (prototype, C1–C9) |
| [`docs/endring-og-risiko/`](docs/endring-og-risiko/README.md) | Dokumentasjon for endring og risiko: arkitektur, datamodell, API, prosesser, RACI, sikkerhet, integrasjoner og forutsetninger for produksjon |
| [`TARGET_ARCHITECTURE.md`](TARGET_ARCHITECTURE.md) | Målarkitektur |
| [`DATABASE_DESIGN.md`](DATABASE_DESIGN.md) | Datamodell |
| [`GRAPH_API_INTEGRATIONS.md`](GRAPH_API_INTEGRATIONS.md) | Microsoft Graph: endepunkter, rettigheter og struping |
| [`AZURE_INTEGRATIONS.md`](AZURE_INTEGRATIONS.md) | Azure-integrasjoner og plattform |
| [`DEVELOPMENT_ROADMAP.md`](DEVELOPMENT_ROADMAP.md) | Veikart |

Arkitekturdokumentene ble skrevet for en tidligere MSP-modell (flere kunder); der de avviker, gjelder `UTVIDELSESPLAN.md`.

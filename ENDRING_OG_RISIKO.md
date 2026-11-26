# Change Management og Risk Management (prototype)

| | |
|---|---|
| Status | Plan v0.1 (2026-11-26). Ingen kode er skrevet. |
| Grunnlag | ITIL 4 (Change Enablement, Risk Management) og IT Governance |
| Bygger på | Revisjonslogg (1.2), tjenestekatalog (1.3), Azure (fase 3), varsler og Teams (fase 4), `EntraUserService`, fase 5 steg 3 (Foundry) |
| Tenant | **Kun utvikling** (`3af5c92b-…`). Se kapittel 10. |

## 1. Avklaringer (2026-11-26)

| | Beslutning |
|---|---|
| A. Teams | Kort sendes gjennom den eksisterende webhook-køen og viser status og varsler, med knapper som åpner riktig skjema i appen. Godkjenning skjer i appen, der serveren sjekker identiteten. En Teams-bot med knapper som virker i selve kortet kommer når dev-tenanten har Teams-lisens, og bruker de samme API-ene. |
| B. Roller | De ti endringsrollene og RACI ligger i databasen og kan konfigureres. En rolle tildeles en ansatt eller en Entra-gruppe, globalt eller per system/tjeneste. App-rollene Helpdesk.Agent og Helpdesk.Admin gir fortsatt grov tilgang til modulene. |
| C. Testbrukere | Syntetiske testbrukere opprettes i dev-tenanten (f.eks. «Test Change Manager»), slik at separasjon av plikter kan prøves med ekte innlogging. Gjøres i C2 etter tenant-sjekk. |
| D. AI | Egen, smal regel for Change and Risk-agenten (CLAUDE.md, kapittel AI). |
| E. Foundry | Foundry-ressursen opprettes i dev-abonnementet med Terraform i C8, felles med fase 5 steg 3. |
| F. Oppbevaring | Gjelder endringer og risikoer (arkivering eller anonymisering etter frist). Revisjonsloggen slettes aldri; personopplysninger vaskes med samme mekanisme som GDPR-modulen. |
| G. Koder | Statuser og typer lagres som engelske koder (`pending_approval`) og vises på norsk og engelsk via i18n, som sakene. |

## 2. Gjenbruk

| Behov | Gjenbruker |
|---|---|
| Revisjonslogg (hvem, når, før og etter) | `AuditEvent` og `AuditInterceptor`. Nye tabeller er `IAudited`. |
| Tenant-skille | `ITenantOwned` og filteret i `AppDbContext` |
| Personer og grupper | `Employee` (Entra-ID) og `GraphService.IsMemberOfAnyGroupAsync` |
| Systemer og tjenester | `ServiceCatalogItem` |
| Azure-abonnementer | `AzureSubscription` (ressursgrupper som tekst) |
| Hendelser | `Ticket` og `Alert` |
| GDPR | `ProcessingActivity` |
| Løpenumre | PostgreSQL-sekvens, som ansattnummer (`EmployeeNumberService`), med teller i SQLite-testene |
| Teams | `TeamsNotificationService` (kø, nye forsøk) og `TeamsCards` |
| Bakgrunnsjobber | Mønsteret fra `SlaMonitorService` (`CreateTenantScope`, aktør `system`) |

## 3. Endringer

**Typer:** `standard` (forhåndsgodkjent mal, lav risiko), `normal` (risikovurdering, testing, godkjenning), `emergency` (hurtiggodkjenning og obligatorisk etterkontroll).

**Statuser:** `draft`, `submitted`, `risk_assessment`, `technical_review`, `testing`, `pending_approval`, `approved`, `scheduled`, `implementation`, `verification`, `completed`, `closed`, og `rejected`, `cancelled`, `failed`, `rolled_back`.

**Arbeidsflyt** (`ChangeWorkflow`, bare på serveren; klienten ber om en overgang, serveren avgjør):

- **Normal:** draft → submitted → risk_assessment → technical_review → testing → pending_approval → approved → scheduled → implementation → verification → completed → closed.
- **Standard:** fra en aktiv mal: draft → submitted → scheduled → implementation → verification → completed → closed. Malen er godkjent på forhånd av Change Manager, og endringen må holde seg innenfor malens miljøer og risikonivå.
- **Emergency:** draft → submitted → pending_approval (hurtiggodkjenning) → approved → implementation → verification → completed → **etterkontroll** → closed. Kan ikke lukkes uten etterkontroll.
- Fra implementation: `failed` eller `rolled_back`. Fra de fleste statuser før implementation: `rejected` eller `cancelled`.

**Godkjenning låst til innholdet:** hver godkjenning lagrer en SHA-256-hash av omfang (systemer, abonnementer, miljø), risiko, implementeringsplan, rollback-plan og eventuelle koblinger til PR-SHA og Terraform-plan. Endrer noe av dette seg etter godkjenning, gjelder ikke godkjenningen lenger: endringen går tilbake til `pending_approval`, og hendelsen logges. Godkjenninger endres eller slettes aldri.

**Ingen produksjonsendring uten godkjenning:** overgang til `implementation` i `production` krever at godkjenningsreglene er oppfylt med gyldige godkjenninger (eller en aktiv standardmal).

## 4. Roller, RACI og separasjon av plikter

**Roller:** Change Requester, Change Manager, Technical Owner, System Owner, Risk Owner, Security Officer, GDPR Responsible, Approver, Implementer, Reviewer.

- En rolle tildeles en ansatt eller en Entra-gruppe, globalt eller for ett system/én tjeneste.
- **RACI** er en konfigurerbar matrise: aktivitet (f.eks. godkjenne produksjonsendring, risikovurdering, implementering, akseptere risiko) × rolle → R, A, C eller I.
- **Godkjenningsregler** per type og miljø: hvilke roller som må godkjenne og hvor mange.
- **Separasjon av plikter (SoD):** den som har opprettet eller skal implementere en produksjonsendring, teller ikke som godkjenner. Den som eier en risiko med høy score, kan ikke alene akseptere den hvis regelen sier noe annet. Alt håndheves på serveren mot innlogget identitet.

## 5. Risiko

- Løpenummer `RISK-000001`. Kategoriene fra kravet (Information Security, Backup and Disaster Recovery, GDPR and Compliance, Identity and Access Management, Financial and FinOps, Operational Risk, Technical Debt, Availability, Governance, Supplier Risk).
- Sannsynlighet og konsekvens 1–5; score = sannsynlighet × konsekvens, beregnet av koden. Restrisiko med egen sannsynlighet og konsekvens.
- Strategier: Mitigate, Accept, Transfer, Avoid.
- **Aksept** krever en autorisert risikoeier, lagres som egen hendelse (hvem, når, begrunnelse, score ved aksept, gyldig til), og kan bare trekkes tilbake med en ny hendelse.
- Koblinger til endringer, systemer/tjenester, saker og varsler, og til risikoeier og tiltaksansvarlig.
- 5×5-matrise i frontend; bakgrunnsjobb for forfalte tiltak, neste vurdering og utløpt aksept.

## 6. Teams

Kort gjennom den eksisterende køen: ny endring til godkjenning, status endret, kommende endringer, risikoer uten behandling, utløpt aksept, og lenke til revisjonshistorikken. Alle knapper er `Action.OpenUrl` til appen. Ingen beslutninger tas på grunnlag av innhold i kortet.

## 7. AI-agent (Foundry)

Et verktøy i appen som foreslår: risikoklassifisering, konsekvenser, sikkerhetstiltak, GDPR-konsekvenser, testplan, rollback-plan, manglende backup, kostnadsoptimalisering, ledelsesoppsummering og endringsdokumentasjon. Brukeren lagrer selv. Agenten har ingen skrivetilgang og ingen identitet med roller, og kan derfor ikke godkjenne, akseptere, endre status eller rettigheter. Regel for data: CLAUDE.md, kapittel AI.

## 8. GitHub og Terraform (simulert)

- `IChangeDeliveryProvider` med en mock-versjon. En endring kan kobles til PR, commit-SHA, pipeline-kjøring og hash av Terraform-plan.
- Ny SHA eller ny plan-hash gjør godkjenningen ugyldig (kapittel 3).
- `GET /api/changes/{nummer}/deployment-gate?environment=…` svarer som en «required check»: ja bare med gyldig godkjent endring og godkjent pipeline-steg. **Produksjon avvises alltid i prototypen.**
- Ekte integrasjon er av som standard (`Delivery:Provider = mock`).

## 9. Faser

Hver fase har egen migrering (hvis den trengs), tester og commit, og godkjennes før den starter.

| Fase | Innhold |
|---|---|
| C1 | Datamodell og migrering. Løpenumre. Godkjenninger og aksepter kan ikke endres. |
| C2 | Arbeidsflyt, godkjenning låst til innhold, SoD, standardmaler. API og tester, også negative sikkerhetstester. Syntetiske testbrukere i dev. |
| C3 | Risikoregister, aksept, koblinger, bakgrunnsjobb for frister og utløpt aksept. |
| C4 | Frontend: Endringer, Risiko, kalender, 5×5-matrise, innstillinger for roller, RACI og regler. |
| C5 | Teams-kort og påminnelser. |
| C6 | Simulert GitHub og Terraform, og sjekk før produksjon. |
| C7 | Dashboard og rapporter. |
| C8 | Foundry (felles med fase 5 steg 3) og agenten. |
| C9 | Syntetiske demodata (bare i dev, av som standard) og dokumentasjon (arkitektur, datamodell, API, prosesser, RACI, sikkerhet, integrasjoner, forutsetninger for produksjon). |

## 10. Absolutte begrensninger

- Ingen tilkobling til Antires produksjonsmiljø, Entra ID, Azure-abonnementer eller GitHub-repositorier.
- Ingen reelle personopplysninger og ingen import av produksjonsdata. Bare syntetiske data.
- Ingen faktiske produksjonssettinger og ingen Terraform Apply mot produksjon.
- Ingen produksjonshemmeligheter.
- Produksjonsintegrasjoner er av som standard og krever eksplisitt konfigurasjon og godkjenning.
- Videre arbeid mot produksjon krever skriftlig mandat, avklart systemeierskap og risikoeierskap, ansvarsfordeling, sikkerhetsgodkjenning og et separat testmiljø.

---

## C1: Datamodell og migrering (detaljert plan)

Ingen API eller frontend i C1, bare modellen, migreringen og tester. Alle tabeller er `ITenantOwned` (med tenant-filter) og `IAudited`, med mindre annet står.

### Tabeller

| Tabell | Innhold |
|---|---|
| `ChangeRequests` | `Number` (`CHG-000001`, unik per tenant), tittel, beskrivelse, begrunnelse, forventet gevinst, `Type`, `Priority`, `Environment`, `Status`, sannsynlighet og konsekvens (1–5) med beregnet score, risiko- og konsekvensanalyse, sikkerhetskonsekvens, GDPR-konsekvens (valgfri kobling til `ProcessingActivity`), kostnadsendring (beløp per måned og tekst), backupstatus (`verified`, `unverified`, `missing`, `not_applicable`) med merknad, implementeringsplan, testplan, testresultater, rollback-plan, planlagt start og slutt, innmelder (objekt-ID og ansatt), implementør (ansatt), implementeringsresultat, etterkontroll (påkrevd, notat, tidspunkt, av hvem), `TemplateId`, leveransekoblinger (PR-URL, commit-SHA, pipeline-kjøring, hash av Terraform-plan), `ScopeHash` (gjeldende hash), opprettet, endret, lukket |
| `ChangeAffectedServices` | Endring ↔ `ServiceCatalogItem` |
| `ChangeAzureScopes` | Endring ↔ `AzureSubscription`, med valgfri ressursgruppe |
| `ChangeApprovals` | Endring, rolle, beslutning (`approved`, `rejected`, `info_requested`), godkjenner (objekt-ID, navn, ansatt), tidspunkt, kommentar, `ScopeHash` ved beslutningen. **Kan ikke endres eller slettes** (stoppes i `AuditInterceptor`, som for `AuditEvent`). Gyldighet = hashen er lik endringens gjeldende `ScopeHash`. |
| `ChangeTemplates` | Standardmaler: navn, beskrivelse, planer (implementering, test, rollback), tillatte miljøer, maks risikoscore, godkjent av og når, neste gjennomgang, aktiv |
| `GovernanceRoleAssignments` | Rolle, omfang (global eller `ServiceCatalogItemId`), ansatt **eller** Entra-gruppe |
| `RaciEntries` | Aktivitet × rolle → `R`, `A`, `C` eller `I` |
| `ApprovalRules` | Type, miljø, påkrevd rolle, antall godkjenninger, rekkefølge |
| `Risks` | `Number` (`RISK-000001`), tittel, beskrivelse, kategori, sannsynlighet, konsekvens og beregnet score, eksisterende tiltak, anbefalte tiltak, restsannsynlighet og -konsekvens med score, strategi, status (`identified`, `assessed`, `in_treatment`, `monitoring`, `accepted`, `closed`), risikoeier og tiltaksansvarlig (ansatte), frist, neste vurdering |
| `RiskAcceptances` | Risiko, akseptert av (objekt-ID, navn, ansatt), tidspunkt, begrunnelse, score ved aksept, gyldig til, og `Kind` (`accepted` eller `revoked`). **Kan ikke endres eller slettes.** |
| `RiskChanges`, `RiskServices`, `RiskIncidents` | Risiko ↔ endring, ↔ tjeneste, ↔ sak eller varsel |
| `GovernanceSettings` | Per tenant: oppbevaringstid, påminnelser (dager før), om simulert leveranse er på |

**Løpenumre:** sekvensene `change_number_seq` og `risk_number_seq` i PostgreSQL. Nummeret settes når raden opprettes, og er unikt per tenant. SQLite-testene bruker en teller, som for ansattnummer.

**Standardoppsett per tenant** (lages første gang det trengs, som malene for onboarding): godkjenningsregler (Normal i produksjon: Change Manager + System Owner; Emergency: Change Manager + én til; Standard: ingen utover malen) og en RACI-matrise som utgangspunkt.

### Tester i C1

- Tenant-filter på alle nye tabeller.
- Løpenumre: format, økende, unike per tenant (SQLite med teller; sekvensen i `PostgresTests`).
- `ChangeApprovals` og `RiskAcceptances` kan ikke endres eller slettes.
- Score beregnes riktig og holdes innenfor 1–25.
- Migreringen bygger gjeldende modell (`PostgresTests`).
- Hele `dotnet test` grønn.

### Ikke i C1

API, arbeidsflyt, frontend, Teams, AI og testbrukere.

### Status C1 (2026-11-26): bygget

- Migreringen `AddChangeAndRiskManagement` er kjørt i den lokale dev-databasen. Den legger bare til nye tabeller og sekvenser.
- Sekvensene `change_number_seq` og `risk_number_seq` lages med SQL i migreringen, ikke i modellen, fordi SQLite (testene) ikke støtter sekvenser.
- Risikoscorer er beregnede kolonner i databasen (`RiskScore`, `Score`, `ResidualScore`), og 1–5 er kontroller i databasen.
- Godkjenninger og risikoaksept (`IAppendOnly`) stoppes i `AuditInterceptor` ved endring eller sletting, på samme måte som revisjonsloggen.
- Koblingstabellene heter `ChangeAffectedServices` og `ChangeAzureScopes`, slik at navnet `ChangeRequestService` er ledig for tjenesten i C2.
- Tester: `GovernanceModelTests` (15) mot SQLite og 1 ny i `PostgresTests` (sekvenser, beregnede scorer og kontroll i ekte PostgreSQL).

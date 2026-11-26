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

---

## C2: Arbeidsflyt, godkjenning og separasjon av plikter (detaljert plan)

Ingen frontend i C2 (kommer i C4). Ingen migrering er planlagt; alt bygger på tabellene fra C1.

### Tilgang (least privilege)

| Hvem | Kan |
|---|---|
| Alle innloggede i tillatt tenant med Helpdesk.Agent **eller** en endringsrolle | Se endringer |
| Innmelder | Se og endre egne utkast, sende inn, avbryte egne før implementering |
| Endringsroller (fra `GovernanceRoleAssignments`) | Overganger og beslutninger etter tabellen under |
| Helpdesk.Admin | Konfigurere roller, RACI, godkjenningsregler og maler. **Kan ikke tildele roller til seg selv.** |

**Hvem har en rolle:** den innloggede brukeren slås opp som ansatt (Entra objekt-ID), og rolletildelinger til den ansatte telles med. Tildelinger til Entra-grupper sjekkes mot Graph (`IsMemberOfAnyGroupAsync`, appens leserettighet). En rolle gjelder globalt eller for en tjeneste endringen berører. Rollene slås opp på nytt ved hver forespørsel; ingenting tas fra klienten.

### Overganger (`ChangeWorkflow`)

Klienten ber om en ny status; serveren sjekker type, nåværende status, rolle og vilkår.

| Fra → til | Rolle | Vilkår |
|---|---|---|
| draft → submitted | Innmelder | Påkrevde felt for typen (normal: beskrivelse, begrunnelse, miljø, minst én tjeneste, implementerings-, test- og rollback-plan, planlagt tid; produksjon: backupstatus er ikke `missing`) |
| submitted → risk_assessment | Change Manager | Normal |
| risk_assessment → technical_review | Change Manager eller Technical Owner | Sannsynlighet, konsekvens, risikoanalyse, sikkerhets- og GDPR-konsekvens er fylt ut |
| technical_review → testing | Technical Owner | |
| testing → pending_approval | Technical Owner eller Implementer | Testresultater er fylt ut (test og produksjon) |
| pending_approval → approved / rejected | **Automatisk** ved beslutninger | Se godkjenning |
| approved → scheduled | Change Manager | Planlagt start og slutt |
| scheduled → implementation | Implementer | **Gyldig godkjenning** (eller aktiv standardmal) |
| implementation → verification | Implementer | Implementeringsresultat er fylt ut |
| implementation → failed / rolled_back | Implementer | Notat påkrevd |
| verification → completed | Reviewer | Ikke den som implementerte |
| completed → closed | Change Manager | Emergency: etterkontroll er gjort |
| (før implementation) → cancelled | Innmelder eller Change Manager | |

**Standard:** opprettes fra en aktiv, forhåndsgodkjent mal som ikke har passert neste gjennomgang. Miljøet må være tillatt i malen, og risikoscoren kan ikke overstige malens maks. Planene kopieres fra malen og kan ikke endres. Løp: draft → submitted → scheduled (automatisk når vilkårene er oppfylt) → implementation → verification → completed → closed.

**Emergency:** draft → submitted → pending_approval (hurtiggodkjenning etter emergency-reglene) → approved → implementation (uten scheduled) → verification → completed → etterkontroll → closed. Etterkontroll (`POST …/post-review`) gjøres av Reviewer eller Change Manager som ikke implementerte.

### Godkjenning

- Beslutning: `POST /api/changes/{id}/decisions` med rolle, beslutning (`approved`, `rejected`, `info_requested`), kommentar og **hashen klienten så** (`expectedScopeHash`). Er hashen en annen enn endringens, avvises beslutningen (409): ingen kan godkjenne innhold de ikke har sett.
- Godkjenneren må ha rollen (globalt eller for en berørt tjeneste).
- **Separasjon av plikter (alle miljøer):** innmelder og implementør kan ikke godkjenne. Én person teller bare for én påkrevd regel per endring. Den som tildeler roller kan ikke tildele seg selv.
- Når alle regler for type og miljø har nok gyldige godkjenninger (samme hash), går endringen til `approved`. En `rejected` fra en påkrevd rolle gir `rejected`. `info_requested` beholder `pending_approval` og vises som «venter på informasjon».
- **Hash av godkjent innhold** (SHA-256 av kanonisk JSON): type, miljø, beskrivelse, tjenester, Azure-abonnementer og ressursgrupper, sannsynlighet og konsekvens, risiko- og konsekvensanalyse, sikkerhets- og GDPR-konsekvens, implementeringsplan, rollback-plan, commit-SHA og hash av Terraform-plan. Planlagt tidspunkt er ikke med (flytting krever ikke ny godkjenning, men logges).
- Endres noe av dette etter `approved` eller `scheduled`, går endringen tilbake til `pending_approval`, og `approval_invalidated` logges. Gamle godkjenninger blir liggende, men gjelder ikke.

### API

| Endepunkt | |
|---|---|
| `GET /api/changes` | Liste med filter (status, type, miljø, mine) |
| `GET /api/changes/{id}` | Endringen, godkjenninger med «gyldig», hvilke regler som mangler, og hvilke overganger **du** kan gjøre |
| `POST /api/changes` | Nytt utkast, eventuelt fra mal (`templateId`) |
| `PUT /api/changes/{id}` | Endre (innmelder eller Change Manager, før implementation) |
| `POST /api/changes/{id}/transitions` | Ny status |
| `POST /api/changes/{id}/decisions` | Godkjenne, avvise, be om informasjon |
| `POST /api/changes/{id}/post-review` | Etterkontroll (emergency) |
| `GET /api/changes/{id}/history` | Revisjonslogg for endringen og beslutningene |
| `GET /api/governance/me` | Mine roller |
| `/api/governance/roles`, `/raci`, `/approval-rules`, `/api/changetemplates` | Konfigurasjon (Helpdesk.Admin); maler godkjennes av Change Manager |

Nye hendelser i revisjonsloggen: `change_submitted`, `change_status_changed`, `change_approved`, `change_rejected`, `change_info_requested`, `approval_invalidated`, `change_post_reviewed`, `role_assigned`, `role_removed`.

### Syntetiske testbrukere i dev-tenanten

- Skriptet `scripts/New-GovernanceTestUsers.ps1`, bare for `3af5c92b` (stopper ellers): fem brukere `test.requester`, `test.changemanager`, `test.systemowner`, `test.implementer`, `test.reviewer` på tenantens `onmicrosoft.com`-domene, med visningsnavn «Test …» og ingen lisenser.
- Tilfeldige passord skrives til en fil i brukerprofilen din (ikke i repoet og ikke i chatten), med krav om bytte ved første pålogging.
- App-rollen Helpdesk.Agent på API-appen, og ansatt-rader via den vanlige synken fra Entra. Endringsrollene tildeles i appen.
- Kjøres med `-WhatIf` først, og etter tenant-sjekk.

### Tester

- Hele løpet for normal, standard og emergency.
- Ugyldige overganger (400) og feil rolle (403).
- Innmelder og implementør kan ikke godkjenne; én person teller ikke for to regler; Helpdesk.Admin kan ikke tildele seg selv roller.
- Beslutning med feil hash avvises (409). Endring i godkjent innhold gjør godkjenningen ugyldig og gir `pending_approval`; endring av tidspunkt gjør det ikke.
- Produksjon kan ikke gå til implementation uten gyldig godkjenning.
- Standard utenfor malens miljø eller risiko avvises, og planene kan ikke endres.
- Emergency kan ikke lukkes uten etterkontroll, og den som implementerte kan ikke gjøre etterkontrollen.
- Rolle via Entra-gruppe (falsk Graph).
- Tenant-skille, og nye rader i `AuthorizationTests`.

### Status C2 (2026-11-26): bygget

- `ChangeWorkflowService` (overganger, godkjenning, separasjon av plikter, standard og emergency), `GovernanceRoleService` (roller fra ansatt og Entra-grupper; uten svar fra Graph gis ingen rolle via gruppe), `ChangeScopeHash`.
- API: `ChangesController`, `GovernanceController` (me, roller, RACI, godkjenningsregler) og `ChangeTemplatesController` (maler; endret innhold fjerner forhåndsgodkjenningen, som gis på nytt av en global Change Manager).
- Ingen migrering.
- Endres implementøren etter en godkjenning, teller ikke godkjenningen fra den personen lenger.
- Testinnloggingen har fått en valgfri header for objekt-ID (`X-Test-ObjectId`), så testene kan bruke flere personer.
- Tester: `ChangeWorkflowTests` (14) og nye rader i `AuthorizationTests`.
- Skriptet `scripts/New-GovernanceTestUsers.ps1` lager de syntetiske testbrukerne.

---

## C3: Risikoregister (detaljert plan)

Ingen frontend i C3 (C4) og ingen Teams-varsler (C5). Ingen migrering er planlagt.

### Nivåer

Score = sannsynlighet × konsekvens (beregnet i databasen): **lav** 1–4, **middels** 5–9, **høy** 10–14, **kritisk** 15–25. Brukes i lister, matrise og regler under.

### Tilgang

| Hvem | Kan |
|---|---|
| Helpdesk.Agent eller en endringsrolle | Se risikoer, registrere nye |
| Risikoeier (`OwnerEmployeeId`), rollen Risk Owner (global eller for en koblet tjeneste), Security Officer | Endre vurdering, tiltak, strategi, eier og koblinger |
| Tiltaksansvarlig | Oppdatere status for tiltak (in_treatment → monitoring) |
| **Utpekt risikoeier som også har rollen Risk Owner** | Akseptere og trekke tilbake aksept |

### Statuser (styrt på serveren)

identified → assessed (sannsynlighet, konsekvens og eier er satt) → in_treatment (strategi mitigate, transfer eller avoid, tiltaksansvarlig og frist) → monitoring → closed. `accepted` nås bare gjennom aksept. Lukking krever en kommentar.

### Risikoaksept

- `POST /api/risks/{id}/acceptances` med begrunnelse, gyldig til (maks 12 måneder) og **scoren personen så**. Annen score enn nå → 409.
- Krever strategi `accept` og at personen er utpekt risikoeier **og** har rollen Risk Owner.
- **Kritiske risikoer** (15–25) kan ikke aksepteres av den som registrerte risikoen (hentes fra revisjonsloggen).
- Lagres som ny rad (`RiskAcceptances`); tilbaketrekking er en ny rad med `revoked`.
- Aksepten gjelder bare så lenge scoren er den samme og den ikke er utløpt. Øker scoren, eller utløper aksepten, går risikoen tilbake til `assessed`, og hendelsen logges.

### Koblinger

`PUT /api/risks/{id}/links` med endringer, tjenester, saker og varsler (bare fra samme tenant). Endringsdetaljene (C2) får listen over koblede risikoer.

### Bakgrunnsjobb (`RiskMonitorService`)

Mønsteret fra `SlaMonitorService`: hver time, per tillatt tenant, aktør `system`, én tenant som feiler stopper ikke de andre. Logger én gang per hendelse: tiltak forbi frist (`risk_action_overdue`), vurdering forfalt (`risk_review_due`), aksept utløpt (`risk_acceptance_expired`, status tilbake til `assessed`). Teams-varsel for disse kommer i C5. Konfigurasjon: `Risks:MonitorEnabled` (standard true), `Risks:CheckIntervalSeconds` (3600).

### API

| Endepunkt | |
|---|---|
| `GET /api/risks` | Filter: kategori, status, nivå, eier, uten eier, forfalte tiltak, venter på aksept |
| `GET /api/risks/{id}` | Risikoen, gjeldende aksept (gyldig eller ikke), akseptshistorikk, koblinger og hva **du** kan gjøre |
| `POST /api/risks`, `PUT /api/risks/{id}` | Registrere og endre |
| `POST /api/risks/{id}/transitions` | Ny status |
| `POST /api/risks/{id}/acceptances`, `…/acceptances/revoke` | Aksept og tilbaketrekking |
| `PUT /api/risks/{id}/links` | Koblinger |
| `GET /api/risks/matrix` | 5×5-matrise med antall (iboende og restrisiko) |
| `GET /api/risks/{id}/history` | Revisjonslogg |

### Tester

Nummer og score, nivåer, statusoverganger og vilkår, tilgang per rolle (403), aksept bare av utpekt eier med rollen, feil score (409), kritisk risiko kan ikke aksepteres av den som registrerte den, aksept faller bort når scoren øker og når den utløper (jobben), tilbaketrekking som ny rad, koblinger bare innen tenanten, matrisen teller riktig, jobben logger hver hendelse én gang og fortsetter når én tenant feiler, tenant-skille, og rader i `AuthorizationTests`.

### Status C3 (2026-11-26): bygget

- `RiskRegisterService` (statuser, tilgang, aksept, koblinger), `RiskMonitorService` (hver time), `RisksController` (`/api/risks`, matrise, historikk). Endringsdetaljene viser koblede risikoer.
- Ingen migrering. Tjenesten heter `RiskRegisterService` fordi `RiskService` er koblingstabellen.
- De beregnede scorene er merket `[JsonInclude]`, slik at klienter som leser JSON tilbake (testene) får dem med.
- Ikke testet automatisk: at jobben fortsetter når én tenant feiler (samme `try/catch` per tenant som SLA-jobben).
- Tester: `RiskRegisterTests` (18).

---

## C4: Frontend (detaljert plan)

To nye sider i menyen etter **Driftsrapport**: **Endringer** og **Risiko**, og nye faner under **Innstillinger**. Samme stil og hjelpefunksjoner som resten av appen (`useList`, `useResource`, `useSend`, `Helpdesk.css`), tekster på norsk med engelsk i `i18n/en`. Alle knapper vises ut fra det API-et sier at **du** kan gjøre (`allowedTransitions`, `canDecideAs`, `canEdit`, `canAccept`); serveren sjekker uansett på nytt.

### Endringer

- **Liste** med filter (status, type, miljø, mine) og fargemerker for type, miljø og status. Produksjon er tydelig merket.
- **Kalender**: planlagte endringer per uke (enkel ukevisning, ingen nytt bibliotek), med produksjon uthevet.
- **Ny endring**: velg type. Standard: velg aktiv mal, planene fylles ut og er låst. Skjema delt i seksjoner (beskrivelse, omfang, risiko og konsekvens, GDPR og sikkerhet, kostnad og backup, planer, tidspunkt). Viser hva som mangler før innsending.
- **Detaljer**: alle felt, godkjenninger (gyldig eller ugyldig, med hvem og når), hvilke regler som mangler, «venter på informasjon», koblede risikoer, og knapper for overganger, godkjenning (med kommentar; hashen sendes automatisk), etterkontroll og avbryting. Varsel når godkjent innhold endres: «Lagring sender endringen tilbake til godkjenning».
- **Historikk**: revisjonsloggen for endringen.

### Risiko

- **Liste** med filter (kategori, status, nivå, uten eier, forfalte tiltak, venter på aksept) og nivåfarger.
- **5×5-matrise** (iboende og restrisiko), klikk i en rute filtrerer listen.
- **Ny risiko** og **detaljer**: vurdering, tiltak, strategi, eier og tiltaksansvarlig, frist og neste vurdering, koblinger, aksept (med begrunnelse og utløpsdato; scoren sendes automatisk), tilbaketrekking, akseptshistorikk og revisjonslogg.

### Innstillinger (Helpdesk.Admin)

- **Endringsroller**: tildel rolle til ansatt eller Entra-gruppe, globalt eller per tjeneste. Du kan ikke velge deg selv.
- **RACI**: matrise aktivitet × rolle med R, A, C, I.
- **Godkjenningsregler**: per type og miljø.
- **Endringsmaler**: maler for standardendringer; Change Manager forhåndsgodkjenner fra samme fane.

### Liten tilføyelse i backend

`GET /api/changes/options` (Helpdesk.Agent eller endringsrolle): tjenester, Azure-abonnementer (id og navn), aktive maler og ansatte (id og navn) til skjemaene. I dag kan bare Helpdesk.Admin liste Azure-abonnementer. Test for tilgang.

### Kontroll

`npm run build` og `npm run lint`, hele `dotnet test`, og at du prøver i nettleseren med testbrukerne (normal, standard og emergency, og aksept av en risiko).

### Status C4 (2026-11-26): ferdig, prøvd i nettleser med testbrukerne

- Sidene **Endringer** (`Changes.tsx`: liste, ukekalender, skjema, detaljer med godkjenning og historikk) og **Risiko** (`Risks.tsx`: 5×5-matrise, liste, skjema, aksept, koblinger), og fanene Endringsroller, RACI, Godkjenningsregler og Endringsmaler under Innstillinger (`settings/GovernanceSettings.tsx`). Felles typer og etiketter i `governance.ts`, engelsk i `i18n/en/governance.ts`.
- `GET /api/changes/options` (Helpdesk.Agent eller endringsrolle) med test.
- Koblinger til saker, varsler og endringer i risikoskjemaet legges inn som ID-er (enkel første versjon).

---

## C5: Teams-kort og påminnelser (detaljert plan)

Bygger på den eksisterende køen (`TeamsNotificationService`: kø, nye forsøk, kryptert webhook-adresse per tenant). **Ingen knapper som gjør noe i selve kortet**: alle knapper er `Action.OpenUrl` til appen, der serveren sjekker identitet og rolle (avklaring A). Dev-tenanten har ikke Teams-lisens, så kortene testes mot den falske Teams-tjenesten, som i fase 4.

### Kort

| Hendelse | Når | Innhold |
|---|---|---|
| Venter på godkjenning | Endringen går til `pending_approval` (også på nytt etter endret innhold) | Nummer, tittel, type, miljø, risikoscore, planlagt tid, hvilke roller som mangler. «Åpne og vurder» |
| Ber om informasjon | `info_requested` | Nummer, tittel, rollen som spør. «Åpne endringen» |
| Status endret | `approved`, `rejected`, `implementation`, `completed`, `failed`, `rolled_back`, `closed` | Nummer, tittel, ny status |
| Kommende endring | Produksjonsendringer som er `approved` eller `scheduled` og starter innen `ReminderDaysBefore` (innstilling, standard 3 dager). Én gang per endring og planlagt start | Nummer, tittel, start og slutt |
| Etterkontroll mangler | Hasteendring `completed` uten etterkontroll i mer enn ett døgn. Én gang | Nummer, tittel |
| Risiko ikke behandlet | Høy eller kritisk risiko uten eier, eller `identified` i mer enn 7 dager. Én gang per risiko | Nummer, tittel, nivå, kategori |
| Tiltak forbi frist | Samme hendelse som jobben allerede logger (`risk_action_overdue`) | Nummer, tittel, frist |
| Aksept utløper snart / er utløpt | 14 dager før `ValidUntil`, og når den utløper (`risk_acceptance_expired`) | Nummer, tittel, gyldig til |
| Revisjonshistorikk | Alle kort har lenken «Se historikk» | |

**Innhold:** bare nummer, tittel, kategorier, status, tidspunkt og roller. Ingen beskrivelser, planer, analyser eller begrunnelser i kortene.

### Lenker inn i appen

`?endring=<id>` åpner Endringer med endringen, `?risiko=<id>` åpner Risiko med risikoen (som `?sak=` for saker). Med `&historikk=1` vises historikken med en gang.

### Innstilling og jobb

- Ny innstilling per tenant: **Teams-varsler for endringer og risiko** (på/av, standard på), i `GovernanceSettings`. Liten migrering (`AddGovernanceTeamsSetting`, én kolonne).
- Fanen Teams under Innstillinger får bryteren.
- `RiskMonitorService` blir `GovernanceMonitorService` og tar også påminnelsene for endringer (kommende endringer, manglende etterkontroll, aksept som utløper snart). `Risks:*`-innstillingene virker fortsatt.
- Hver påminnelse logges i revisjonsloggen med en nøkkel, slik at den sendes én gang, også etter omstart.

### Tester

Riktig type kort i køen for hver hendelse, ingen beskrivelser eller planer i kortet, lenkene peker på riktig side, ingen kort når innstillingen er av eller Teams ikke er satt opp, påminnelser sendes én gang (også ved ny kjøring), tenant-skille, og at feil i Teams ikke stopper endringen (som i fase 4).

### Status C5 (2026-11-26): bygget, kort testet mot falsk Teams

- `GovernanceNotifier` lager kortene; arbeidsflyten sender dem etter lagring. `RiskMonitorService` heter nå `GovernanceMonitorService` og tar også påminnelser for endringer (`Governance:*`, med `Risks:*` som reserve).
- Migreringen `AddGovernanceTeamsSetting` (bryteren er på for eksisterende tenanter) er kjørt i den lokale dev-databasen.
- `GET/PUT /api/governance/settings` (Helpdesk.Admin), og bryteren under Innstillinger → Teams.
- Lenker `?endring=`, `?risiko=` og `&historikk=1` åpner riktig side.
- Ekte Teams-kanal er ikke prøvd (ingen Teams-lisens i dev-tenanten).
- Tester: `GovernanceTeamsTests` (5).

---

## C6: Simulert GitHub og Terraform (detaljert plan)

Alt er **simulert**. Ingen kall til GitHub eller Azure, ingen ekte pipeline, ingen Terraform Plan eller Apply. Feltene for leveranse finnes allerede på endringen (C1), og commit-SHA og hash av Terraform-plan er allerede med i hashen for godkjent innhold (C2). **Ingen migrering.**

### Leverandør

- `IChangeDeliveryProvider` med én implementasjon: `MockDeliveryProvider`. Konfigurasjon `Delivery:Provider` (standard `mock`). Andre verdier gir «Ekte integrasjon er ikke bygget» og ingen kall ut.
- Innstillingen `SimulatedDeliveryEnabled` (C1) slår funksjonen av og på per tenant.

### Hva man kan gjøre

| Endepunkt | Hvem | Hva |
|---|---|---|
| `POST /api/changes/{id}/delivery/link` | Innmelder, Technical Owner eller Implementer | Kobler PR-URL (bare `https://github.com/…`), commit-SHA (7–40 heksadesimale tegn) og pipeline-kjøring. Ny SHA gjør godkjenningen ugyldig. |
| `POST /api/changes/{id}/delivery/plan` | Samme | **Simulert Terraform Plan**: lager en syntetisk plan for omfanget (abonnementer og ressursgrupper), med antall add, change og destroy. Serveren regner ut hashen og lagrer den. Ny plan gjør godkjenningen ugyldig. Planer med destroy merkes tydelig. |
| `GET /api/changes/{nummer}/deployment-gate?environment=&commitSha=&planHash=` | Helpdesk.Agent eller endringsrolle (senere: pipelinens identitet) | **Sjekken en pipeline ville kalt før Apply.** Ja bare når endringen er i `implementation`, har gyldig godkjenning (eller aktiv standardmal), miljøet stemmer, og commit og plan er de samme som ble godkjent. **Produksjon avvises alltid i prototypen.** Hver sjekk logges (`change_gate_checked`) med svar og årsak. |
| `POST /api/changes/{id}/delivery/simulate-apply` | Implementer | **Simulert Apply** i utvikling eller test: kaller sjekken over og registrerer resultatet. Ingen infrastruktur endres. |

### Policy as Code, beskyttede miljøer og påkrevde godkjennere

- Dokumenteres i en ny del i denne filen: hvordan et GitHub-miljø med beskyttelsesregel, påkrevde godkjennere og sjekken over ville vært koblet sammen, og at sjekken senere skal kalles av pipelinens egen identitet (GitHub OIDC mot Entra), ikke av en person.
- Et **eksempel** på en GitHub Actions-arbeidsflyt legges i `docs/examples/deploy-with-change-gate.yml`, **ikke** i `.github/workflows`, slik at den aldri kjører.

### Frontend

Ny seksjon **Leveranse** i endringsdetaljene: koble PR og commit, lage simulert plan (med antall og hash), se svaret fra sjekken per miljø, og simulert Apply i utvikling og test. Tydelig merket «Simulert».

### Tester

Kobling og plan gjør godkjenningen ugyldig. Sjekken sier nei uten godkjenning, ved feil commit eller plan, feil miljø, feil status og **alltid for produksjon**, og ja når alt stemmer. Hver sjekk logges. Ugyldig PR-URL og SHA avvises. Funksjonen kan slås av. Ingen HTTP-kall ut (den falske leverandøren brukes; testen sjekker at ingen `HttpClient` brukes). Tilgang per rolle og tenant-skille.

### Policy as Code, beskyttede miljøer og påkrevde godkjennere (C6)

Slik er det tenkt koblet sammen når en eventuell produksjonsintegrasjon bygges (eksempel i `docs/examples/deploy-with-change-gate.yml`, som ikke kjører):

1. **Pull request** med endringsnummeret i tittelen. Commit-SHA kobles til endringen (`/delivery/link`); ny commit gjør godkjenningen ugyldig.
2. **Terraform Plan** i pipelinen. Hashen av planen kobles til endringen; ny plan gjør godkjenningen ugyldig. Policy as Code (f.eks. Azure Policy, OPA eller Checkov) kjøres mot planen før godkjenning.
3. **Godkjenning i appen** etter reglene, med separasjon av plikter, låst til innholdet (inkludert commit og plan).
4. **Beskyttet miljø i GitHub** (`production`) med påkrevde godkjennere. Jobben for Apply kaller `deployment-gate` med miljø, commit og plan-hash og stopper ved nei.
5. **Pipelinens egen identitet** (GitHub OIDC med federated credential i Entra) kaller sjekken. Ingen personlige tokener eller hemmeligheter i repoet.
6. **Hver sjekk logges** i revisjonsloggen med svar og årsak.

I prototypen er sjekken simulert, og **produksjon avvises alltid**.

### Status C6 (2026-11-26): bygget (alt simulert)

- `IChangeDeliveryProvider` med bare `MockDeliveryProvider` (ingen kall ut), `ChangeDeliveryService` (plan, sjekk før Apply, simulert Apply), endepunkter under `/api/changes/{id}/delivery/…` og `/api/changes/{nummer}/deployment-gate`.
- Kobling av commit og plan bruker samme ugyldiggjøring av godkjenning som vanlig redigering (`Rehash` i `ChangeWorkflowService`).
- Bryteren `SimulatedDeliveryEnabled` kan settes via `PUT /api/governance/settings`.
- Seksjonen **Leveranse** (merket «Simulert») i endringsdetaljene. Eksempel på arbeidsflyt i `docs/examples/deploy-with-change-gate.yml` (kjører ikke).
- Ingen migrering. Tester: `ChangeDeliveryTests` (9).

---

## C7: Dashboard og rapporter (detaljert plan)

Tallene beregnes av koden (CLAUDE.md), i ett nytt endepunkt. Ingen migrering.

### Backend

`GET /api/governance/dashboard` (Helpdesk.Agent eller endringsrolle; samme tilgang som listene), for tenanten i scopet:

| Område | Nøkkeltall |
|---|---|
| Endringer | Åpne (ikke lukket, avvist, avbrutt), venter på godkjenning, planlagte produksjonsendringer de neste 14 dagene (liste med nummer, tittel og start), vellykkede (lukket/gjennomført) og mislykkede (feilet) siste 90 dager, rollbacks siste 90 dager, hasteendringer uten etterkontroll |
| Risiko | Åpne, kritiske (15–25), uten eier, venter på aksept, aksepterte (gyldig aksept), forfalte tiltak, aksept som utløper innen 14 dager |
| Matrise | Samme 5×5 som risikosiden (iboende og restrisiko) |
| Ansvar og eierskap | Per rolle: antall personer og grupper med rollen, og om rollen mangler helt. Per risikoeier: antall åpne risikoer og hvor mange som er kritiske. |

### Frontend

- **Dashboard** (startsiden) får en ny del **Endringer og risiko** under sakene: kort for nøkkeltallene (klikk åpner riktig side med filter), listen over kommende produksjonsendringer, og 5×5-matrisen.
- Vises bare for dem som har tilgang (Helpdesk.Agent eller en endringsrolle).
- **Ansvar og eierskap** vises som en liten tabell, med varsel når en påkrevd rolle i godkjenningsreglene ikke er tildelt noen.

### Tester

Tallene stemmer for kjente data (hver teller), 90-dagersvinduet, tenant-skille, tilgang (403 uten rolle), og at roller som kreves i godkjenningsreglene men mangler, blir merket.

### Status C7 (2026-11-26): bygget

- `GovernanceDashboardService` og `GET /api/governance/dashboard` (Helpdesk.Agent eller endringsrolle). Feilede, rullet tilbake og gjennomførte endringer telles fra revisjonsloggen (statusendringer siste 90 dager); «vellykket» = gjennomført uten feil eller rollback.
- Delen **Endringer og risiko** på Dashboard (`GovernanceDashboard.tsx`) med kort som åpner riktig side med filter (`?status=`, `?level=`, `?flag=`), kommende produksjonsendringer, 5×5-matrise, roller med varsel for påkrevde roller uten noen tildelt, og risikoeiere.
- Ingen migrering. Tester: `GovernanceDashboardTests` (2).

---

## C8: Foundry og Change and Risk-agenten (detaljert plan)

Felles med steg 3 i fase 5 (FASE5_DRIFTSRAPPORT.md kap. 4): ett AI-lag som både driftsrapporten og agenten bruker. AI er **av som standard** (`Ai:Enabled = false`) og brukes bare i utviklingstenanten.

### AI-laget

- `IAiClient` med `FoundryAiClient`: `POST {Ai:Endpoint}/openai/v1/chat/completions`, `model` = deploymentnavnet, `response_format: json_object`. Innlogging med `AppCredentials` og scope `https://cognitiveservices.azure.com/.default`. **Ingen API-nøkkel.**
- `FakeAiClient` i testene, som tar vare på hver forespørsel.
- Instruksjonene ligger i `backend/Prompts/` (`ops-report.md`, `change-assist.md`, `risk-summary.md`), slik at de kan justeres uten kodeendring.
- Tidsavbrudd, og maks antall forespørsler per bruker per time (standard 20), så kostnaden holdes nede.

### Hva som sendes (CLAUDE.md, kapittel AI)

- `AiPayloadBuilder` bygger forespørselen. Med: tekstfeltene i endringer og risikoer (tittel, beskrivelse, begrunnelse, planer, analyser), type, miljø, risikoscore, backupstatus og kategorier.
- **Aldri med:** navn, e-post, objekt-ID-er, ansatt-ID-er, sakstekster, varseltitler, ressurs-ID-er, abonnementsnavn.
- Tekstfeltene vaskes før de sendes: e-postadresser, GUID-er og Azure-ressurs-ID-er (`/subscriptions/…`) erstattes, og navnene på alle ansatte i tenanten erstattes med «[person]». Personer i endringen omtales med rollen sin.
- Hver forespørsel logges i revisjonsloggen med oppgave og endringsnummer, **ikke** innholdet.

### Agenten: bare forslag

| Endepunkt | Gir forslag til |
|---|---|
| `POST /api/ai/changes/{id}/assist` med `task` | risikoklassifisering (sannsynlighet og konsekvens med begrunnelse), konsekvensanalyse, sikkerhetstiltak, GDPR-konsekvenser, testplan, rollback-plan, manglende backup, kostnadsoptimalisering, endringsdokumentasjon |
| `POST /api/ai/changes/draft` | utkast til en ny endring fra en kort tekst (felt som brukeren går gjennom og lagrer selv) |
| `POST /api/ai/risks/summary` | oppsummering av risikoene for ledelsen (tall fra dashboardet, titler og kategorier) |

- Svarene vises som «Forslag fra AI», og knappen **Bruk forslaget** fyller bare inn skjemaet. Lagring går gjennom det vanlige API-et, med alle kontroller.
- Agenten har **ingen verktøy, ingen skrivetilgang og ingen identitet med roller**. Den kan derfor ikke godkjenne, akseptere risiko, endre status, gjennomføre noe eller gi seg selv rettigheter. Svaret behandles alltid som tekst og leses aldri som en kommando.
- Tilgang som for endringer og risiko (Helpdesk.Agent eller en endringsrolle), og bare til endringer personen kan se.

### Driftsrapporten (fase 5, steg 3)

`IOpsReportWriter` med AI-laget: bare de aggregerte nøkkeltallene sendes. `AiStatus` blir `ok`, `failed` eller `disabled`. Rapporten lagres og vises uansett.

### Infrastruktur (Terraform i dev-abonnementet)

- `azurerm_cognitive_account` (`AIServices`, Sweden Central, `local_auth_enabled = false`), `azurerm_cognitive_deployment` (`gpt-4.1-mini`, Data Zone Standard (EU), 20k TPM) og rollen **Cognitive Services OpenAI User** til API-appens service principal (både sertifikatet lokalt og den federerte identiteten i Azure logger inn som den).
- App-innstillinger `Ai__Enabled`, `Ai__Endpoint`, `Ai__Deployment`.
- Jeg kjører `terraform plan` og viser deg den før `apply`. Forventet kostnad er lav (betaling per token).

### Frontend

- Panelet **Forslag fra AI** i skjemaet og detaljene for endringer, i risikodetaljene, og en knapp for ledelsesoppsummering på Risiko. Tydelig merket «Skrevet av AI – forslag».
- Driftsrapporten viser AI-oppsummeringen med merket «Skrevet av AI» (finnes fra steg 2).

### Tester

Forespørselen inneholder ikke navn, e-post, objekt-ID-er eller ressurs-ID-er fra testdataene (også når de står i fritekst). Ingen endring i status, godkjenninger eller felt etter AI-kall. AI av, feil og tidsavbrudd gir en tydelig melding, og driftsrapporten lagres likevel. Grensen per time. Tilgang og tenant-skille. Svar som ikke er gyldig JSON håndteres.

### Status C8 (2026-11-26): bygget, ressursen i Azure er opprettet

- AI-laget: `IAiClient` med `FoundryAiClient` (AppCredentials, ingen nøkkel), `AiService` (av/på, 20 forespørsler per bruker per time, instruksjoner i `backend/Prompts/`), `AiTextScrubber` (e-post, GUID-er, ressurs-ID-er og navn på ansatte byttes ut).
- Agenten: `AiAssistService` og `AiController` (`/api/ai/status`, `/api/ai/changes/{id}/assist`, `/api/ai/changes/draft`, `/api/ai/risks/summary`). Bare forslag; bare tillatte felt fra svaret tas med; revisjonsloggen får oppgave og nummer, ikke innhold.
- Driftsrapporten (fase 5 steg 3): `OpsReportService` sender bare nøkkeltallene og setter `AiStatus` `ok`, `failed` eller `disabled`.
- Frontend: «Forslag fra AI» i endringsdetaljene, «Lag utkast med AI» i nytt skjema, «Oppsummering for ledelsen» på Risiko. Vises bare når AI er på.
- Terraform: `azurerm_cognitive_account` (Sweden Central, ingen nøkkelinnlogging), `azurerm_cognitive_deployment` (`gpt-4.1-mini`, DataZoneStandard, 20k TPM) og rollen Cognitive Services OpenAI User til API-appen. Kjørt med `apply` i dev (3 nye, 1 endret, 0 slettet). `ai_enabled` er `false` i Azure; AI er bare slått på lokalt (user secrets).
- Tester: `AiAgentTests` (6) og `AiDisabledTests` (1), med `FakeAiClient`.

---

## C9: Syntetiske demodata og dokumentasjon (detaljert plan)

### Demodata

- `GovernanceDemoSeeder` og `POST /api/governance/demo-data` (Helpdesk.Admin). **Virker bare når** `Demo:Enabled = true` **og** innlogget tenant er utviklingstenanten (`3af5c92b`); ellers 404. Av som standard, og aldri satt i Azure.
- Lager data **gjennom de vanlige tjenestene** (`ChangeWorkflowService`, `RiskRegisterService`), slik at revisjonsloggen, hashene og godkjenningene blir ekte. Aktørene er de syntetiske testbrukerne fra C2 (må være synket inn som ansatte); mangler de, sier endepunktet hvilke som mangler.
- Innhold (alt merket «[DEMO]» i tittelen, oppdiktet, ingen ekte personer eller systemer):
  - 8 endringer: én i hver viktige status (utkast, venter på godkjenning, godkjent, planlagt, implementeres, lukket, avvist, rullet tilbake), en hasteendring uten etterkontroll, og en standardendring fra en demomal.
  - 10 risikoer, én per kategori, spredt i matrisen: kritisk uten eier, under behandling med forfalt tiltak, akseptert med aksept som utløper snart, overvåkes, lukket.
  - Koblinger mellom risikoer og endringer, og en standardmal.
- Kan kjøres flere ganger: hopper over det som allerede finnes (tittel med «[DEMO]»).
- **Fjerne** demodata: `DELETE /api/governance/demo-data` sletter bare rader merket «[DEMO]» som ikke har godkjenninger eller aksepter (de kan ikke slettes); resten settes til avbrutt eller lukket. Revisjonsloggen beholdes.
- Knapp under Innstillinger → Endringsmaler (vises bare når funksjonen er på).
- De tre utkastene CHG-000001–000003 jeg la inn direkte, blir liggende (de har ingen revisjonslogg); jeg kan avbryte dem hvis du vil.

### Dokumentasjon (`docs/endring-og-risiko/`)

| Fil | Innhold |
|---|---|
| `README.md` | Oversikt og lenker |
| `arkitektur.md` | Komponenter (backend, frontend, bakgrunnsjobber, Teams-kø, Foundry, mock-leveranse), dataflyt og diagram (Mermaid) |
| `datamodell.md` | Tabeller, felt, relasjoner, kontroller i databasen og hva som ikke kan endres |
| `api.md` | Alle endepunkter med tilgang, input og svar |
| `endringsprosess.md` | Typer, statuser og overganger (diagram), godkjenning låst til innhold, standard og hasteendring |
| `risikoprosess.md` | Nivåer, statuser, strategier, aksept og utløp, bakgrunnsjobben |
| `raci.md` | Roller, standard RACI-matrise og godkjenningsregler, og hvordan de endres |
| `sikkerhet.md` | Tilgang (app-roller og endringsroller), least privilege, separasjon av plikter, serversidekontroll, revisjonslogg, hemmeligheter, tenant-skille, oppbevaring |
| `integrasjoner.md` | Teams (kort, kø, innhold), Foundry (AI-laget, hva som sendes, begrensninger), GitHub og Terraform (simulert, sjekken, eksempel) |
| `produksjon.md` | Forutsetninger før produksjon: skriftlig mandat, system- og risikoeierskap, ansvarsfordeling, sikkerhetsgodkjenning, eget testmiljø, ekte Teams-bot, pipelinens identitet, DPIA og behandlingsprotokoll for AI, og hva som er av i dag |

`README.md` i rotmappen får en kort lenke. `ENDRING_OG_RISIKO.md` beholdes som plan og logg.

### Tester

Demodata: virker bare med `Demo:Enabled` og utviklingstenanten (404 ellers), bare Helpdesk.Admin, lager riktig antall i riktige statuser med revisjonslogg, kan kjøres to ganger uten dubletter, sier fra om manglende testbrukere, og fjerning rører bare «[DEMO]»-rader.

### Status C9 (2026-11-26): bygget

- `GovernanceDemoSeeder` og `GET/POST/DELETE /api/governance/demo-data` (Helpdesk.Admin, bare med `Demo:Enabled` og i `Demo:TenantId`, standard utviklingstenanten). Dataene lages gjennom de vanlige tjenestene som testbrukerne (`HttpCurrentUser.UseIdentity`, bare brukt her, i et eget scope). 10 endringer og 10 risikoer merket «[DEMO]»; kan kjøres på nytt; fjerning sletter bare rader uten beslutninger og avslutter resten.
- Knapp under Innstillinger → Endringsmaler (vises bare når funksjonen er på).
- Dokumentasjon i `docs/endring-og-risiko/` (10 filer) og lenker i `README.md`.
- Tester: `GovernanceDemoTests` (3), `GovernanceDemoDisabledTests` (1), `GovernanceDemoMissingUsersTests` (1).

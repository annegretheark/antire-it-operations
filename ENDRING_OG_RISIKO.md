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

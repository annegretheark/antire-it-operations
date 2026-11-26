# Fase 5: Ukentlig driftsrapport med AI

| | |
|---|---|
| Status | Utkast v0.1 (2026-11-25) |
| Bygger på | Fase 1 (SLA, revisjonslogg), fase 2 (Microsoft 365), fase 4 (varsler og Teams), 8b (drift i Azure) |
| Tenant | Kun utvikling (`3af5c92b-…`). Antire-tenanten røres ikke, se `CLAUDE.md`. |

## 1. Mål

Hver mandag morgen får IT en kort rapport i Teams og i appen: hva som skjedde i driften forrige uke, hva som haster nå, og maks tre anbefalte tiltak.

Prinsippet er at **koden regner, AI skriver**. Alle tall i rapporten beregnes av appen fra databasen og eksisterende tjenester. AI-modellen får bare de ferdige tallene og skriver oppsummering og tiltak. Feiler AI-kallet, sendes rapporten likevel, uten AI-delen.

## 2. Innhold i rapporten

Periode: forrige hele uke (mandag 00:00 til søndag 23:59, Europe/Oslo), lagret som UTC.

| Område | Nøkkeltall | Kilde |
|---|---|---|
| Saker | Nye, løste, åpne nå per prioritet, åpne eldre enn 14 dager | `Tickets` |
| SLA | Brudd på respons og løsning i perioden, saker «I fare» nå | `Tickets`, `SlaService` |
| Varsler | Nye og gjenåpnede per kilde og alvorlighetsgrad, kilder med feilstatus nå | `Alerts`, `AlertSourceStatusRegistry` |
| Tilgang | Bestillinger som venter på godkjenning, eldste i dager | `AccessRequests` |
| Livssyklus | Onboarding og offboarding som starter de neste 14 dagene | `Lifecycle` |
| Microsoft 365 | Inaktive gjester (ingen pålogging på 90 dager) | `GraphService` (samme som M365-siden) |

Kostnader i Azure tas med i et senere steg (Cost Management, se `AZURE_INTEGRATIONS.md` 2.2). Det krever rollen Cost Management Reader for appen.

## 3. Personvern

Rapporten er driftsdata, men saker og varsler kan inneholde personopplysninger. Derfor:

- **Til modellen sendes bare aggregerte tall og kategorinavn.** Aldri navn, e-post, sakstekster, beskrivelser, varseltitler eller ressurs-ID-er.
- En test sjekker at forespørselen til modellen ikke inneholder e-post eller navn på ansatte fra testdataene.
- Modellen kjøres i egen Foundry-ressurs i Sweden Central med deployment-typen **Data Zone Standard (EU)**, slik at data behandles innenfor EU. Velg Global Standard bare hvis modellen ikke er tilgjengelig som Data Zone.
- Tiltaket føres inn i behandlingsprotokollen i GDPR-modulen.

## 4. AI-delen

| | |
|---|---|
| Tjeneste | Azure OpenAI i Microsoft Foundry, egen ressurs i dev-abonnementet |
| Modell | `gpt-4.1-mini`, versjon `2025-04-14`, kapasitet 20k TPM |
| API | OpenAI v1: `POST {Ai:Endpoint}/openai/v1/chat/completions`, `model` = deployment-navnet, `response_format: json_object` |
| Innlogging | `AppCredentials` (samme som Graph og Azure), scope `https://cognitiveservices.azure.com/.default`. Ingen API-nøkkel; `local_auth_enabled = false` på ressursen. |
| Rettighet | Rollen **Cognitive Services OpenAI User** på Foundry-ressursen til service principal for API-app-registreringen |
| Svarformat | `{ "oppsummering": "3–5 setninger", "tiltak": ["maks 3 konkrete tiltak"] }` |
| Instruks | Ligger i `backend/Prompts/ops-report.md`, slik at den kan justeres uten kodeendring. Skal be modellen bruke bare tallene den får, ikke finne på årsaker, og si tydelig fra hvis tallene ikke gir grunnlag for tiltak. |

Konfigurasjon: `Ai:Enabled` (standard false), `Ai:Endpoint`, `Ai:Deployment`.

Kallet går gjennom et grensesnitt, `IOpsReportWriter`, slik at testene kan bruke en falsk versjon, på samme måte som `FakeGraphService` og `FakeAzureService`.

## 5. Backend

**Modell:** `OpsReport` (`ITenantOwned`, `IAudited`)

| Felt | Type | Merknad |
|---|---|---|
| `Id` | int | |
| `TenantId` | string | tenant-filter som alle andre tabeller |
| `PeriodStart`, `PeriodEnd` | DateTime (UTC) | unik per tenant og periode |
| `Metrics` | jsonb | nøkkeltallene fra kapittel 2 |
| `AiSummary` | string? | oppsummering fra modellen |
| `AiActions` | jsonb? | tiltakene fra modellen |
| `AiStatus` | string | `ok`, `disabled` eller `failed` |
| `CreatedAt` | DateTime (UTC) | |
| `CreatedBy` | string | `system` for den ukentlige jobben, ellers innlogget bruker |

**Tjenester:**
- `OpsReportService`: beregner nøkkeltall for en tenant og periode, kaller `IOpsReportWriter`, lagrer rapporten og legger Teams-varsel i køen.
- `OpsReportMonitorService` (BackgroundService, samme mønster som `SlaMonitorService`): sjekker hver time om det er mandag etter `OpsReport:Hour` (standard 7, Europe/Oslo) og om rapport for forrige uke mangler, per tillatt tenant. Én tenant som feiler stopper ikke de andre.
- Konfigurasjon: `OpsReport:Enabled` (standard false), `OpsReport:Hour`.

**API:** `OpsReportsController`

| Endepunkt | Tilgang |
|---|---|
| `GET /api/opsreports` (de siste 12) | `HelpdeskAgent` |
| `GET /api/opsreports/{id}` | `HelpdeskAgent` |
| `POST /api/opsreports/generate` (forrige uke, overskriver ikke en eksisterende) | `HelpdeskAdmin` |

**Teams:** nytt kort `TeamsCards.ForOpsReport` med de viktigste tallene, AI-oppsummeringen og lenke til rapporten i appen. Sendes gjennom den eksisterende køen i `TeamsNotificationService` med type `ops_report` og uten sak.

## 6. Frontend

Ny side **Driftsrapport** under drift: liste over ukene til venstre, valgt rapport til høyre med nøkkeltall som kort, AI-oppsummering og tiltak. Knappen **Lag rapport nå** for `Helpdesk.Admin`. Merket «Skrevet av AI» ved AI-delen. Tekster i `i18n/en/operations.ts`.

## 7. Infrastruktur (Terraform)

I `infra/terraform/main.tf`:

- `azurerm_cognitive_account`: `kind = "AIServices"`, `sku_name = "S0"`, `location = var.ai_location` (standard `swedencentral`), `custom_subdomain_name`, `local_auth_enabled = false`.
- `azurerm_cognitive_deployment`: `gpt-4.1-mini`, versjon `2025-04-14`, `sku.name = var.ai_sku` (standard `DataZoneStandard`), `capacity = 20`.
- `azurerm_role_assignment`: **Cognitive Services OpenAI User** til service principal for API-app-registreringen.
- App-innstillinger på web-appen: `Ai__Enabled`, `Ai__Endpoint`, `Ai__Deployment`, `OpsReport__Enabled`.
- Budsjettet dekker ressursgruppen fra før. Forventet kostnad er under 1 kr i måneden ved én rapport i uka.

Lokalt: sett `Ai:Endpoint` og `Ai:Deployment` i user secrets. Innloggingen bruker sertifikatet appen allerede har.

## 8. Tester

- Nøkkeltall beregnes riktig for en uke med kjente saker, varsler og bestillinger (SQLite).
- Tenant-isolasjon: rapporten i én tenant teller aldri data fra en annen.
- Forespørselen til modellen inneholder ikke navn eller e-post fra testdataene.
- AI feiler eller er slått av: rapporten lagres med `AiStatus` `failed` eller `disabled` og sendes likevel.
- Jobben lager ikke to rapporter for samme uke, heller ikke ved omstart.
- Tilgang: `HelpdeskAgent` kan lese, bare `HelpdeskAdmin` kan generere.
- Teams-varsel legges i køen med riktig type.

## 9. Rekkefølge

1. Modell, migrering og `OpsReportService` med nøkkeltall, uten AI. Tester.
2. API og frontend-side.
3. `IOpsReportWriter` mot Foundry og Terraform for ressursen. Tester med falsk writer.
4. Bakgrunnsjobb og Teams-kort.
5. Kostnader fra Cost Management (eget steg, egen rolle).

## 10. Status

**Steg 1 (2026-11-25): ferdig.** Migreringen `AddOpsReports` er kjørt i den lokale dev-databasen.
- `OpsReport` med unik indeks på (`TenantId`, `PeriodStart`). `OpsReportService` beregner nøkkeltallene for forrige uke (mandag 00:00 til mandag 00:00 norsk tid) og lagrer rapporten med `AiStatus` `disabled`. Finnes rapporten, lages ingen ny.
- SLA-brudd regnes fra fristene (som `SlaEvaluator`), ikke fra `ResponseBreachedAt`, slik at svar som kom for sent mellom to kjøringer av SLA-jobben også telles.
- «Gjenåpnet varsel» = varsel fra før perioden som fyrte igjen i perioden (`LastFiredAt`). En varselkilde som ikke er hentet ennå, regnes ikke som feil.
- Feiler Graph, lagres rapporten med inaktive gjester tomt og en årsak.
- Tester: `OpsReportTests` (6).

**Steg 2 (2026-11-26): bygget.** Ingen ny migrering.
- `OpsReportsController` som i kapittel 5. `generate` gir 201 for en ny rapport og 200 med den eksisterende.
- Siden **Driftsrapport** rett etter Varsler: ukene til venstre, nøkkeltall som kort til høyre, AI-delen med merket «Skrevet av AI» når den finnes. Knappen **Lag rapport nå** for `Helpdesk.Admin`.
- Tester: `OpsReportsControllerTests` (3) og nye rader i `AuthorizationTests`.

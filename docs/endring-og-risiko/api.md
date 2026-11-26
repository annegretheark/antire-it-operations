# API

Alle endepunkter krever innlogging fra en tillatt tenant. «Agent eller rolle» betyr app-rollen Helpdesk.Agent/Helpdesk.Admin **eller** en endringsrolle. Feil gir `{ "message": "…" }` med 400, 403, 404, 409 eller (AI) 503.

## Endringer

| Endepunkt | Tilgang | Beskrivelse |
|---|---|---|
| `GET /api/changes` | Alle (uten tilgang: bare egne) | Liste; filter `status`, `type`, `environment`, `mine` |
| `GET /api/changes/options` | Agent eller rolle | Tjenester, abonnementer, aktive maler og ansatte til skjemaene |
| `GET /api/changes/{id}` | Agent, rolle eller innmelder | Detaljer, godkjenninger (gyldig/ugyldig), regler som mangler, hva *du* kan gjøre |
| `POST /api/changes` | Agent eller Change Requester | Nytt utkast (standard: `templateId`) |
| `PUT /api/changes/{id}` | Innmelder, Change Manager, Technical Owner | Endre før implementering; endret godkjent innhold gir ny godkjenning |
| `POST /api/changes/{id}/transitions` | Etter rolle per overgang | `{ to, note }` |
| `POST /api/changes/{id}/decisions` | Påkrevd rolle, ikke innmelder/implementør | `{ role, decision, comment, expectedScopeHash }`; feil hash gir 409 |
| `POST /api/changes/{id}/post-review` | Reviewer eller Change Manager, ikke implementør | Etterkontroll av hasteendring |
| `GET /api/changes/{id}/history` | Som detaljer | Revisjonslogg |
| `POST /api/changes/{id}/delivery/link` | Innmelder, Technical Owner, Implementer | PR, commit, pipeline (simulert) |
| `POST /api/changes/{id}/delivery/plan` | Samme | Simulert Terraform-plan |
| `GET /api/changes/{nummer}/deployment-gate` | Agent eller rolle | Sjekk før Apply; produksjon avvises alltid |
| `POST /api/changes/{id}/delivery/simulate-apply` | Implementer | Simulert Apply i utvikling/test |

## Risiko

| Endepunkt | Tilgang | Beskrivelse |
|---|---|---|
| `GET /api/risks` | Agent eller rolle | Filter `category`, `status`, `level`, `ownerEmployeeId`, `unowned`, `overdue`, `awaitingAcceptance` |
| `GET /api/risks/matrix` | Agent eller rolle | 5×5 før og etter tiltak |
| `GET /api/risks/{id}`, `…/history` | Agent eller rolle | Detaljer og revisjonslogg |
| `POST /api/risks`, `PUT /api/risks/{id}` | Registrere: agent eller rolle. Endre: eier, Risk Owner, Security Officer | |
| `POST /api/risks/{id}/transitions` | Eier/Risk Owner/Security Officer; tiltaksansvarlig til «overvåkes» | |
| `POST /api/risks/{id}/acceptances` | Utpekt eier med rollen Risk Owner | `{ justification, validUntil, expectedScore }`; maks 12 måneder |
| `POST /api/risks/{id}/acceptances/revoke` | Samme | Ny rad |
| `PUT /api/risks/{id}/links` | Som endre | Endringer, tjenester, saker, varsler |

## Styring, AI og demodata

| Endepunkt | Tilgang |
|---|---|
| `GET /api/governance/me` | Alle |
| `GET/POST/DELETE /api/governance/roles` | Helpdesk.Admin (ikke til seg selv) |
| `GET /api/governance/raci`, `PUT …` | Les: alle. Endre: Helpdesk.Admin |
| `GET /api/governance/approval-rules`, `POST/PUT/DELETE …` | Les: alle. Endre: Helpdesk.Admin |
| `GET/PUT /api/governance/settings` | Helpdesk.Admin |
| `GET /api/governance/dashboard` | Agent eller rolle |
| `GET/POST/DELETE /api/governance/demo-data` | Helpdesk.Admin, bare med `Demo:Enabled` i utviklingstenanten |
| `GET /api/changetemplates`, `POST/PUT …`, `POST …/{id}/approve` | Les: agent eller rolle. Endre: Helpdesk.Admin. Forhåndsgodkjenne: global Change Manager |
| `GET /api/ai/status`, `POST /api/ai/changes/{id}/assist`, `POST /api/ai/changes/draft`, `POST /api/ai/risks/summary` | Agent eller rolle (og bare endringer du kan se) |

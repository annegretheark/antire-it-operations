# Roller, RACI og godkjenningsregler

## Roller

Change Requester, Change Manager, Technical Owner, System Owner, Risk Owner, Security Officer, GDPR Responsible, Approver, Implementer og Reviewer.

- En rolle tildeles en **ansatt** eller en **Entra-gruppe**, globalt eller for én tjeneste (Innstillinger → Endringsroller).
- Rollene slås opp på nytt ved hver forespørsel. Medlemskap i grupper sjekkes mot Graph; svarer ikke Graph, gis ingen rolle via gruppen.
- Helpdesk.Admin kan ikke tildele roller til seg selv eller til en gruppe de er medlem av.

## Standard RACI-matrise

R = Responsible, A = Accountable (én per aktivitet), C = Consulted, I = Informed. Kan endres under Innstillinger → RACI.

| Aktivitet | Requester | Change Mgr | Technical Owner | System Owner | Risk Owner | Security | GDPR | Approver | Implementer | Reviewer |
|---|---|---|---|---|---|---|---|---|---|---|
| Melde inn endring | R | A | C | I | | | | | | |
| Vurdere risiko for endring | | A | R | | C | C | C | | | |
| Teknisk gjennomgang | | A | R | C | | | | | I | |
| Godkjenne produksjonsendring | I | A | | R | | C | | R | I | |
| Implementere endring | | I | A | I | | | | | R | |
| Verifisere endring | I | A | | I | | | | | | R |
| Etterkontroll av hasteendring | | A | | I | | C | | | | R |
| Akseptere risiko | | I | | | A | C | C | | | |
| Behandle risiko | | | R | I | A | C | | | | |

## Standard godkjenningsregler

Kan endres under Innstillinger → Godkjenningsregler.

| Type | Miljø | Må godkjennes av |
|---|---|---|
| Normal | Produksjon | Change Manager **og** System Owner |
| Normal | Test | Change Manager |
| Normal | Utvikling | Technical Owner |
| Emergency | Alle | Change Manager **og** Approver |
| Standard | Alle | Ingen utover den forhåndsgodkjente malen |

Produksjon krever alltid minst Change Manager, også om reglene er slettet. Dashboardet varsler når en rolle som reglene krever, ikke er tildelt noen.

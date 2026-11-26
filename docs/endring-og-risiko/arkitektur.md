# Arkitektur

Modulene er bygget inn i den eksisterende appen, ikke som egne tjenester.

```mermaid
flowchart LR
  B[Nettleser<br/>React] -->|Entra-token| API[ASP.NET Core API]
  API --> DB[(PostgreSQL<br/>tenant-filter)]
  API --> AUD[Revisjonslogg<br/>AuditInterceptor]
  API -->|on-behalf-of| G[Microsoft Graph]
  API -->|AppCredentials| AI[Azure OpenAI i Foundry<br/>Sweden Central, EU]
  API --> Q[Teams-kø] -->|Workflows-webhook| T[Teams-kanal]
  J[GovernanceMonitorService<br/>hver time] --> DB
  J --> Q
  P[Pipeline (eksempel)] -.->|deployment-gate| API
```

| Komponent | Fil | Rolle |
|---|---|---|
| Arbeidsflyt for endringer | `Services/ChangeWorkflowService.cs` | Overganger, godkjenning, separasjon av plikter, standard og hasteendring |
| Hash av godkjent innhold | `Services/ChangeScopeHash.cs` | SHA-256 av det godkjenningen gjelder |
| Roller | `Services/GovernanceRoleService.cs` | Endringsroller fra ansatt og Entra-grupper |
| Risikoregister | `Services/RiskRegisterService.cs` | Statuser, aksept, koblinger |
| Leveranse (simulert) | `Services/ChangeDeliveryService.cs` | Plan, sjekk før Apply, simulert Apply |
| Dashboard | `Services/GovernanceDashboardService.cs` | Nøkkeltall |
| Teams-kort | `Services/GovernanceNotifier.cs` | Kort gjennom den eksisterende køen |
| Bakgrunnsjobb | `Services/GovernanceMonitorService.cs` | Påminnelser, frister, utløpt aksept |
| AI | `Services/AiClient.cs`, `Services/AiAssistService.cs` | Foundry-klient, vasking, forslag |
| Demodata | `Services/GovernanceDemoSeeder.cs` | Syntetiske data i utviklingstenanten |
| Frontend | `frontend/src/components/Changes.tsx`, `Risks.tsx`, `GovernanceDashboard.tsx`, `settings/GovernanceSettings.tsx` | Sider og innstillinger |

**Prinsipper**

- Alt avgjøres på serveren. Klienten ber om en handling; tjenestene sjekker identitet, rolle, status og innhold.
- Alle data er tenant-skilt (`ITenantOwned` og filter i `AppDbContext`).
- Alle endringer logges i revisjonsloggen. Beslutninger (godkjenninger og risikoaksept) kan bare legges til.
- Integrasjoner som kan gjøre skade (GitHub, Terraform, ekte Teams-knapper) er simulert eller finnes ikke.

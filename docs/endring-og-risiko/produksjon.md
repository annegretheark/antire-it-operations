# Forutsetninger for eventuell produksjonssetting

Modulene er en **prototype** i utviklingstenanten med syntetiske data. Ingenting er koblet til Antires produksjonsmiljø, Entra ID, Azure-abonnementer eller GitHub-repositorier.

## Må være på plass før videre arbeid mot produksjon

| Område | Hva som kreves |
|---|---|
| Mandat | Skriftlig mandat for å ta modulene i bruk |
| Eierskap | Avklart systemeier og risikoeier for løsningen, og ansvarsfordeling (RACI) godkjent av ledelsen |
| Sikkerhet | Sikkerhetsgodkjenning og en gjennomgang av tilgangsmodellen og separasjon av plikter |
| Testmiljø | Eget testmiljø (egen tenant eller eget abonnement) mellom utvikling og produksjon |
| Personvern | DPIA og oppføring i behandlingsprotokollen for AI-bruken; avtale og databehandling for Foundry (Data Zone EU) |
| Roller | Ekte personer og grupper i endringsrollene; Change Manager og System Owner må finnes før produksjonsendringer |

## Teknisk arbeid som gjenstår

- **Teams:** ekte kanal i en tenant med lisens; eventuell bot (Bot Framework) for knapper som virker i selve kortet.
- **GitHub og Terraform:** ekte leverandør bak `IChangeDeliveryProvider`, pipelinens egen identitet (GitHub OIDC med federated credential), beskyttede miljøer med påkrevde godkjennere, og Policy as Code mot planen. Sjekken før Apply må slippe gjennom produksjon først når dette er godkjent.
- **Oppbevaring:** regel og jobb for arkivering eller anonymisering av lukkede endringer og risikoer.
- **AI:** gjennomgang av instruksjonene og vaskingen, og overvåking av kostnad; `ai_enabled` er `false` i Azure i dag.

## Av i dag (standard)

| Funksjon | Innstilling |
|---|---|
| AI | `Ai:Enabled = false` (Azure: `ai_enabled = false`) |
| Demodata | `Demo:Enabled = false`, og bare utviklingstenanten |
| Leveranse | Bare `Delivery:Provider = mock`; produksjon avvises alltid |
| Teams-kort | På, men sendes bare når en webhook er satt opp |

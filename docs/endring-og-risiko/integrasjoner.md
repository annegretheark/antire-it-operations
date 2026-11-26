# Integrasjoner

## Microsoft Teams

- Kort sendes gjennom den eksisterende køen (`TeamsNotificationService`) til en Workflows-webhook per tenant, med nye forsøk. Feil i Teams stopper aldri arbeidsflyten.
- **Kort:** venter på godkjenning (med roller som mangler), ber om informasjon, statusendringer, kommende produksjonsendringer, manglende etterkontroll, risiko ikke behandlet, tiltak forbi frist, aksept som utløper og utløpt aksept.
- **Innhold:** bare nummer, tittel, type, miljø, score, status, tidspunkt og rollenavn. Aldri beskrivelser, planer, analyser eller begrunnelser.
- **Knapper:** bare lenker inn i appen (`?endring=`, `?risiko=`, `&historikk=1`). Godkjenning skjer i appen, der serveren sjekker identitet og rolle.
- Kan slås av under Innstillinger → Teams. Ekte Teams er ikke prøvd (utviklingstenanten har ikke Teams-lisens). En bot med knapper som virker i kortet krever Bot Framework og lisens.

## Microsoft Foundry (AI)

- `FoundryAiClient` kaller `POST {Ai:Endpoint}/openai/v1/chat/completions` med `AppCredentials` (rollen Cognitive Services OpenAI User). Modell `gpt-4.1-mini` som Data Zone Standard (EU) i Sweden Central.
- **Av som standard** (`Ai:Enabled`). Maks 20 forespørsler per bruker per time. Instruksjonene ligger i `backend/Prompts/`.
- **Sendes:** tekstfeltene i endringer og risikoer og kategorier, etter vasking (`AiTextScrubber`: e-post, GUID-er, ressurs-ID-er og navn på ansatte byttes ut). Driftsrapporten sender bare aggregerte nøkkeltall.
- **Sendes aldri:** navn, e-post, objekt-ID-er, sakstekster, varseltitler, ressurs-ID-er, abonnementsnavn.
- **Agenten gir bare forslag.** Den har ingen verktøy, skrivetilgang eller roller, og kan ikke godkjenne, akseptere risiko, endre status eller gi rettigheter. Bare tillatte felt fra svaret tas med, og brukeren lagrer selv. Revisjonsloggen får oppgave og nummer, ikke innhold.

## GitHub og Terraform (simulert)

- `IChangeDeliveryProvider` har bare `MockDeliveryProvider`: ingen kall ut av appen.
- En endring kobles til PR (`https://github.com/…`), commit-SHA og pipeline, og får en simulert Terraform-plan med hash. Ny commit eller plan gjør godkjenningen ugyldig.
- `GET /api/changes/{nummer}/deployment-gate` svarer ja bare når endringen er under implementering med gyldig godkjenning, riktig miljø, og samme commit og plan som ble godkjent. **Produksjon avvises alltid.** Hver sjekk logges.
- Eksempel på arbeidsflyt: [`docs/examples/deploy-with-change-gate.yml`](../examples/deploy-with-change-gate.yml) (kjører ikke). Slik er det tenkt koblet: PR med endringsnummer → plan og Policy as Code → godkjenning i appen → beskyttet miljø i GitHub med påkrevde godkjennere → pipelinens egen identitet (GitHub OIDC) kaller sjekken før Apply.

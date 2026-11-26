# Sikkerhetsmodell

## Tilgang (RBAC og least privilege)

- **Innlogging:** Entra ID-token fra en tillatt tenant (`Tenants:Allowed`). Alle data er tenant-skilt i databasen.
- **To lag:** app-rollene (Helpdesk.Agent, Helpdesk.Admin) gir tilgang til modulene; endringsrollene (i databasen) bestemmer hvem som kan gjøre hva i prosessen. Konfigurasjon krever Helpdesk.Admin.
- Personer uten Helpdesk.Agent og uten endringsrolle ser bare egne endringer, og ingenting røper at andre finnes (404).

## Separasjon av plikter

Innmelder og implementør kan ikke godkjenne; én person teller for én regel; den som implementerte, kan ikke verifisere eller gjøre etterkontrollen; kritiske risikoer kan ikke aksepteres av den som registrerte dem; Helpdesk.Admin kan ikke tildele seg selv roller.

## Serversidekontroll

Klienten ber bare om handlinger. Status, rolle, vilkår og innhold sjekkes i tjenestene. Godkjenning og aksept krever at klienten sender hashen eller scoren den så, så ingen kan godkjenne innhold de ikke har sett. Teams-kort og AI-svar behandles aldri som kommandoer.

## Revisjonslogg og beslutninger

- Hver opprettelse, endring og sletting logges med aktør (Entra objekt-ID og navn, eller `system`), tidspunkt, handling og felt med før- og etterverdi.
- Revisjonsloggen, godkjenninger og risikoaksept kan ikke endres eller slettes (`AuditInterceptor`). En ny beslutning er alltid en ny rad.
- Personopplysninger kan vaskes ut av revisjonsloggen ved anonymisering (GDPR-modulen); radene beholdes.

## Hemmeligheter og innlogging mot tjenester

- Ingen API-nøkler. Graph, Azure og Foundry bruker `AppCredentials`: sertifikat lokalt, federert managed identity i Azure. Foundry har `local_auth_enabled = false`.
- Deaktivering og sletting av brukere bruker personens egne rettigheter (on-behalf-of).
- Teams-webhooken lagres kryptert med Data Protection. Passord for testbrukere lagres utenfor repoet.

## Oppbevaring

`GovernanceSettings.RetentionMonths` er satt av til arkivering eller anonymisering av lukkede endringer og risikoer (regel og jobb er ikke bygget i prototypen). Revisjonsloggen slettes aldri.

## Prototype

Bare utviklingstenanten, bare syntetiske data. AI og demodata er av som standard. GitHub og Terraform er simulert, og produksjon avvises alltid av sjekken før Apply.

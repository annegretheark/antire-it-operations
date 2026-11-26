## Tenanter
- Utvikling: 3af5c92b-38f9-48ce-8746-82bb4c113032. All testing skjer her.
- Antire: 94f56205-4854-45cd-b54c-3383e8abfb3f. Skal IKKE røres før jeg sier fra.
- Kjør `az account show` og `Get-MgContext` før kommandoer mot Entra, Graph eller Azure,
  og stopp hvis tenanten ikke er 3af5c92b.
- Ikke endre AzureAd:TenantId, Tenants:Allowed eller VITE_AUTHORITY uten at jeg ber om det.
## Planer
- UTVIDELSESPLAN.md er hovedplanen. FASE5_DRIFTSRAPPORT.md beskriver driftsrapporten med AI.
  ENDRING_OG_RISIKO.md beskriver Change Management og Risk Management (prototype).
- Ta én fase eller ett steg om gangen, og vis planen før du endrer kode.

## AI
- Send aldri personopplysninger til AI-modellen: ingen navn, e-post, sakstekster,
  varseltitler eller ressurs-ID-er. Bare aggregerte tall og kategorinavn.
- Unntak, bare for Change and Risk-agenten (ENDRING_OG_RISIKO.md): den kan få tekstfeltene
  i endringer og risikoer (tittel, beskrivelse, planer, analyser) og kategorier. Aldri navn,
  e-post, objekt-ID-er, sakstekster, varseltitler eller ressurs-ID-er; personer erstattes med
  rollen sin. Bare i utviklingstenanten med syntetiske data, og Ai:Enabled er av som standard.
- AI kan bare foreslå. Den kan aldri godkjenne, akseptere risiko, endre status eller rettigheter.
- Tall i rapporter beregnes av koden, ikke av modellen.
- Ingen API-nøkler: Foundry-kall går via AppCredentials.

## Før commit
- Kjør `dotnet test` og sjekk at alle tester er grønne.
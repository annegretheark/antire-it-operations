// Innstillinger: Settings.tsx og components/settings/*
export const settings: Record<string, string> = {
  // Faner og tilgang
  'Du trenger rollen Helpdesk.Admin for å se innstillingene.': 'You need the Helpdesk.Admin role to view the settings.',
  'Tjenestekatalog': 'Service catalog',
  'Ansvarsområder': 'Responsibilities',
  'Kategorigrupper': 'Category groups',
  'SLA': 'SLA',
  'Varselregler': 'Alert rules',
  'Teams': 'Teams',
  'Onboarding-maler': 'Onboarding templates',
  'Pålogginger': 'Sign-ins',
  'Revisjonslogg': 'Audit log',

  // Felles
  'Kunne ikke hente data.': 'Could not load data.',
  'Lagringen feilet. Prøv igjen.': 'Saving failed. Please try again.',
  'Noe gikk galt.': 'Something went wrong.',
  'Lagre': 'Save',
  'Lagrer...': 'Saving...',
  'Lagre endringer': 'Save changes',
  'Legg til': 'Add',
  'Avbryt': 'Cancel',
  'Endre': 'Edit',
  'Fjern': 'Remove',
  'Endre «{name}»': 'Edit “{name}”',
  'Velg kategori': 'Select category',
  'Velg gruppe eller lim inn gruppe-ID': 'Select a group or paste a group ID',
  'Tidspunkt': 'Time',
  'Hva': 'What',
  'Ja': 'Yes',
  'Nei': 'No',

  // Revisjonslogg
  'Kunne ikke hente revisjonsloggen.': 'Could not load the audit log.',
  'Alle endringer på saker, tilgangsbestillinger, ansatte og konfigurasjon i denne tenanten, med hvem og når. Loggen kan ikke endres eller slettes.':
    'All changes to tickets, access requests, employees and configuration in this tenant, with who and when. The log cannot be changed or deleted.',
  'Vis:': 'Show:',
  'Alt': 'Everything',
  'Ingen hendelser registrert.': 'No events recorded.',
  'Hvem': 'Who',
  'Hendelse': 'Event',
  'Endringer': 'Changes',
  'Henter hendelser...': 'Loading events...',
  'Vis eldre hendelser': 'Show older events',

  // Varselregler
  'Regelen for {source} er lagret.': 'The rule for {source} has been saved.',
  'Varsler som er minst så alvorlige som terskelen, blir saker. Mindre alvorlige lagres under Varsler uten sak. Kategorien styrer automatisk tildeling. Prioriteten følger alvorlighetsgraden (Azure Sev0 kritisk, Sev1 høy, Sev2 normal; Defender høy → høy, middels → normal), med mindre du velger en fast prioritet.':
    'Alerts at least as severe as the threshold become tickets. Less severe alerts are stored under Alerts without a ticket. The category controls automatic assignment. The priority follows the severity (Azure Sev0 critical, Sev1 high, Sev2 normal; Defender high → high, medium → normal), unless you choose a fixed priority.',
  'Henter regler...': 'Loading rules...',
  'Etter alvorlighetsgrad': 'By severity',
  'Alltid {priority}': 'Always {priority}',
  'Lag saker fra varsler': 'Create tickets from alerts',
  'Varsle Teams om nye og gjenåpnede saker': 'Notify Teams about new and reopened tickets',

  // Pålogginger
  'Hvem som har brukt appen i denne tenanten, og når. Oppdateres når brukeren åpner appen (høyst hvert 5. minutt). Et nytt besøk regnes når det har gått mer enn 30 minutter siden sist. Personer som blir stoppet før de kommer inn, for eksempel fordi de logger inn fra en annen tenant, vises ikke her; se påloggingsloggen i Entra ID for dem.':
    'Who has used the app in this tenant, and when. Updated when the user opens the app (at most every 5 minutes). A new visit is counted when more than 30 minutes have passed since the last one. People who are stopped before getting in, for example because they sign in from another tenant, are not shown here; see the sign-in log in Entra ID for them.',
  'Søk': 'Search',
  'Søk på navn eller brukernavn': 'Search by name or username',
  'Oppdater': 'Refresh',
  'Henter pålogginger...': 'Loading sign-ins...',
  'Ingen pålogginger registrert.': 'No sign-ins recorded.',
  '{n} brukere': '{n} users',
  'Bruker': 'User',
  'Første gang': 'First time',
  'Besøk': 'Visits',

  // Kategorigrupper
  'En sak i en kategori tildeles bare ansatte som er medlem av en av gruppene for kategorien (direkte eller via en annen gruppe). Uten grupper tildeles ikke saker i kategorien automatisk.':
    'A ticket in a category is only assigned to employees who are members of one of the groups for the category (directly or via another group). Without groups, tickets in the category are not assigned automatically.',
  'Entra-gruppe-ID': 'Entra group ID',
  'Henter kategorigrupper...': 'Loading category groups...',
  'Ingen kategorigrupper registrert.': 'No category groups registered.',

  // Onboarding-maler
  'På startdato': 'On start date',
  '{n} dager før startdato': '{n} days before start date',
  '{n} dager etter startdato': '{n} days after start date',
  'På sluttdato': 'On end date',
  '{n} dager før sluttdato': '{n} days before end date',
  '{n} dager etter sluttdato': '{n} days after end date',
  'Oppgavene som legges inn når onboarding eller offboarding starter. Fristen regnes fra startdato (onboarding) eller sluttdato (offboarding). Endringer gjelder nye prosesser.':
    'The tasks added when onboarding or offboarding starts. The due date is calculated from the start date (onboarding) or end date (offboarding). Changes apply to new processes.',
  'Ny oppgave': 'New task',
  'Frist (dager, minus = før)': 'Due (days, minus = before)',
  'Ansvarlig område (valgfritt)': 'Responsible area (optional)',
  'Område': 'Area',
  'av': 'off',
  'Slå av': 'Turn off',
  'Slå på': 'Turn on',

  // Ansvarsområder
  'Ansatte med et ansvarsområde kan få nye saker i kategorien automatisk, så sant de også er medlem av en av kategorigruppene i Entra ID.':
    'Employees with a responsibility can be assigned new tickets in the category automatically, provided they are also a member of one of the category groups in Entra ID.',
  'Velg ansatt': 'Select employee',
  'Henter ansvarsområder...': 'Loading responsibilities...',
  'Ingen ansvarsområder registrert.': 'No responsibilities registered.',
  'Automatisk tildeling': 'Automatic assignment',
  'Sluttet': 'Left',
  'På': 'On',
  'Av': 'Off',

  // Tjenestekatalog
  'Tjenesten er lagt til.': 'The service has been added.',
  'Tjenesten er oppdatert.': 'The service has been updated.',
  'Tjenestene brukerne velger når de melder inn en sak eller bestiller tilgang. Kategorien styrer automatisk tildeling sammen med ansvarsområder og kategorigrupper.':
    'The services users choose when they report a ticket or request access. The category controls automatic assignment together with responsibilities and category groups.',
  'Ny tjeneste': 'New service',
  'Entra-gruppe-ID (gir tilgangen)': 'Entra group ID (grants the access)',
  'Aktiv (vises for brukerne)': 'Active (shown to users)',
  'Henter tjenester...': 'Loading services...',
  'Godkjenning': 'Approval',
  'Deaktiver': 'Deactivate',
  'Aktiver': 'Activate',

  // SLA
  '{n} arbeidsdag': '{n} working day',
  '{n} arbeidsdager': '{n} working days',
  'SLA for {priority} prioritet er lagret.': 'SLA for {priority} priority has been saved.',
  'Frister for første respons og løsning per prioritet. Arbeidstid er 08:00–16:00 på virkedager, uten norske helligdager. Med «døgnet rundt» løper fristen også kveld, helg og helligdag. Endringer gjelder nye saker og saker som får ny prioritet.':
    'Deadlines for first response and resolution per priority. Working hours are 08:00–16:00 on weekdays, excluding Norwegian public holidays. With “24/7” the deadline also runs in the evening, at weekends and on public holidays. Changes apply to new tickets and tickets that get a new priority.',
  'Henter SLA-mål...': 'Loading SLA targets...',
  'Respons (minutter)': 'Response (minutes)',
  'Løsning (minutter)': 'Resolution (minutes)',
  'Responstid for {priority}': 'Response time for {priority}',
  'Løsningstid for {priority}': 'Resolution time for {priority}',
  'Døgnet rundt for {priority}': '24/7 for {priority}',

  // Teams
  'Ny sak fra varsel': 'New ticket from alert',
  'Sak gjenåpnet av varsel': 'Ticket reopened by alert',
  'Ny kritisk sak': 'New critical ticket',
  'Venter (prøves igjen)': 'Pending (will retry)',
  'Sendt': 'Sent',
  'Feilet': 'Failed',
  'Teams-oppsettet er lagret.': 'The Teams settings have been saved.',
  'Webhook-adressen er fjernet. Teams varsles ikke lenger.': 'The webhook URL has been removed. Teams will no longer be notified.',
  'Lag en arbeidsflyt i Teams-kanalen:': 'Create a workflow in the Teams channel:',
  '«Post to a channel when a webhook request is received», og lim inn adressen den gir. Adressen er en hemmelighet: den lagres kryptert og vises bare forkortet her.':
    '“Post to a channel when a webhook request is received”, and paste the URL it gives you. The URL is a secret: it is stored encrypted and only shown shortened here.',
  'Henter oppsett...': 'Loading settings...',
  'Satt opp': 'Configured',
  'Ikke satt opp. Saker lages som vanlig, men Teams varsles ikke.': 'Not configured. Tickets are created as usual, but Teams is not notified.',
  'Ny webhook-adresse': 'New webhook URL',
  'Varsle også ved nye manuelle saker med prioritet kritisk': 'Also notify on new manual tickets with critical priority',
  'Send testmelding': 'Send test message',
  'Fjern adressen': 'Remove the URL',
  'Siste meldinger': 'Recent messages',
  'forsøk {n}': 'attempt {n}',
}

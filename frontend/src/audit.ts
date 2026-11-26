import { PRIORITY_LABELS, STATUS_LABELS } from './tickets'
import { ASSET_STATUS_LABELS, ASSET_TYPE_LABELS } from './assets'
import { SERVICE_TYPE_LABELS } from './serviceCatalog'
import { locale, t } from './i18n'

// Felles typer og visning for revisjonsloggen (sakshistorikk og Innstillinger)

export type AuditEvent = {
  id: number
  occurredAt: string
  actorObjectId: string
  actorName: string
  entityType: string
  entityId: string
  action: string
  // { "Felt": [før, etter] } for endringer, eller fritt innhold (f.eks. synk)
  changes: Record<string, unknown> | null
}

const ACTION_LABELS: Record<string, string> = {
  created: 'Opprettet',
  updated: 'Endret',
  deleted: 'Slettet',
  approved: 'Godkjent',
  rejected: 'Avslått',
  completed: 'Fullført',
  synced: 'Synkronisert',
  sla_breached: 'SLA-brudd',
  access_granted: 'Tilgang gitt i Entra',
  access_grant_failed: 'Tilgang feilet i Entra',
  alert_recurred: 'Varselet fyrte igjen',
  alert_resolved: 'Varselet er løst hos kilden',
  reopened_by_alert: 'Gjenåpnet av varsel',
  lifecycle_started: 'Startet',
  access_revoked: 'Tilgang fjernet',
  gdpr_exported: 'Innsyn lastet ned',
  gdpr_anonymized: 'Anonymisert',
  user_disabled: 'Konto deaktivert',
  user_enabled: 'Konto aktivert',
  user_deleted: 'Slettet i Entra',
}

export const ENTITY_LABELS: Record<string, string> = {
  Ticket: 'Sak',
  AccessRequest: 'Tilgangsbestilling',
  Employee: 'Ansatt',
  EmployeeResponsibility: 'Ansvarsområde',
  TicketCategoryGroup: 'Kategorigruppe',
  EmployeeSync: 'Synk fra Entra',
  ServiceCatalogItem: 'Tjeneste',
  AzureSubscription: 'Azure-abonnement',
  AzureSubscriptionSync: 'Synk fra Azure',
  EntraUser: 'Bruker i Entra',
  Alert: 'Varsel',
  AlertRule: 'Varselregel',
  TeamsSettings: 'Teams-oppsett',
  LifecycleProcess: 'Onboarding/offboarding',
  LifecycleTask: 'Oppgave',
  LifecycleTemplateTask: 'Onboarding-mal',
  ProcessingActivity: 'Behandlingsprotokoll',
  GdprSettings: 'GDPR-innstillinger',
  Asset: 'Utstyr',
}

const FIELD_LABELS: Record<string, string> = {
  Status: 'Status',
  Priority: 'Prioritet',
  AssignedToEmployeeId: 'Ansvarlig',
  ReportedByEmployeeId: 'Meldt inn av',
  EmployeeId: 'Ansatt',
  Category: 'Kategori',
  Description: 'Beskrivelse',
  CreatedAt: 'Opprettet',
  ApprovedAt: 'Godkjent',
  EmployeeName: 'Ansatt',
  SystemName: 'System',
  Reason: 'Begrunnelse',
  ManagerName: 'Leder',
  RequestedByObjectId: 'Bestilt av (objekt-ID)',
  FirstName: 'Fornavn',
  LastName: 'Etternavn',
  Email: 'E-post',
  Department: 'Avdeling',
  JobTitle: 'Stilling',
  StartDate: 'Startdato',
  EndDate: 'Sluttdato',
  EmployeeNumber: 'Ansattnummer',
  EntraObjectId: 'Entra objekt-ID',
  EntraGroupId: 'Entra-gruppe',
  CanBeAssigned: 'Kan tildeles',
  ServiceCatalogItemId: 'Tjeneste',
  Name: 'Navn',
  Type: 'Type',
  DefaultPriority: 'Standard prioritet',
  RequiresApproval: 'Krever godkjenning',
  IsActive: 'Aktiv',
  ResponseDueAt: 'Svarfrist',
  ResolutionDueAt: 'Løsningsfrist',
  FirstRespondedAt: 'Første respons',
  ResolvedAt: 'Løst',
  ResponseBreachedAt: 'Svarfrist brutt',
  ResolutionBreachedAt: 'Løsningsfrist brutt',
  ResponseMinutes: 'Responstid (min)',
  ResolutionMinutes: 'Løsningstid (min)',
  TwentyFourSeven: 'Døgnet rundt',
  AssetId: 'Utstyr',
  AssetTag: 'Utstyrsnummer',
  Manufacturer: 'Produsent',
  Model: 'Modell',
  SerialNumber: 'Serienummer',
  PurchaseDate: 'Kjøpsdato',
  WarrantyEndsAt: 'Garanti til',
  Notes: 'Notater',
  Source: 'Kilde',
  ExternalId: 'Ekstern ID',
  SubscriptionId: 'Abonnements-ID',
  DisplayName: 'Navn',
  State: 'Tilstand',
  IsVisible: 'Synlig for appen',
  FirstSeenAt: 'Først sett',
  LastSeenAt: 'Sist sett',
  added: 'Nye',
  hidden: 'Ikke lenger synlige',
  total: 'Totalt',
  AlertId: 'Varsel',
  alertId: 'Varsel',
  occurrences: 'Forekomster',
  Occurrences: 'Forekomster',
  Title: 'Tittel',
  Severity: 'Alvorlighetsgrad',
  ResourceId: 'Ressurs',
  PortalUrl: 'Lenke',
  LastFiredAt: 'Sist fyrt',
  RawPayload: 'Rådata',
  TicketId: 'Sak',
  MinSeverity: 'Sak fra og med',
  PriorityOverride: 'Fast prioritet',
  NotifyTeams: 'Varsle Teams',
  CreateTickets: 'Lag saker',
  EncryptedWebhookUrl: 'Webhook-adresse',
  NotifyCriticalManualTickets: 'Varsle ved kritiske saker',
  UpdatedAt: 'Oppdatert',
  ReferenceDate: 'Dato',
  CompletedAt: 'Fullført',
  CompletedByName: 'Utført av',
  DueDate: 'Frist',
  OffsetDays: 'Frist (dager)',
  SortOrder: 'Rekkefølge',
  Note: 'Notat',
  AnonymizedAt: 'Anonymisert',
  AnonymizeAfterMonths: 'Anonymiser etter (måneder)',
  Purpose: 'Formål',
  LegalBasis: 'Behandlingsgrunnlag',
  Retention: 'Lagringstid',
  anonymisert: 'Anonymisert',
  auditEventsRedacted: 'Vaskede logglinjer',
  accessRequests: 'Tilgangsbestillinger',
  created: 'Nye',
  updated: 'Oppdatert',
  totalFromEntra: 'Totalt i Entra',
  groupId: 'Entra-gruppe',
  userObjectId: 'Entra-bruker',
  alreadyMember: 'Allerede medlem',
  error: 'Feil',
}

// Felter som er støy i en «opprettet»-hendelse
const HIDDEN_ON_CREATE = new Set(['CreatedAt', 'RequestedByObjectId', 'EntraObjectId'])

const EMPLOYEE_FIELDS = new Set(['AssignedToEmployeeId', 'ReportedByEmployeeId', 'EmployeeId'])

// Koder som vises med norske navn (gamle hendelser kan ha norske verdier, som vises som de er)
const CODE_LABELS: Record<string, Record<string, string>> = {
  Status: { ...STATUS_LABELS, ...ASSET_STATUS_LABELS },
  Type: { ...SERVICE_TYPE_LABELS, ...ASSET_TYPE_LABELS },
  Priority: PRIORITY_LABELS,
  DefaultPriority: PRIORITY_LABELS,
}

export function actionLabel(action: string): string {
  return ACTION_LABELS[action] ? t(ACTION_LABELS[action]) : action
}

export function formatDateTime(value: string): string {
  return new Date(value).toLocaleString(locale(), {
    dateStyle: 'short',
    timeStyle: 'short',
  })
}

// Slår opp ansatt-ID til navn; ukjente ID-er vises som #ID
export type EmployeeNames = Map<number, string>

function formatValue(field: string, value: unknown, employees: EmployeeNames): string {
  if (value === null || value === undefined || value === '') return '–'

  if (typeof value === 'string' && CODE_LABELS[field]?.[value]) return t(CODE_LABELS[field][value])

  if (EMPLOYEE_FIELDS.has(field) && typeof value === 'number') {
    return employees.get(value) ?? `#${value}`
  }

  if (typeof value === 'boolean') return value ? t('ja') : t('nei')

  if (typeof value === 'string' && /^\d{4}-\d{2}-\d{2}T/.test(value)) {
    return formatDateTime(value)
  }

  const text = String(value)
  return text.length > 80 ? `${text.slice(0, 77)}…` : text
}

// Én linje per felt, f.eks. «Status: Ny → Under behandling»
export function describeChanges(event: AuditEvent, employees: EmployeeNames): string[] {
  if (!event.changes) return []

  return Object.entries(event.changes)
    .filter(([field]) => !(event.action === 'created' && HIDDEN_ON_CREATE.has(field)))
    .map(([field, value]) => {
      const label = FIELD_LABELS[field] ? t(FIELD_LABELS[field]) : field

      if (!Array.isArray(value) || value.length !== 2) {
        return `${label}: ${formatValue(field, value, employees)}`
      }

      const [before, after] = value

      if (event.action === 'created') return `${label}: ${formatValue(field, after, employees)}`
      if (event.action === 'deleted') return `${label}: ${formatValue(field, before, employees)}`

      return `${label}: ${formatValue(field, before, employees)} → ${formatValue(field, after, employees)}`
    })
}

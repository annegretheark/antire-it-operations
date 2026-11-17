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
}

export const ENTITY_LABELS: Record<string, string> = {
  Ticket: 'Sak',
  AccessRequest: 'Tilgangsbestilling',
  Employee: 'Ansatt',
  EmployeeResponsibility: 'Ansvarsområde',
  TicketCategoryGroup: 'Kategorigruppe',
  EmployeeSync: 'Synk fra Entra',
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
  created: 'Nye',
  updated: 'Oppdatert',
  totalFromEntra: 'Totalt i Entra',
}

// Felter som er støy i en «opprettet»-hendelse
const HIDDEN_ON_CREATE = new Set(['CreatedAt', 'RequestedByObjectId', 'EntraObjectId'])

const EMPLOYEE_FIELDS = new Set(['AssignedToEmployeeId', 'ReportedByEmployeeId', 'EmployeeId'])

export function actionLabel(action: string): string {
  return ACTION_LABELS[action] ?? action
}

export function formatDateTime(value: string): string {
  return new Date(value).toLocaleString('nb-NO', {
    dateStyle: 'short',
    timeStyle: 'short',
  })
}

// Slår opp ansatt-ID til navn; ukjente ID-er vises som #ID
export type EmployeeNames = Map<number, string>

function formatValue(field: string, value: unknown, employees: EmployeeNames): string {
  if (value === null || value === undefined || value === '') return '–'

  if (EMPLOYEE_FIELDS.has(field) && typeof value === 'number') {
    return employees.get(value) ?? `#${value}`
  }

  if (typeof value === 'boolean') return value ? 'ja' : 'nei'

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
      const label = FIELD_LABELS[field] ?? field

      if (!Array.isArray(value) || value.length !== 2) {
        return `${label}: ${formatValue(field, value, employees)}`
      }

      const [before, after] = value

      if (event.action === 'created') return `${label}: ${formatValue(field, after, employees)}`
      if (event.action === 'deleted') return `${label}: ${formatValue(field, before, employees)}`

      return `${label}: ${formatValue(field, before, employees)} → ${formatValue(field, after, employees)}`
    })
}

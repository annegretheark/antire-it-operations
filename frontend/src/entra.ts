import { t } from './i18n'

// Kontostatus i Entra for ansatte (Ansatte og offboarding). Speiler
// EntraAccountStatuses i backend.

export type EntraStatus = 'enabled' | 'disabled' | 'missing' | 'not_linked' | 'unknown'

export type EmployeeEntraStatus = { employeeId: number; status: EntraStatus; entraObjectId: string | null }

export type EmployeeEntraStatuses = {
  statuses: EmployeeEntraStatus[]
  unavailableReason: string | null
}

const LABELS: Record<EntraStatus, string> = {
  enabled: 'Aktiv',
  disabled: 'Deaktivert',
  missing: 'Finnes ikke',
  not_linked: 'Ikke koblet',
  unknown: 'Ukjent',
}

export function entraStatusLabel(status: EntraStatus): string {
  return t(LABELS[status] ?? status)
}

// Bør sjekkes: sluttet, men kontoen er aktiv; eller i jobb, men kontoen er deaktivert
export function entraMismatch(employeeStatus: string, status: EntraStatus): string | null {
  if (employeeStatus === 'Sluttet' && status === 'enabled')
    return t('Har sluttet, men kontoen i Entra er aktiv')
  if (employeeStatus === 'Aktiv' && status === 'disabled')
    return t('Er i jobb, men kontoen i Entra er deaktivert')
  return null
}

// Lenke som åpner Microsoft 365 → Brukere med brukeren (bare Helpdesk.Admin ser siden)
export function m365UserLink(entraObjectId: string): string {
  return `?bruker=${encodeURIComponent(entraObjectId)}`
}

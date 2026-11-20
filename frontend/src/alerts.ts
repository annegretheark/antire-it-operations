import { t } from './i18n'

// Varsler (fase 4): typer og norske visningsnavn

export type Alert = {
  id: number
  source: string
  externalId: string
  title: string
  severity: string
  resourceId: string | null
  portalUrl: string | null
  state: string
  firstSeenAt: string
  lastSeenAt: string
  lastFiredAt: string
  occurrences: number
  ticketId: number | null
  ticketStatus: string | null
}

export type AlertSourceStatus = {
  source: string
  lastRunAt: string | null
  ok: boolean
  message: string
  received: number
}

export type AlertRule = {
  id: number
  source: string
  minSeverity: string
  category: string
  priorityOverride: string | null
  notifyTeams: boolean
  createTickets: boolean
}

export const ALERT_SOURCE_LABELS: Record<string, string> = {
  'azure-monitor': 'Azure Monitor',
  defender: 'Microsoft Defender',
}

export const ALERT_STATE_LABELS: Record<string, string> = {
  fired: 'Aktiv',
  resolved: 'Løst hos kilden',
}

// Fra mest til minst alvorlig, per kilde (samme som backend)
export const SEVERITIES: Record<string, string[]> = {
  'azure-monitor': ['Sev0', 'Sev1', 'Sev2', 'Sev3', 'Sev4'],
  defender: ['high', 'medium', 'low', 'informational'],
}

const DEFENDER_SEVERITY_LABELS: Record<string, string> = {
  high: 'Høy',
  medium: 'Middels',
  low: 'Lav',
  informational: 'Informasjon',
}

export function severityLabel(source: string, severity: string): string {
  return source === 'defender' ? (DEFENDER_SEVERITY_LABELS[severity] ? t(DEFENDER_SEVERITY_LABELS[severity]) : severity) : severity
}

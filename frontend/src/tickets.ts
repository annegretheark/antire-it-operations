// Koder for status, prioritet og SLA på saker, med norske visningsnavn.
// Backend bruker kodene; revisjonsloggen kan inneholde de gamle norske verdiene.

export const STATUSES = ['new', 'in_progress', 'resolved'] as const
export const PRIORITIES = ['low', 'normal', 'high', 'critical'] as const

export type TicketStatus = (typeof STATUSES)[number]
export type TicketPriority = (typeof PRIORITIES)[number]
export type SlaStatus = 'none' | 'ok' | 'at_risk' | 'breached' | 'met'

export const STATUS_LABELS: Record<string, string> = {
  new: 'Ny',
  in_progress: 'Under behandling',
  resolved: 'Løst',
}

export const PRIORITY_LABELS: Record<string, string> = {
  low: 'Lav',
  normal: 'Normal',
  high: 'Høy',
  critical: 'Kritisk',
}

export const SLA_LABELS: Record<SlaStatus, string> = {
  none: 'Ingen SLA',
  ok: 'I rute',
  at_risk: 'I fare',
  breached: 'Brudd',
  met: 'Oppfylt',
}

export function statusLabel(status: string): string {
  return STATUS_LABELS[status] ?? status
}

export function priorityLabel(priority: string): string {
  return PRIORITY_LABELS[priority] ?? priority
}

// Brudd eller i fare: det som trenger oppmerksomhet
export function needsAttention(sla: SlaStatus): boolean {
  return sla === 'at_risk' || sla === 'breached'
}

// «om 2 t 15 min» / «for 3 t siden», for frister
export function formatRelative(iso: string, now = Date.now()): string {
  const minutes = Math.round((new Date(iso).getTime() - now) / 60000)
  const abs = Math.abs(minutes)

  const text =
    abs < 60
      ? `${abs} min`
      : abs < 48 * 60
        ? `${Math.floor(abs / 60)} t${abs % 60 ? ` ${abs % 60} min` : ''}`
        : `${Math.round(abs / (24 * 60))} døgn`

  return minutes >= 0 ? `om ${text}` : `for ${text} siden`
}

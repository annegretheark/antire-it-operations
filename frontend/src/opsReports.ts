import { locale } from './i18n'

// Ukentlig driftsrapport (fase 5): typer som speiler OpsReportsController og
// OpsReportMetrics i backend

export type OpsReportSummary = {
  id: number
  periodStart: string
  periodEnd: string
  aiStatus: OpsReportAiStatus
  createdAt: string
  createdBy: string
}

export type OpsReportAiStatus = 'ok' | 'disabled' | 'failed'

export type OpsAlertCount = { source: string; severity: string; count: number }

export type OpsReportMetrics = {
  generatedAt: string
  tickets: {
    created: number
    resolved: number
    openByPriority: Record<string, number>
    openOlderThan14Days: number
  }
  sla: { responseBreaches: number; resolutionBreaches: number; atRisk: number }
  alerts: { new: OpsAlertCount[]; recurred: OpsAlertCount[]; failingSources: string[] }
  access: { pendingApproval: number; oldestPendingDays: number | null }
  lifecycle: { onboardingNext14Days: number; offboardingNext14Days: number }
  m365: { inactiveGuests: number | null; unavailableReason: string | null }
}

export type OpsReport = OpsReportSummary & {
  metrics: OpsReportMetrics | null
  aiSummary: string | null
  aiActions: string[] | null
}

// «16.–22. nov. 2026»: perioden slutter mandag 00:00, så siste dag er søndagen før
export function formatPeriod(periodStart: string, periodEnd: string): string {
  const zone = 'Europe/Oslo'
  const start = new Date(periodStart)
  const lastDay = new Date(new Date(periodEnd).getTime() - 1)

  const day = (d: Date) => d.toLocaleDateString(locale(), { day: 'numeric', timeZone: zone })
  const full = (d: Date) =>
    d.toLocaleDateString(locale(), { day: 'numeric', month: 'short', year: 'numeric', timeZone: zone })

  const sameMonth =
    start.toLocaleDateString('en-GB', { month: 'numeric', year: 'numeric', timeZone: zone }) ===
    lastDay.toLocaleDateString('en-GB', { month: 'numeric', year: 'numeric', timeZone: zone })

  return sameMonth ? `${day(start)}.–${full(lastDay)}` : `${full(start)} – ${full(lastDay)}`
}

export function sumCounts(counts: OpsAlertCount[]): number {
  return counts.reduce((sum, c) => sum + c.count, 0)
}

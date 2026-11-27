import { useState } from 'react'
import './Helpdesk.css'
import { useCurrentUser } from '../useCurrentUser'
import { formatDateTime } from '../audit'
import { priorityLabel, PRIORITIES } from '../tickets'
import { ALERT_SOURCE_LABELS, severityLabel } from '../alerts'
import {
  formatPeriod,
  sumCounts,
  type OpsAlertCount,
  type OpsReport,
  type OpsReportMetrics,
  type OpsReportSummary,
} from '../opsReports'
import { errorText, useList, useResource, useSend } from './settings/settingsApi'
import { locale, t } from '../i18n'

// Ukentlig driftsrapport (Helpdesk.Agent og Helpdesk.Admin). Tallene er beregnet
// av appen; AI-delen vises bare når den er laget.
export default function OpsReports() {
  const { isHelpdeskAgent, isHelpdeskAdmin } = useCurrentUser()
  const [reloadKey, setReloadKey] = useState(0)
  // ?driftsrapport=<id> (lenke fra Teams) velger rapporten
  const [selectedId, setSelectedId] = useState<number | null>(() => {
    const id = Number(new URLSearchParams(window.location.search).get('driftsrapport'))
    return Number.isInteger(id) && id > 0 ? id : null
  })
  const [message, setMessage] = useState('')
  const [error, setError] = useState('')
  const [generating, setGenerating] = useState(false)
  const send = useSend()

  const reports = useList<OpsReportSummary>(isHelpdeskAgent ? '/api/opsreports' : null, reloadKey)

  // Den nyeste er valgt til brukeren velger en annen (eller den valgte ikke er i listen)
  const currentId =
    selectedId !== null && reports.items.some((r) => r.id === selectedId)
      ? selectedId
      : (reports.items[0]?.id ?? null)
  const report = useResource<OpsReport>(currentId === null ? null : `/api/opsreports/${currentId}`, reloadKey)

  async function generate() {
    setGenerating(true)
    setMessage('')
    setError('')

    try {
      const response = await send('POST', '/api/opsreports/generate')
      const created: OpsReport = await response.json()
      setMessage(
        response.status === 201
          ? t('Rapporten for forrige uke er laget.')
          : t('Rapporten for forrige uke finnes allerede.')
      )
      setSelectedId(created.id)
      setReloadKey((k) => k + 1)
    } catch (e) {
      setError(errorText(e))
    } finally {
      setGenerating(false)
    }
  }

  if (!isHelpdeskAgent) {
    return (
      <section className="helpdesk">
        <div className="helpdesk-card">
          <h3>{t('Driftsrapport')}</h3>
          <p>{t('Du trenger rollen Helpdesk.Agent eller Helpdesk.Admin for å se driftsrapporter.')}</p>
        </div>
      </section>
    )
  }

  return (
    <section className="helpdesk">
      <div className="helpdesk-card">
        <h3>{t('Driftsrapport')}</h3>
        <p className="service-description">
          {t('Hver uke: hva som skjedde i driften forrige uke, og hva som haster nå. Tallene beregnes av appen.')}
        </p>
        {isHelpdeskAdmin && (
          <div className="settings-form-actions">
            <button type="button" onClick={() => void generate()} disabled={generating}>
              {generating ? t('Lager rapport...') : t('Lag rapport nå')}
            </button>
          </div>
        )}
        {message && <p className="settings-message">{message}</p>}
        {error && <p role="alert" className="settings-error">{error}</p>}
      </div>

      <div className="ops-report-layout">
        <div className="helpdesk-card ops-report-weeks">
          <h3>{t('Uker')}</h3>
          {reports.error && <p role="alert" className="settings-error">{reports.error}</p>}
          {reports.loading ? (
            <p>{t('Henter rapporter...')}</p>
          ) : reports.items.length === 0 ? (
            <p>{t('Ingen rapporter ennå.')}</p>
          ) : (
            <ul>
              {reports.items.map((r) => (
                <li key={r.id}>
                  <button
                    type="button"
                    className={r.id === currentId ? 'active' : ''}
                    aria-current={r.id === currentId}
                    onClick={() => setSelectedId(r.id)}
                  >
                    {formatPeriod(r.periodStart, r.periodEnd)}
                  </button>
                </li>
              ))}
            </ul>
          )}
        </div>

        <div className="ops-report-details">
          {report.error && <p role="alert" className="settings-error">{report.error}</p>}
          {currentId !== null && report.loading && <p>{t('Henter rapporten...')}</p>}
          {report.data && !report.loading && <ReportView report={report.data} />}
        </div>
      </div>
    </section>
  )
}

function ReportView({ report }: { report: OpsReport }) {
  const m = report.metrics

  return (
    <>
      <div className="helpdesk-card">
        <h3>{t('Uke {period}', { period: formatPeriod(report.periodStart, report.periodEnd) })}</h3>
        <p className="settings-muted">
          {t('Laget {time} av {by}', {
            time: formatDateTime(report.createdAt),
            by: report.createdBy === 'system' ? t('systemet') : report.createdBy,
          })}
        </p>
        <AiSection report={report} />
      </div>

      {m ? <MetricsView m={m} /> : (
        <div className="helpdesk-card">
          <p>{t('Rapporten mangler nøkkeltall.')}</p>
        </div>
      )}
    </>
  )
}

function AiSection({ report }: { report: OpsReport }) {
  if (report.aiStatus === 'ok' && report.aiSummary) {
    return (
      <div className="ops-report-ai">
        <span className="ops-report-ai-badge">{t('Skrevet av AI')}</span>
        <p>{report.aiSummary}</p>
        {report.aiActions && report.aiActions.length > 0 && (
          <>
            <h4>{t('Anbefalte tiltak')}</h4>
            <ol>
              {report.aiActions.map((action, i) => <li key={i}>{action}</li>)}
            </ol>
          </>
        )}
      </div>
    )
  }

  return (
    <p className="settings-muted">
      {report.aiStatus === 'failed'
        ? t('AI-oppsummeringen feilet. Tallene under er likevel riktige.')
        : t('AI-oppsummering er ikke slått på.')}
    </p>
  )
}

function Stat({ label, value, tone }: { label: string; value: number | string; tone?: 'alert' | 'warning' }) {
  const highlight = typeof value === 'number' && value > 0 && tone
  return (
    <div className={`dashboard-stat${highlight ? ` dashboard-${tone}` : ''}`}>
      <h4>{label}</h4>
      <strong>{value}</strong>
    </div>
  )
}

function MetricsView({ m }: { m: OpsReportMetrics }) {
  return (
    <>
      <div className="helpdesk-card">
        <h3>{t('Saker')}</h3>
        <div className="dashboard-grid">
          <Stat label={t('Nye i perioden')} value={m.tickets.created} />
          <Stat label={t('Løst i perioden')} value={m.tickets.resolved} />
          <Stat label={t('Åpne eldre enn 14 dager')} value={m.tickets.openOlderThan14Days} tone="warning" />
        </div>
        <h4 className="ops-report-subheading">{t('Åpne nå per prioritet')}</h4>
        <div className="dashboard-grid">
          {[...PRIORITIES].reverse().map((p) => (
            <Stat
              key={p}
              label={priorityLabel(p)}
              value={m.tickets.openByPriority[p] ?? 0}
              tone={p === 'critical' ? 'alert' : undefined}
            />
          ))}
        </div>
      </div>

      <div className="helpdesk-card">
        <h3>{t('SLA')}</h3>
        <div className="dashboard-grid">
          <Stat label={t('Brudd på responstid')} value={m.sla.responseBreaches} tone="alert" />
          <Stat label={t('Brudd på løsningstid')} value={m.sla.resolutionBreaches} tone="alert" />
          <Stat label={t('I fare nå')} value={m.sla.atRisk} tone="warning" />
        </div>
      </div>

      <div className="helpdesk-card">
        <h3>{t('Varsler')}</h3>
        <div className="dashboard-grid">
          <Stat label={t('Nye i perioden')} value={sumCounts(m.alerts.new)} />
          <Stat label={t('Fyrte igjen i perioden')} value={sumCounts(m.alerts.recurred)} />
        </div>
        <AlertCountTable title={t('Nye')} counts={m.alerts.new} />
        <AlertCountTable title={t('Fyrte igjen')} counts={m.alerts.recurred} />
        {m.alerts.failingSources.length > 0 ? (
          <p className="settings-error">
            {t('Henting feiler nå: {sources}', {
              sources: m.alerts.failingSources.map((s) => ALERT_SOURCE_LABELS[s] ?? s).join(', '),
            })}
          </p>
        ) : (
          <p className="settings-muted">{t('Ingen varselkilder feilet da rapporten ble laget.')}</p>
        )}
      </div>

      <div className="helpdesk-card">
        <h3>{t('Tilgang, livssyklus og Microsoft 365')}</h3>
        <div className="dashboard-grid">
          <Stat label={t('Bestillinger som venter')} value={m.access.pendingApproval} tone="warning" />
          <Stat
            label={t('Eldste venter (dager)')}
            value={m.access.oldestPendingDays ?? '–'}
          />
          <Stat label={t('Onboarding neste 14 dager')} value={m.lifecycle.onboardingNext14Days} />
          <Stat label={t('Offboarding neste 14 dager')} value={m.lifecycle.offboardingNext14Days} />
          <Stat
            label={t('Inaktive gjester (90 dager)')}
            value={m.m365.inactiveGuests ?? '–'}
            tone="warning"
          />
        </div>
        {m.m365.unavailableReason && (
          <p className="settings-muted">
            {t('Inaktive gjester: {reason}', { reason: m.m365.unavailableReason })}
          </p>
        )}
        <p className="settings-muted">
          {t('«Nå»-tall gjelder {time}, da rapporten ble laget.', { time: formatDateTime(m.generatedAt) })}
        </p>
      </div>

      {m.costs && <CostSection costs={m.costs} />}
    </>
  )
}

function AlertCountTable({ title, counts }: { title: string; counts: OpsAlertCount[] }) {
  if (counts.length === 0) return null

  return (
    <div className="audit-table-wrapper">
      <table className="audit-table">
        <caption className="ops-report-caption">{title}</caption>
        <thead>
          <tr>
            <th>{t('Kilde')}</th>
            <th>{t('Alvorlighetsgrad')}</th>
            <th>{t('Antall')}</th>
          </tr>
        </thead>
        <tbody>
          {counts.map((c) => (
            <tr key={`${c.source}|${c.severity}`}>
              <td>{ALERT_SOURCE_LABELS[c.source] ?? c.source}</td>
              <td>{severityLabel(c.source, c.severity)}</td>
              <td>{c.count}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}

// Kostnader i Azure (steg 5). Beløpene er beregnet av appen fra Cost Management.
function CostSection({ costs }: { costs: NonNullable<OpsReportMetrics['costs']> }) {
  const money = (value: number | null) =>
    value == null ? '–' : `${value.toLocaleString(locale(), { maximumFractionDigits: 0 })} ${costs.currency ?? ''}`.trim()
  const change = costs.changePercent

  return (
    <div className="helpdesk-card">
      <h3>{t('Kostnader i Azure')}</h3>
      {costs.unavailableReason ? (
        <p className="settings-muted">{t('Kostnader: {reason}', { reason: costs.unavailableReason })}</p>
      ) : (
        <>
          <div className="dashboard-grid">
            <Stat label={t('Kostnad forrige uke')} value={money(costs.total)} />
            <Stat label={t('Uka før')} value={money(costs.previousTotal)} />
            <Stat
              label={t('Endring fra uka før')}
              value={change == null ? '–' : `${change > 0 ? '+' : ''}${change.toLocaleString(locale())} %`}
              tone={change != null && change > 20 ? 'warning' : undefined}
            />
          </div>
          {costs.topServices.length > 0 && (
            <table className="audit-table">
              <caption className="ops-report-caption">{t('Dyreste tjenestetyper')}</caption>
              <thead><tr><th>{t('Tjenestetype')}</th><th>{t('Beløp')}</th></tr></thead>
              <tbody>
                {costs.topServices.map((s) => (
                  <tr key={s.service}><td>{s.service}</td><td>{money(s.amount)}</td></tr>
                ))}
              </tbody>
            </table>
          )}
          <p className="settings-muted">
            {t('{n} abonnementer telt', { n: costs.subscriptionsCounted })}
            {costs.subscriptionsSkipped > 0 && ` · ${t('{n} uten tilgang', { n: costs.subscriptionsSkipped })}`}
            {' · '}{t('Cost Management kan være forsinket med 24–48 timer.')}
          </p>
        </>
      )}
    </div>
  )
}

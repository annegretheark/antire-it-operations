import { useMemo, useState } from 'react'
import './Helpdesk.css'
import { useCurrentUser } from '../useCurrentUser'
import { formatDateTime } from '../audit'
import { statusLabel } from '../tickets'
import {
  ALERT_SOURCE_LABELS,
  ALERT_STATE_LABELS,
  SEVERITIES,
  severityLabel,
  type Alert,
  type AlertSourceStatus,
} from '../alerts'
import { errorText, useList, useSend } from './settings/settingsApi'
import { t } from '../i18n'

type IngestResult = { alertId: number; ticketId: number | null; outcome: string }

const OUTCOME_TEXT: Record<string, string> = {
  ticket_created: 'Varselet ga en ny sak',
  ticket_reopened: 'Varselet gjenåpnet saken',
  recurred: 'Ny forekomst på en åpen sak',
  stored_only: 'Varselet er lagret uten sak (under terskelen i varselreglene)',
  resolved: 'Varselet er løst hos kilden',
  unchanged: 'Ingen endring',
}

// Varsler fra Azure Monitor og Defender (Helpdesk.Agent og Helpdesk.Admin)
export default function Alerts() {
  const { isHelpdeskAgent, isHelpdeskAdmin } = useCurrentUser()
  const [reloadKey, setReloadKey] = useState(0)
  const [source, setSource] = useState('')
  const [state, setState] = useState('')

  const url = useMemo(() => {
    const params = new URLSearchParams()
    if (source) params.set('source', source)
    if (state) params.set('state', state)
    return `/api/alerts${params.size ? `?${params}` : ''}`
  }, [source, state])

  const alerts = useList<Alert>(url, reloadKey)
  const sources = useList<AlertSourceStatus>('/api/alerts/sources', reloadKey)

  if (!isHelpdeskAgent) {
    return (
      <section className="helpdesk">
        <div className="helpdesk-card">
          <h3>{t('Varsler')}</h3>
          <p>{t('Du trenger rollen Helpdesk.Agent eller Helpdesk.Admin for å se varsler.')}</p>
        </div>
      </section>
    )
  }

  return (
    <section className="helpdesk">
      <div className="helpdesk-card">
        <h3>{t('Kilder')}</h3>
        <ul className="alert-sources">
          {sources.items.map((s) => (
            <li key={s.source}>
              <strong>{ALERT_SOURCE_LABELS[s.source] ?? s.source}:</strong>{' '}
              <span className={s.ok ? 'settings-message' : 'settings-muted'}>{s.message}</span>
              {s.lastRunAt && <span className="settings-muted"> · {formatDateTime(s.lastRunAt)}</span>}
            </li>
          ))}
        </ul>
        <p className="service-description">
          {t('Varsler hentes hvert 5. minutt. Hvilke varsler som blir saker, styres under Innstillinger → Varselregler.')}
        </p>
      </div>

      {isHelpdeskAdmin && <SimulateAlert onDone={() => setReloadKey((k) => k + 1)} />}

      <div className="helpdesk-card">
        <h3>{t('Mottatte varsler')}</h3>

        <div className="asset-filters">
          <select aria-label={t('Kilde')} value={source} onChange={(e) => setSource(e.target.value)}>
            <option value="">{t('Alle kilder')}</option>
            {Object.entries(ALERT_SOURCE_LABELS).map(([value, label]) => (
              <option key={value} value={value}>{label}</option>
            ))}
          </select>
          <select aria-label={t('Tilstand')} value={state} onChange={(e) => setState(e.target.value)}>
            <option value="">{t('Alle tilstander')}</option>
            {Object.entries(ALERT_STATE_LABELS).map(([value, label]) => (
              <option key={value} value={value}>{t(label)}</option>
            ))}
          </select>
          <button type="button" className="settings-row-action" onClick={() => setReloadKey((k) => k + 1)}>
            {t('Oppdater')}
          </button>
        </div>

        {alerts.error && <p role="alert" className="settings-error">{alerts.error}</p>}

        {alerts.loading ? (
          <p>{t('Henter varsler...')}</p>
        ) : alerts.items.length === 0 ? (
          <p>{t('Ingen varsler mottatt.')}</p>
        ) : (
          <div className="audit-table-wrapper">
            <table className="audit-table">
              <thead>
                <tr>
                  <th>{t('Varsel')}</th>
                  <th>{t('Kilde')}</th>
                  <th>{t('Alvorlighetsgrad')}</th>
                  <th>{t('Tilstand')}</th>
                  <th>{t('Forekomster')}</th>
                  <th>{t('Sist fyrt')}</th>
                  <th>{t('Sak')}</th>
                </tr>
              </thead>
              <tbody>
                {alerts.items.map((a) => (
                  <tr key={a.id}>
                    <td>
                      <strong>{a.title}</strong>
                      {a.resourceId && <div className="settings-muted">{a.resourceId}</div>}
                      {a.portalUrl && (
                        <div>
                          <a href={a.portalUrl} target="_blank" rel="noreferrer">{t('Åpne varselet')}</a>
                        </div>
                      )}
                    </td>
                    <td>{ALERT_SOURCE_LABELS[a.source] ?? a.source}</td>
                    <td>{severityLabel(a.source, a.severity)}</td>
                    <td>{ALERT_STATE_LABELS[a.state] ? t(ALERT_STATE_LABELS[a.state]) : a.state}</td>
                    <td>{a.occurrences}</td>
                    <td className="audit-nowrap">{formatDateTime(a.lastFiredAt)}</td>
                    <td className="audit-nowrap">
                      {a.ticketId
                        ? <a href={`?sak=${a.ticketId}`}>#{a.ticketId}</a>
                        : <span className="settings-muted">{t('Ingen')}</span>}
                      {a.ticketStatus && <div className="settings-muted">{statusLabel(a.ticketStatus)}</div>}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>
    </section>
  )
}

// Testvarsel gjennom hele flyten (Helpdesk.Admin), siden dev-tenanten ikke har Defender
function SimulateAlert({ onDone }: { onDone: () => void }) {
  const send = useSend()
  const [source, setSource] = useState('defender')
  const [severity, setSeverity] = useState('high')
  const [title, setTitle] = useState('Simulert varsel')
  const [externalId, setExternalId] = useState('')
  const [saving, setSaving] = useState(false)
  const [message, setMessage] = useState('')
  const [error, setError] = useState('')

  async function simulate(state: 'fired' | 'resolved') {
    setSaving(true)
    setMessage('')
    setError('')

    try {
      const response = await send('POST', '/api/alerts/simulate', {
        source, severity, title, externalId: externalId.trim() || null, state,
      })
      const result: IngestResult = await response.json()
      setMessage(
        (OUTCOME_TEXT[result.outcome] ? t(OUTCOME_TEXT[result.outcome]) : result.outcome) +
        (result.ticketId ? ` (${t('sak #{id}', { id: result.ticketId })}).` : '.')
      )
      onDone()
    } catch (e) {
      setError(errorText(e))
    } finally {
      setSaving(false)
    }
  }

  return (
    <div className="helpdesk-card">
      <h3>{t('Simuler varsel')}</h3>
      <p className="audit-intro">
        {t('Sender et testvarsel gjennom hele flyten: varselregler, sak, SLA, tildeling og Teams. Bruk samme varsel-ID flere ganger for å teste ny forekomst, gjenåpning og «løst hos kilden».')}
      </p>

      <div className="helpdesk-form">
        <div className="settings-form-grid">
          <div>
            <label htmlFor="sim-source">{t('Kilde')}</label>
            <select
              id="sim-source"
              value={source}
              onChange={(e) => {
                setSource(e.target.value)
                setSeverity(SEVERITIES[e.target.value][0])
              }}
            >
              {Object.entries(ALERT_SOURCE_LABELS).map(([value, label]) => (
                <option key={value} value={value}>{label}</option>
              ))}
            </select>
          </div>
          <div>
            <label htmlFor="sim-severity">{t('Alvorlighetsgrad')}</label>
            <select id="sim-severity" value={severity} onChange={(e) => setSeverity(e.target.value)}>
              {SEVERITIES[source].map((s) => (
                <option key={s} value={s}>{severityLabel(source, s)}</option>
              ))}
            </select>
          </div>
          <div>
            <label htmlFor="sim-title">{t('Tittel')}</label>
            <input id="sim-title" value={title} maxLength={200} onChange={(e) => setTitle(e.target.value)} />
          </div>
          <div>
            <label htmlFor="sim-id">{t('Varsel-ID (valgfri)')}</label>
            <input
              id="sim-id"
              value={externalId}
              placeholder={t('Tom = nytt varsel')}
              onChange={(e) => setExternalId(e.target.value)}
            />
          </div>
        </div>

        <div className="settings-form-actions">
          <button type="button" disabled={saving} onClick={() => void simulate('fired')}>
            {t('Send varsel')}
          </button>
          <button
            type="button"
            className="secondary"
            disabled={saving || !externalId.trim()}
            onClick={() => void simulate('resolved')}
          >
            {t('Marker som løst hos kilden')}
          </button>
        </div>
      </div>

      {message && <p className="settings-message">{message}</p>}
      {error && <p role="alert" className="settings-error">{error}</p>}
    </div>
  )
}

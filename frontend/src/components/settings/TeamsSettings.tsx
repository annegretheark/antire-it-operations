import { useState } from 'react'
import { formatDateTime } from '../../audit'
import { errorText, useResource, useSend } from './settingsApi'
import { t } from '../../i18n'

type TeamsNotification = {
  id: number
  ticketId: number | null
  kind: string
  status: string
  attempts: number
  lastError: string | null
  createdAt: string
  sentAt: string | null
}

type TeamsSettingsData = {
  configured: boolean
  maskedWebhookUrl: string | null
  notifyCriticalManualTickets: boolean
  recentNotifications: TeamsNotification[]
}

const KIND_LABELS: Record<string, string> = {
  new_alert_ticket: 'Ny sak fra varsel',
  reopened: 'Sak gjenåpnet av varsel',
  critical_ticket: 'Ny kritisk sak',
}

const STATUS_LABELS: Record<string, string> = {
  pending: 'Venter (prøves igjen)',
  sent: 'Sendt',
  failed: 'Feilet',
}

// Teams-varsling: webhook-adresse fra Workflows, og varslingslogg
export default function TeamsSettings() {
  const send = useSend()
  const [reloadKey, setReloadKey] = useState(0)
  const settings = useResource<TeamsSettingsData>('/api/teams/settings', reloadKey)

  const [webhookUrl, setWebhookUrl] = useState('')
  const [notifyCritical, setNotifyCritical] = useState<boolean | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [message, setMessage] = useState('')

  const data = settings.data
  const critical = notifyCritical ?? data?.notifyCriticalManualTickets ?? false

  async function run(action: () => Promise<string>) {
    setBusy(true)
    setError('')
    setMessage('')

    try {
      setMessage(await action())
      setWebhookUrl('')
      setNotifyCritical(null)
      setReloadKey((k) => k + 1)
    } catch (e) {
      setError(errorText(e))
    } finally {
      setBusy(false)
    }
  }

  const save = () =>
    run(async () => {
      await send('PUT', '/api/teams/settings', {
        webhookUrl: webhookUrl.trim() || null,
        notifyCriticalManualTickets: critical,
      })
      return t('Teams-oppsettet er lagret.')
    })

  const remove = () =>
    run(async () => {
      await send('PUT', '/api/teams/settings', {
        removeWebhook: true,
        notifyCriticalManualTickets: critical,
      })
      return t('Webhook-adressen er fjernet. Teams varsles ikke lenger.')
    })

  const test = () =>
    run(async () => {
      const response = await send('POST', '/api/teams/test')
      return (await response.json()).message
    })

  return (
    <div className="helpdesk-card">
      <h3>Teams</h3>
      <p className="audit-intro">
        {t('Lag en arbeidsflyt i Teams-kanalen:')} <strong>Workflows</strong> →{' '}
        {t('«Post to a channel when a webhook request is received», og lim inn adressen den gir. Adressen er en hemmelighet: den lagres kryptert og vises bare forkortet her.')}
      </p>

      {settings.loading ? (
        <p>{t('Henter oppsett...')}</p>
      ) : (
        <form
          className="helpdesk-form settings-form"
          onSubmit={(e) => {
            e.preventDefault()
            void save()
          }}
        >
          <p>
            <strong>{t('Status')}:</strong>{' '}
            {data?.configured
              ? <>{t('Satt opp')} (<code>{data.maskedWebhookUrl}</code>)</>
              : t('Ikke satt opp. Saker lages som vanlig, men Teams varsles ikke.')}
          </p>

          <div>
            <label htmlFor="teams-url">{data?.configured ? t('Ny webhook-adresse') : t('Webhook-adresse')}</label>
            <input
              id="teams-url"
              type="url"
              value={webhookUrl}
              autoComplete="off"
              placeholder="https://….logic.azure.com/workflows/…"
              onChange={(e) => setWebhookUrl(e.target.value)}
            />
          </div>

          <label className="settings-check">
            <input type="checkbox" checked={critical} onChange={(e) => setNotifyCritical(e.target.checked)} />
            {t('Varsle også ved nye manuelle saker med prioritet kritisk')}
          </label>

          <div className="settings-form-actions">
            <button type="submit" disabled={busy}>{t('Lagre')}</button>
            {data?.configured && (
              <>
                <button type="button" className="secondary" disabled={busy} onClick={() => void test()}>
                  {t('Send testmelding')}
                </button>
                <button type="button" className="secondary" disabled={busy} onClick={() => void remove()}>
                  {t('Fjern adressen')}
                </button>
              </>
            )}
          </div>
        </form>
      )}

      {error && <p role="alert" className="settings-error">{error}</p>}
      {message && <p className="settings-message">{message}</p>}

      {data && data.recentNotifications.length > 0 && (
        <>
          <h4>{t('Siste meldinger')}</h4>
          <div className="audit-table-wrapper">
            <table className="audit-table">
              <thead>
                <tr>
                  <th>{t('Tidspunkt')}</th>
                  <th>{t('Hva')}</th>
                  <th>{t('Sak')}</th>
                  <th>{t('Status')}</th>
                </tr>
              </thead>
              <tbody>
                {data.recentNotifications.map((n) => (
                  <tr key={n.id}>
                    <td className="audit-nowrap">{formatDateTime(n.createdAt)}</td>
                    <td>{KIND_LABELS[n.kind] ? t(KIND_LABELS[n.kind]) : n.kind}</td>
                    <td>{n.ticketId ? <a href={`?sak=${n.ticketId}`}>#{n.ticketId}</a> : '–'}</td>
                    <td>
                      <span className={n.status === 'sent' ? undefined : 'settings-error'}>
                        {STATUS_LABELS[n.status] ? t(STATUS_LABELS[n.status]) : n.status}
                      </span>
                      {n.lastError && <div className="settings-muted">{n.lastError} ({t('forsøk {n}', { n: n.attempts })})</div>}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </>
      )}
    </div>
  )
}

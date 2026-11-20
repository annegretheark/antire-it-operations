import { useState } from 'react'
import { priorityLabel } from '../../tickets'
import { errorText, useList, useSend } from './settingsApi'
import { t } from '../../i18n'

type SlaTarget = {
  id: number
  priority: string
  responseMinutes: number
  resolutionMinutes: number
  twentyFourSeven: boolean
}

// «2 arbeidsdager 4 t» (arbeidstid) eller «4 t» (døgnet rundt)
function describe(minutes: number, twentyFourSeven: boolean): string {
  const dayLength = twentyFourSeven ? 24 * 60 : 8 * 60
  const days = Math.floor(minutes / dayLength)
  const hours = Math.floor((minutes % dayLength) / 60)
  const rest = minutes % 60

  const parts = [
    days > 0
      ? t(twentyFourSeven ? '{n} døgn' : days === 1 ? '{n} arbeidsdag' : '{n} arbeidsdager', { n: days })
      : '',
    hours > 0 ? `${hours} ${t('t')}` : '',
    rest > 0 ? `${rest} min` : '',
  ].filter(Boolean)

  return parts.join(' ') || '0 min'
}

// SLA-mål per prioritet. Arbeidstid er 08–16 på virkedager uten helligdager.
export default function SlaSettings() {
  const send = useSend()
  const [reloadKey, setReloadKey] = useState(0)
  const targets = useList<SlaTarget>('/api/slatargets', reloadKey)

  // Bare rader som er endret og ikke lagret ennå
  const [drafts, setDrafts] = useState<Record<string, SlaTarget>>({})
  const [savingPriority, setSavingPriority] = useState<string | null>(null)
  const [error, setError] = useState('')
  const [message, setMessage] = useState('')

  function update(target: SlaTarget, change: Partial<SlaTarget>) {
    setDrafts((previous) => ({
      ...previous,
      [target.priority]: { ...(previous[target.priority] ?? target), ...change },
    }))
  }

  async function save(priority: string) {
    const draft = drafts[priority]
    setSavingPriority(priority)
    setError('')
    setMessage('')

    try {
      await send('PUT', `/api/slatargets/${priority}`, {
        responseMinutes: draft.responseMinutes,
        resolutionMinutes: draft.resolutionMinutes,
        twentyFourSeven: draft.twentyFourSeven,
      })
      setMessage(t('SLA for {priority} prioritet er lagret.', { priority: priorityLabel(priority).toLowerCase() }))
      setDrafts(({ [priority]: _saved, ...rest }) => rest)
      setReloadKey((k) => k + 1)
    } catch (e) {
      setError(errorText(e))
    } finally {
      setSavingPriority(null)
    }
  }

  return (
    <div className="helpdesk-card">
      <h3>SLA</h3>
      <p className="audit-intro">
        {t('Frister for første respons og løsning per prioritet. Arbeidstid er 08:00–16:00 på virkedager, uten norske helligdager. Med «døgnet rundt» løper fristen også kveld, helg og helligdag. Endringer gjelder nye saker og saker som får ny prioritet.')}
      </p>

      {error && <p role="alert" className="settings-error">{error}</p>}
      {message && <p className="settings-message">{message}</p>}
      {targets.error && <p role="alert" className="settings-error">{targets.error}</p>}

      {targets.loading ? (
        <p>{t('Henter SLA-mål...')}</p>
      ) : (
        <div className="audit-table-wrapper">
          <table className="audit-table">
            <thead>
              <tr>
                <th>{t('Prioritet')}</th>
                <th>{t('Respons (minutter)')}</th>
                <th>{t('Løsning (minutter)')}</th>
                <th>{t('Døgnet rundt')}</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {targets.items.map((target) => {
                const draft = drafts[target.priority] ?? target
                const changed =
                  draft.responseMinutes !== target.responseMinutes ||
                  draft.resolutionMinutes !== target.resolutionMinutes ||
                  draft.twentyFourSeven !== target.twentyFourSeven

                return (
                  <tr key={target.priority}>
                    <td><strong>{priorityLabel(target.priority)}</strong></td>
                    <td>
                      <input
                        type="number"
                        min={1}
                        aria-label={t('Responstid for {priority}', { priority: priorityLabel(target.priority) })}
                        className="sla-minutes"
                        value={draft.responseMinutes}
                        onChange={(e) =>
                          update(target, { responseMinutes: Number(e.target.value) })
                        }
                      />
                      <div className="settings-muted">
                        {describe(draft.responseMinutes, draft.twentyFourSeven)}
                      </div>
                    </td>
                    <td>
                      <input
                        type="number"
                        min={1}
                        aria-label={t('Løsningstid for {priority}', { priority: priorityLabel(target.priority) })}
                        className="sla-minutes"
                        value={draft.resolutionMinutes}
                        onChange={(e) =>
                          update(target, { resolutionMinutes: Number(e.target.value) })
                        }
                      />
                      <div className="settings-muted">
                        {describe(draft.resolutionMinutes, draft.twentyFourSeven)}
                      </div>
                    </td>
                    <td>
                      <input
                        type="checkbox"
                        aria-label={t('Døgnet rundt for {priority}', { priority: priorityLabel(target.priority) })}
                        checked={draft.twentyFourSeven}
                        onChange={(e) =>
                          update(target, { twentyFourSeven: e.target.checked })
                        }
                      />
                    </td>
                    <td>
                      <button
                        type="button"
                        className="settings-row-action"
                        disabled={!changed || savingPriority !== null}
                        onClick={() => void save(target.priority)}
                      >
                        {savingPriority === target.priority ? t('Lagrer...') : t('Lagre')}
                      </button>
                    </td>
                  </tr>
                )
              })}
            </tbody>
          </table>
        </div>
      )}
    </div>
  )
}

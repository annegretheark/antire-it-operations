import { useMemo, useState } from 'react'
import { ALERT_SOURCE_LABELS, SEVERITIES, severityLabel, type AlertRule } from '../../alerts'
import { PRIORITIES, priorityLabel } from '../../tickets'
import { useServiceCatalog } from '../../serviceCatalog'
import { errorText, useList, useSend } from './settingsApi'
import { t } from '../../i18n'

// Hvilke varsler som blir saker, per kilde
export default function AlertRulesSettings() {
  const send = useSend()
  const [reloadKey, setReloadKey] = useState(0)
  const rules = useList<AlertRule>('/api/alertrules', reloadKey)
  const catalog = useServiceCatalog('includeInactive=true')

  // Bare regler som er endret og ikke lagret
  const [drafts, setDrafts] = useState<Record<string, AlertRule>>({})
  const [saving, setSaving] = useState<string | null>(null)
  const [error, setError] = useState('')
  const [message, setMessage] = useState('')

  const categories = useMemo(
    () => [...new Set([...catalog.items.map((i) => i.category), 'Sikkerhet'])].sort(),
    [catalog.items]
  )

  function update(rule: AlertRule, change: Partial<AlertRule>) {
    setDrafts((previous) => ({
      ...previous,
      [rule.source]: { ...(previous[rule.source] ?? rule), ...change },
    }))
  }

  async function save(source: string) {
    const draft = drafts[source]
    setSaving(source)
    setError('')
    setMessage('')

    try {
      await send('PUT', `/api/alertrules/${source}`, {
        minSeverity: draft.minSeverity,
        category: draft.category,
        priorityOverride: draft.priorityOverride || null,
        notifyTeams: draft.notifyTeams,
        createTickets: draft.createTickets,
      })
      setMessage(t('Regelen for {source} er lagret.', { source: ALERT_SOURCE_LABELS[source] ?? source }))
      setDrafts(({ [source]: _saved, ...rest }) => rest)
      setReloadKey((k) => k + 1)
    } catch (e) {
      setError(errorText(e))
    } finally {
      setSaving(null)
    }
  }

  return (
    <div className="helpdesk-card">
      <h3>{t('Varselregler')}</h3>
      <p className="audit-intro">
        {t('Varsler som er minst så alvorlige som terskelen, blir saker. Mindre alvorlige lagres under Varsler uten sak. Kategorien styrer automatisk tildeling. Prioriteten følger alvorlighetsgraden (Azure Sev0 kritisk, Sev1 høy, Sev2 normal; Defender høy → høy, middels → normal), med mindre du velger en fast prioritet.')}
      </p>

      {error && <p role="alert" className="settings-error">{error}</p>}
      {message && <p className="settings-message">{message}</p>}
      {rules.error && <p role="alert" className="settings-error">{rules.error}</p>}

      {rules.loading ? (
        <p>{t('Henter regler...')}</p>
      ) : (
        rules.items.map((rule) => {
          const draft = drafts[rule.source] ?? rule

          return (
            <div key={rule.source} className="helpdesk-form settings-form">
              <h4>{ALERT_SOURCE_LABELS[rule.source] ?? rule.source}</h4>

              <div className="settings-form-grid">
                <div>
                  <label htmlFor={`rule-severity-${rule.source}`}>{t('Sak fra og med')}</label>
                  <select
                    id={`rule-severity-${rule.source}`}
                    value={draft.minSeverity}
                    onChange={(e) => update(rule, { minSeverity: e.target.value })}
                  >
                    {SEVERITIES[rule.source].map((s) => (
                      <option key={s} value={s}>{severityLabel(rule.source, s)}</option>
                    ))}
                  </select>
                </div>
                <div>
                  <label htmlFor={`rule-category-${rule.source}`}>{t('Kategori')}</label>
                  <input
                    id={`rule-category-${rule.source}`}
                    list="rule-categories"
                    value={draft.category}
                    maxLength={100}
                    onChange={(e) => update(rule, { category: e.target.value })}
                  />
                </div>
                <div>
                  <label htmlFor={`rule-priority-${rule.source}`}>{t('Prioritet')}</label>
                  <select
                    id={`rule-priority-${rule.source}`}
                    value={draft.priorityOverride ?? ''}
                    onChange={(e) => update(rule, { priorityOverride: e.target.value || null })}
                  >
                    <option value="">{t('Etter alvorlighetsgrad')}</option>
                    {PRIORITIES.map((p) => (
                      <option key={p} value={p}>{t('Alltid {priority}', { priority: priorityLabel(p).toLowerCase() })}</option>
                    ))}
                  </select>
                </div>
              </div>

              <label className="settings-check">
                <input
                  type="checkbox"
                  checked={draft.createTickets}
                  onChange={(e) => update(rule, { createTickets: e.target.checked })}
                />
                {t('Lag saker fra varsler')}
              </label>

              <label className="settings-check">
                <input
                  type="checkbox"
                  checked={draft.notifyTeams}
                  onChange={(e) => update(rule, { notifyTeams: e.target.checked })}
                />
                {t('Varsle Teams om nye og gjenåpnede saker')}
              </label>

              <div className="settings-form-actions">
                <button
                  type="button"
                  disabled={!drafts[rule.source] || saving !== null}
                  onClick={() => void save(rule.source)}
                >
                  {saving === rule.source ? t('Lagrer...') : t('Lagre')}
                </button>
              </div>
            </div>
          )
        })
      )}

      <datalist id="rule-categories">
        {categories.map((c) => <option key={c} value={c} />)}
      </datalist>
    </div>
  )
}

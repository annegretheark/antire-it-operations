import { useState } from 'react'
import { errorText, useList, useSend } from './settingsApi'
import { t } from '../../i18n'

type TemplateTask = {
  id: number
  type: string
  title: string
  description: string | null
  category: string | null
  offsetDays: number
  sortOrder: number
  serviceCatalogItemId: number | null
  isActive: boolean
}

type Draft = Omit<TemplateTask, 'id' | 'serviceCatalogItemId'> & { description: string; category: string }

const EMPTY: Draft = {
  type: 'onboarding',
  title: '',
  description: '',
  category: '',
  offsetDays: 0,
  sortOrder: 100,
  isActive: true,
}

function dayText(days: number, type: string): string {
  if (type === 'onboarding') {
    if (days === 0) return t('På startdato')
    return days < 0 ? t('{n} dager før startdato', { n: -days }) : t('{n} dager etter startdato', { n: days })
  }
  if (days === 0) return t('På sluttdato')
  return days < 0 ? t('{n} dager før sluttdato', { n: -days }) : t('{n} dager etter sluttdato', { n: days })
}

// Maler for onboarding og offboarding. Endringer gjelder nye prosesser.
export default function LifecycleTemplatesSettings() {
  const send = useSend()
  const [reloadKey, setReloadKey] = useState(0)
  const templates = useList<TemplateTask>('/api/lifecycletemplates', reloadKey)
  const [editingId, setEditingId] = useState<number | null>(null)
  const [draft, setDraft] = useState<Draft>(EMPTY)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')

  function edit(tpl: TemplateTask) {
    setEditingId(tpl.id)
    setDraft({ ...tpl, description: tpl.description ?? '', category: tpl.category ?? '' })
    setError('')
  }

  async function save(body: Draft, id: number | null) {
    setBusy(true)
    setError('')

    try {
      if (id === null) await send('POST', '/api/lifecycletemplates', body)
      else await send('PUT', `/api/lifecycletemplates/${id}`, body)
      setEditingId(null)
      setDraft(EMPTY)
      setReloadKey((k) => k + 1)
    } catch (e) {
      setError(errorText(e))
    } finally {
      setBusy(false)
    }
  }

  const update = <K extends keyof Draft>(key: K, value: Draft[K]) =>
    setDraft((previous) => ({ ...previous, [key]: value }))

  return (
    <div className="helpdesk-card">
      <h3>{t('Onboarding-maler')}</h3>
      <p className="audit-intro">
        {t('Oppgavene som legges inn når onboarding eller offboarding starter. Fristen regnes fra startdato (onboarding) eller sluttdato (offboarding). Endringer gjelder nye prosesser.')}
      </p>

      <form
        className="helpdesk-form settings-form"
        onSubmit={(e) => {
          e.preventDefault()
          void save(draft, editingId)
        }}
      >
        <h4>{editingId === null ? t('Ny oppgave') : t('Endre «{name}»', { name: draft.title })}</h4>
        <div className="settings-form-grid">
          <div>
            <label htmlFor="tpl-type">{t('Type')}</label>
            <select id="tpl-type" value={draft.type} onChange={(e) => update('type', e.target.value)}>
              <option value="onboarding">Onboarding</option>
              <option value="offboarding">Offboarding</option>
            </select>
          </div>
          <div>
            <label htmlFor="tpl-title">{t('Oppgave')}</label>
            <input id="tpl-title" value={draft.title} maxLength={200} required
              onChange={(e) => update('title', e.target.value)} />
          </div>
          <div>
            <label htmlFor="tpl-offset">{t('Frist (dager, minus = før)')}</label>
            <input id="tpl-offset" type="number" min={-365} max={365} value={draft.offsetDays}
              onChange={(e) => update('offsetDays', Number(e.target.value))} />
          </div>
          <div>
            <label htmlFor="tpl-category">{t('Ansvarlig område (valgfritt)')}</label>
            <input id="tpl-category" value={draft.category} maxLength={100}
              onChange={(e) => update('category', e.target.value)} />
          </div>
          <div>
            <label htmlFor="tpl-order">{t('Rekkefølge')}</label>
            <input id="tpl-order" type="number" value={draft.sortOrder}
              onChange={(e) => update('sortOrder', Number(e.target.value))} />
          </div>
        </div>
        <div>
          <label htmlFor="tpl-description">{t('Beskrivelse')}</label>
          <textarea id="tpl-description" rows={2} maxLength={1000} value={draft.description}
            onChange={(e) => update('description', e.target.value)} />
        </div>
        <div className="settings-form-actions">
          <button type="submit" disabled={busy}>{editingId === null ? t('Legg til') : t('Lagre')}</button>
          {editingId !== null && (
            <button type="button" className="secondary" onClick={() => { setEditingId(null); setDraft(EMPTY) }}>
              {t('Avbryt')}
            </button>
          )}
        </div>
      </form>

      {error && <p role="alert" className="settings-error">{error}</p>}

      {['onboarding', 'offboarding'].map((type) => (
        <div key={type}>
          <h4>{type === 'onboarding' ? 'Onboarding' : 'Offboarding'}</h4>
          <div className="audit-table-wrapper">
            <table className="audit-table">
              <thead>
                <tr>
                  <th>{t('Oppgave')}</th>
                  <th>{t('Frist')}</th>
                  <th>{t('Område')}</th>
                  <th />
                </tr>
              </thead>
              <tbody>
                {templates.items
                  .filter((tpl) => tpl.type === type)
                  .map((tpl) => (
                    <tr key={tpl.id} className={tpl.isActive ? undefined : 'settings-muted'}>
                      <td>
                        <strong>{tpl.title}</strong>
                        {!tpl.isActive && ` (${t('av')})`}
                        {tpl.description && <div className="settings-muted">{tpl.description}</div>}
                      </td>
                      <td className="audit-nowrap">{dayText(tpl.offsetDays, tpl.type)}</td>
                      <td>{tpl.category ?? '–'}</td>
                      <td className="audit-nowrap">
                        <button type="button" className="settings-row-action" onClick={() => edit(tpl)}>
                          {t('Endre')}
                        </button>
                        <button
                          type="button"
                          className="settings-row-action"
                          disabled={busy}
                          onClick={() =>
                            void save(
                              { ...tpl, description: tpl.description ?? '', category: tpl.category ?? '', isActive: !tpl.isActive },
                              tpl.id
                            )
                          }
                        >
                          {tpl.isActive ? t('Slå av') : t('Slå på')}
                        </button>
                      </td>
                    </tr>
                  ))}
              </tbody>
            </table>
          </div>
        </div>
      ))}
    </div>
  )
}

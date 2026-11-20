import { useMemo, useState } from 'react'
import {
  PRIORITIES,
  SERVICE_TYPE_LABELS,
  useServiceCatalog,
  type ServiceCatalogItem,
  type ServiceType,
} from '../../serviceCatalog'
import { errorText, useEntraGroups, useSend } from './settingsApi'
import { priorityLabel } from '../../tickets'
import { t } from '../../i18n'

type Draft = {
  name: string
  category: string
  description: string
  type: ServiceType
  defaultPriority: string
  requiresApproval: boolean
  entraGroupId: string
  isActive: boolean
}

const EMPTY: Draft = {
  name: '',
  category: '',
  description: '',
  type: 'incident',
  defaultPriority: 'normal',
  requiresApproval: false,
  entraGroupId: '',
  isActive: true,
}

function toDraft(item: ServiceCatalogItem): Draft {
  return {
    name: item.name,
    category: item.category,
    description: item.description ?? '',
    type: item.type,
    defaultPriority: item.defaultPriority,
    requiresApproval: item.requiresApproval,
    entraGroupId: item.entraGroupId ?? '',
    isActive: item.isActive,
  }
}

// Tjenestekatalogen: det brukerne velger i Helpdesk og Tilgangsbestillinger.
// Tjenester slettes ikke, men kan deaktiveres.
export default function ServiceCatalogSettings() {
  const send = useSend()
  const [reloadKey, setReloadKey] = useState(0)
  const catalog = useServiceCatalog('includeInactive=true', reloadKey)
  const entra = useEntraGroups()

  const [editingId, setEditingId] = useState<number | null>(null)
  const [draft, setDraft] = useState<Draft>(EMPTY)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState('')
  const [message, setMessage] = useState('')

  const categories = useMemo(
    () => [...new Set(catalog.items.map((i) => i.category))].sort(),
    [catalog.items]
  )

  function update<K extends keyof Draft>(key: K, value: Draft[K]) {
    setDraft((previous) => ({ ...previous, [key]: value }))
  }

  function startEdit(item: ServiceCatalogItem) {
    setEditingId(item.id)
    setDraft(toDraft(item))
    setError('')
    setMessage('')
  }

  function reset() {
    setEditingId(null)
    setDraft(EMPTY)
  }

  async function save() {
    setSaving(true)
    setError('')
    setMessage('')

    try {
      const body = {
        ...draft,
        description: draft.description.trim() || null,
        entraGroupId: draft.type === 'access' ? draft.entraGroupId.trim() || null : null,
      }

      if (editingId === null) await send('POST', '/api/servicecatalog', body)
      else await send('PUT', `/api/servicecatalog/${editingId}`, body)

      setMessage(editingId === null ? t('Tjenesten er lagt til.') : t('Tjenesten er oppdatert.'))
      reset()
      setReloadKey((k) => k + 1)
    } catch (e) {
      setError(errorText(e))
    } finally {
      setSaving(false)
    }
  }

  async function setActive(item: ServiceCatalogItem, isActive: boolean) {
    setError('')
    setMessage('')

    try {
      await send('PUT', `/api/servicecatalog/${item.id}`, { ...toDraft(item), isActive })
      setReloadKey((k) => k + 1)
    } catch (e) {
      setError(errorText(e))
    }
  }

  return (
    <div className="helpdesk-card">
      <h3>{t('Tjenestekatalog')}</h3>
      <p className="audit-intro">
        {t('Tjenestene brukerne velger når de melder inn en sak eller bestiller tilgang. Kategorien styrer automatisk tildeling sammen med ansvarsområder og kategorigrupper.')}
      </p>

      <form
        className="helpdesk-form settings-form"
        onSubmit={(e) => {
          e.preventDefault()
          void save()
        }}
      >
        <h4>{editingId === null ? t('Ny tjeneste') : t('Endre «{name}»', { name: draft.name })}</h4>

        <div className="settings-form-grid">
          <div>
            <label htmlFor="sc-name">{t('Navn')}</label>
            <input
              id="sc-name"
              value={draft.name}
              maxLength={100}
              onChange={(e) => update('name', e.target.value)}
              required
            />
          </div>

          <div>
            <label htmlFor="sc-category">{t('Kategori')}</label>
            <input
              id="sc-category"
              list="sc-categories"
              value={draft.category}
              maxLength={100}
              onChange={(e) => update('category', e.target.value)}
              required
            />
            <datalist id="sc-categories">
              {categories.map((c) => (
                <option key={c} value={c} />
              ))}
            </datalist>
          </div>

          <div>
            <label htmlFor="sc-type">{t('Type')}</label>
            <select
              id="sc-type"
              value={draft.type}
              onChange={(e) => update('type', e.target.value as ServiceType)}
            >
              {Object.entries(SERVICE_TYPE_LABELS).map(([value, label]) => (
                <option key={value} value={value}>
                  {t(label)}
                </option>
              ))}
            </select>
          </div>

          <div>
            <label htmlFor="sc-priority">{t('Standard prioritet')}</label>
            <select
              id="sc-priority"
              value={draft.defaultPriority}
              onChange={(e) => update('defaultPriority', e.target.value)}
            >
              {PRIORITIES.map((p) => (
                <option key={p} value={p}>
                  {priorityLabel(p)}
                </option>
              ))}
            </select>
          </div>

          {draft.type === 'access' && (
            <div>
              <label htmlFor="sc-group">{t('Entra-gruppe-ID (gir tilgangen)')}</label>
              <input
                id="sc-group"
                value={draft.entraGroupId}
                onChange={(e) => update('entraGroupId', e.target.value)}
                list="sc-entra-groups"
                placeholder={t('Velg gruppe eller lim inn gruppe-ID')}
              />
              <datalist id="sc-entra-groups">
                {entra.groups.map((g) => (
                  <option key={g.id} value={g.id}>
                    {g.displayName}
                  </option>
                ))}
              </datalist>
              {draft.entraGroupId && entra.nameOf(draft.entraGroupId) && (
                <p className="service-description">{entra.nameOf(draft.entraGroupId)}</p>
              )}
            </div>
          )}
        </div>

        <div>
          <label htmlFor="sc-description">{t('Beskrivelse')}</label>
          <textarea
            id="sc-description"
            value={draft.description}
            maxLength={500}
            rows={2}
            onChange={(e) => update('description', e.target.value)}
          />
        </div>

        <label className="settings-check">
          <input
            type="checkbox"
            checked={draft.requiresApproval}
            onChange={(e) => update('requiresApproval', e.target.checked)}
          />
          {t('Krever godkjenning')}
        </label>

        <label className="settings-check">
          <input
            type="checkbox"
            checked={draft.isActive}
            onChange={(e) => update('isActive', e.target.checked)}
          />
          {t('Aktiv (vises for brukerne)')}
        </label>

        <div className="settings-form-actions">
          <button type="submit" disabled={saving}>
            {saving ? t('Lagrer...') : editingId === null ? t('Legg til') : t('Lagre endringer')}
          </button>
          {editingId !== null && (
            <button type="button" className="secondary" onClick={reset}>
              {t('Avbryt')}
            </button>
          )}
        </div>
      </form>

      {error && <p role="alert" className="settings-error">{error}</p>}
      {message && <p className="settings-message">{message}</p>}
      {catalog.error && <p role="alert" className="settings-error">{catalog.error}</p>}

      {catalog.loading ? (
        <p>{t('Henter tjenester...')}</p>
      ) : (
        <div className="audit-table-wrapper">
          <table className="audit-table">
            <thead>
              <tr>
                <th>{t('Navn')}</th>
                <th>{t('Kategori')}</th>
                <th>{t('Type')}</th>
                <th>{t('Prioritet')}</th>
                <th>{t('Godkjenning')}</th>
                <th>{t('Status')}</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {catalog.items.map((item) => (
                <tr key={item.id} className={item.isActive ? undefined : 'settings-muted'}>
                  <td>
                    <strong>{item.name}</strong>
                    {item.description && <div>{item.description}</div>}
                  </td>
                  <td>{item.category}</td>
                  <td className="audit-nowrap">{SERVICE_TYPE_LABELS[item.type] ? t(SERVICE_TYPE_LABELS[item.type]) : item.type}</td>
                  <td>{priorityLabel(item.defaultPriority)}</td>
                  <td>{item.requiresApproval ? t('Ja') : t('Nei')}</td>
                  <td>{item.isActive ? t('Aktiv') : t('Deaktivert')}</td>
                  <td className="audit-nowrap">
                    <button
                      type="button"
                      className="settings-row-action"
                      onClick={() => startEdit(item)}
                    >
                      {t('Endre')}
                    </button>
                    <button
                      type="button"
                      className="settings-row-action"
                      onClick={() => void setActive(item, !item.isActive)}
                    >
                      {item.isActive ? t('Deaktiver') : t('Aktiver')}
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  )
}

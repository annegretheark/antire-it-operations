import { useMemo, useState } from 'react'
import { useServiceCatalog } from '../../serviceCatalog'
import { errorText, useEntraGroups, useList, useSend } from './settingsApi'
import { t } from '../../i18n'

type CategoryGroup = {
  id: number
  category: string
  entraGroupId: string
}

// Kategorigrupper: Entra-gruppene som gir rett til å få saker i en kategori.
// Medlemskapet sjekkes i Entra hver gang en sak tildeles automatisk.
export default function CategoryGroupsSettings() {
  const send = useSend()
  const [reloadKey, setReloadKey] = useState(0)
  const groups = useList<CategoryGroup>('/api/ticketcategorygroups', reloadKey)
  const catalog = useServiceCatalog('includeInactive=true')
  const entra = useEntraGroups()

  const [category, setCategory] = useState('')
  const [groupId, setGroupId] = useState('')
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState('')

  const categories = useMemo(
    () => [...new Set(catalog.items.map((i) => i.category))].sort(),
    [catalog.items]
  )

  async function run(action: () => Promise<unknown>) {
    setSaving(true)
    setError('')

    try {
      await action()
      setReloadKey((k) => k + 1)
    } catch (e) {
      setError(errorText(e))
    } finally {
      setSaving(false)
    }
  }

  return (
    <div className="helpdesk-card">
      <h3>{t('Kategorigrupper')}</h3>
      <p className="audit-intro">
        {t('En sak i en kategori tildeles bare ansatte som er medlem av en av gruppene for kategorien (direkte eller via en annen gruppe). Uten grupper tildeles ikke saker i kategorien automatisk.')}
      </p>

      <form
        className="helpdesk-form settings-form"
        onSubmit={(e) => {
          e.preventDefault()
          void run(async () => {
            await send('POST', '/api/ticketcategorygroups', {
              category,
              entraGroupId: groupId.trim(),
            })
            setGroupId('')
          })
        }}
      >
        <div className="settings-form-grid">
          <div>
            <label htmlFor="group-category">{t('Kategori')}</label>
            <select
              id="group-category"
              value={category}
              onChange={(e) => setCategory(e.target.value)}
              required
            >
              <option value="">{t('Velg kategori')}</option>
              {categories.map((c) => (
                <option key={c}>{c}</option>
              ))}
            </select>
          </div>

          <div>
            <label htmlFor="group-id">{t('Entra-gruppe-ID')}</label>
            <input
              id="group-id"
              value={groupId}
              onChange={(e) => setGroupId(e.target.value)}
              list="entra-groups"
              placeholder={t('Velg gruppe eller lim inn gruppe-ID')}
              required
            />
          </div>
        </div>

        <datalist id="entra-groups">
          {entra.groups.map((g) => (
            <option key={g.id} value={g.id}>
              {g.displayName}
            </option>
          ))}
        </datalist>

        <div className="settings-form-actions">
          <button type="submit" disabled={saving || !category || !groupId.trim()}>
            {t('Legg til')}
          </button>
        </div>
      </form>

      {error && <p role="alert" className="settings-error">{error}</p>}
      {groups.error && <p role="alert" className="settings-error">{groups.error}</p>}

      {groups.loading ? (
        <p>{t('Henter kategorigrupper...')}</p>
      ) : groups.items.length === 0 ? (
        <p>{t('Ingen kategorigrupper registrert.')}</p>
      ) : (
        <div className="audit-table-wrapper">
          <table className="audit-table">
            <thead>
              <tr>
                <th>{t('Kategori')}</th>
                <th>{t('Entra-gruppe')}</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {groups.items.map((g) => (
                <tr key={g.id}>
                  <td>{g.category}</td>
                  <td>
                    {entra.nameOf(g.entraGroupId) && <strong>{entra.nameOf(g.entraGroupId)}</strong>}
                    <div><code>{g.entraGroupId}</code></div>
                  </td>
                  <td>
                    <button
                      type="button"
                      className="settings-row-action"
                      disabled={saving}
                      onClick={() =>
                        void run(() => send('DELETE', `/api/ticketcategorygroups/${g.id}`))
                      }
                    >
                      {t('Fjern')}
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

import { useMemo, useState } from 'react'
import { useServiceCatalog } from '../../serviceCatalog'
import { errorText, useList, useSend } from './settingsApi'
import { t } from '../../i18n'

type Responsibility = {
  id: number
  employeeId: number
  employeeName: string
  employeeStatus: string
  category: string
  canBeAssigned: boolean
}

type Employee = {
  id: number
  firstName: string
  lastName: string
  status: string
}

// Ansvarsområder: hvem som kan få saker i en kategori automatisk. Den ansatte
// må også være medlem av en av kategorigruppene i Entra.
export default function ResponsibilitiesSettings() {
  const send = useSend()
  const [reloadKey, setReloadKey] = useState(0)
  const responsibilities = useList<Responsibility>('/api/employeeresponsibilities', reloadKey)
  const employees = useList<Employee>('/api/employees')
  const catalog = useServiceCatalog('includeInactive=true')

  const [employeeId, setEmployeeId] = useState('')
  const [category, setCategory] = useState('')
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState('')

  const activeEmployees = useMemo(
    () =>
      employees.items
        .filter((e) => e.status === 'Aktiv')
        .sort((a, b) => `${a.firstName} ${a.lastName}`.localeCompare(`${b.firstName} ${b.lastName}`)),
    [employees.items]
  )

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

  async function add() {
    await run(async () => {
      await send('POST', '/api/employeeresponsibilities', {
        employeeId: Number(employeeId),
        category,
        canBeAssigned: true,
      })
      setEmployeeId('')
    })
  }

  return (
    <div className="helpdesk-card">
      <h3>{t('Ansvarsområder')}</h3>
      <p className="audit-intro">
        {t('Ansatte med et ansvarsområde kan få nye saker i kategorien automatisk, så sant de også er medlem av en av kategorigruppene i Entra ID.')}
      </p>

      <form
        className="helpdesk-form settings-form"
        onSubmit={(e) => {
          e.preventDefault()
          void add()
        }}
      >
        <div className="settings-form-grid">
          <div>
            <label htmlFor="resp-employee">{t('Ansatt')}</label>
            <select
              id="resp-employee"
              value={employeeId}
              onChange={(e) => setEmployeeId(e.target.value)}
              required
            >
              <option value="">{t('Velg ansatt')}</option>
              {activeEmployees.map((e) => (
                <option key={e.id} value={e.id}>
                  {`${e.firstName} ${e.lastName}`.trim()}
                </option>
              ))}
            </select>
          </div>

          <div>
            <label htmlFor="resp-category">{t('Kategori')}</label>
            <select
              id="resp-category"
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
        </div>

        <div className="settings-form-actions">
          <button type="submit" disabled={saving || !employeeId || !category}>
            {t('Legg til')}
          </button>
        </div>
      </form>

      {error && <p role="alert" className="settings-error">{error}</p>}
      {responsibilities.error && (
        <p role="alert" className="settings-error">{responsibilities.error}</p>
      )}

      {responsibilities.loading ? (
        <p>{t('Henter ansvarsområder...')}</p>
      ) : responsibilities.items.length === 0 ? (
        <p>{t('Ingen ansvarsområder registrert.')}</p>
      ) : (
        <div className="audit-table-wrapper">
          <table className="audit-table">
            <thead>
              <tr>
                <th>{t('Kategori')}</th>
                <th>{t('Ansatt')}</th>
                <th>{t('Automatisk tildeling')}</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {responsibilities.items.map((r) => (
                <tr key={r.id}>
                  <td>{r.category}</td>
                  <td>
                    {r.employeeName}
                    {r.employeeStatus !== 'Aktiv' && (
                      <span className="settings-muted"> ({t(r.employeeStatus).toLowerCase()})</span>
                    )}
                  </td>
                  <td>
                    <label className="settings-check">
                      <input
                        type="checkbox"
                        checked={r.canBeAssigned}
                        disabled={saving}
                        onChange={(e) =>
                          void run(() =>
                            send('PATCH', `/api/employeeresponsibilities/${r.id}`, {
                              canBeAssigned: e.target.checked,
                            })
                          )
                        }
                      />
                      {r.canBeAssigned ? t('På') : t('Av')}
                    </label>
                  </td>
                  <td>
                    <button
                      type="button"
                      className="settings-row-action"
                      disabled={saving}
                      onClick={() =>
                        void run(() => send('DELETE', `/api/employeeresponsibilities/${r.id}`))
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

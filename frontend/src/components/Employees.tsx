
import { useEffect, useState } from 'react'
import { apiUrl, useAuthHeaders } from '../apiClient'
import { useCurrentUser } from '../useCurrentUser'
import EmployeeAssets from './EmployeeAssets'
import { useResource } from './settings/settingsApi'
import {
  entraMismatch,
  entraStatusLabel,
  m365UserLink,
  type EmployeeEntraStatus,
  type EmployeeEntraStatuses,
} from '../entra'
import { t } from '../i18n'

type Employee = {
  id: number
  employeeNumber: string | null
  firstName: string
  lastName: string
  email: string
  department: string
  jobTitle: string
  startDate: string
  endDate: string | null
  status: string
}

const API_URL = apiUrl('/api/employees')
const SYNC_URL = apiUrl('/api/graph/sync-employees')

type SyncResult = {
  created: number
  updated: number
  totalFromEntra: number
}

const sortEmployees = (items: Employee[]) =>
  [...items].sort(
    (a, b) =>
      a.lastName.localeCompare(b.lastName, 'no') ||
      a.firstName.localeCompare(b.firstName, 'no')
  )

const toDate = (date: string) =>
  new Date(date + 'T12:00:00').toISOString()

export default function Employees() {
  const getAuthHeaders = useAuthHeaders()
  const { isEmployeeAdmin, isHelpdeskAgent, isHelpdeskAdmin } = useCurrentUser()
  const [employees, setEmployees] = useState<Employee[]>([])
  const [firstName, setFirstName] = useState('')
  const [employeeNumber, setEmployeeNumber] = useState('')
  const [lastName, setLastName] = useState('')
  const [email, setEmail] = useState('')
  const [department, setDepartment] = useState('')
  const [jobTitle, setJobTitle] = useState('')
  const [startDate, setStartDate] = useState('')

  const [editingId, setEditingId] = useState<number | null>(null)
  const [terminatingId, setTerminatingId] = useState<number | null>(null)
  const [endDate, setEndDate] = useState('')

  const [loading, setLoading] = useState(true)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState('')
  const [message, setMessage] = useState('')
  const [search, setSearch] = useState('')
  const [selectedId, setSelectedId] = useState<number | null>(null)
  const [showForm, setShowForm] = useState(false)
  const [syncing, setSyncing] = useState(false)

  // Kontostatus i Entra (Helpdesk.Agent og Helpdesk.Admin); hentes på nytt når listen endres
  const entra = useResource<EmployeeEntraStatuses>(
    isHelpdeskAgent ? '/api/employees/entra-status' : null,
    employees.length
  )
  const entraById = new Map<number, EmployeeEntraStatus>(
    (entra.data?.statuses ?? []).map((s) => [s.employeeId, s])
  )
  const columns = isHelpdeskAgent ? 5 : 4
  const gridColumns = isHelpdeskAgent
    ? 'minmax(160px, 1.5fr) minmax(110px, 1fr) minmax(110px, 1fr) minmax(70px, .7fr) minmax(90px, .8fr)'
    : 'minmax(160px, 1.5fr) minmax(110px, 1fr) minmax(110px, 1fr) minmax(70px, .7fr)'

  useEffect(() => {
    let cancelled = false

  async function loadEmployees() {
  try {
    const response = await fetch(API_URL, {
      headers: await getAuthHeaders(),
    })

    if (!response.ok) {
      throw new Error(`HTTP ${response.status}`)
    }

    const data: Employee[] = await response.json()

    if (!cancelled) {
      setEmployees(sortEmployees(data))
    }
  } catch (err) {
    if (!cancelled) {
      setError(
        err instanceof Error
          ? t('Kunne ikke hente ansatte: {error}', { error: err.message })
          : t('Kunne ikke hente ansatte.')
      )
    }
  } finally {
    if (!cancelled) {
      setLoading(false)
    }
  }
}

    void loadEmployees()

    return () => {
      cancelled = true
    }
  }, [getAuthHeaders])

  // Hent ansatte fra Microsoft Entra ID og oppdater listen
  async function syncFromEntra() {
    if (syncing) return

    setSyncing(true)
    setError('')
    setMessage('')

    try {
      const response = await fetch(SYNC_URL, {
        method: 'POST',
        headers: await getAuthHeaders(),
      })

      if (!response.ok) {
        throw new Error(`HTTP ${response.status}`)
      }

      const result: SyncResult = await response.json()

      const listResponse = await fetch(API_URL, {
        headers: await getAuthHeaders(),
      })

      if (listResponse.ok) {
        const data: Employee[] = await listResponse.json()
        setEmployees(sortEmployees(data))
      }

      setMessage(
        t('Synkronisert fra Entra: {created} nye, {updated} oppdatert ({total} brukere med avdeling og stilling).', {
          created: result.created,
          updated: result.updated,
          total: result.totalFromEntra,
        })
      )
    } catch (err) {
      setError(
        err instanceof Error
          ? t('Kunne ikke synkronisere fra Entra: {error}', { error: err.message })
          : t('Kunne ikke synkronisere fra Entra.')
      )
    } finally {
      setSyncing(false)
    }
  }

  function clearForm() {
    setFirstName('')
    setEmployeeNumber('')
    setLastName('')
    setEmail('')
    setDepartment('')
    setJobTitle('')
    setStartDate('')
    setEditingId(null)
    setShowForm(false)
  }

  function editEmployee(employee: Employee) {
    setEmployeeNumber(employee.employeeNumber ?? '')
    setEditingId(employee.id)
    setShowForm(true)
    setTerminatingId(null)
    setFirstName(employee.firstName)
    setLastName(employee.lastName)
    setEmail(employee.email)
    setDepartment(employee.department)
    setJobTitle(employee.jobTitle)
    setStartDate(employee.startDate.slice(0, 10))
    setError('')
    setMessage('')
    window.scrollTo({ top: 0, behavior: 'smooth' })
  }

  async function saveEmployee() {
    if (
      saving ||
      !firstName.trim() ||
      !lastName.trim() ||
      !email.trim() ||
      !startDate
    ) return

    setSaving(true)
    setError('')
    setMessage('')

    const editing = employees.find((e) => e.id === editingId)

    const payload = {
      firstName: firstName.trim(),
      lastName: lastName.trim(),
      email: email.trim(),
      employeeNumber: editingId !== null
  ? (employeeNumber.trim() || null)
  : null,
      department: department.trim(),
      jobTitle: jobTitle.trim(),
      startDate: toDate(startDate),
      endDate: editing?.endDate ?? null,
      status: editing?.status ?? 'Aktiv',
    }

    try {
      const response = await fetch(
        editingId === null ? API_URL : `${API_URL}/${editingId}`,
        {
          method: editingId === null ? 'POST' : 'PUT',
          headers: await getAuthHeaders(),
          body: JSON.stringify(payload),
        }
      )

      if (!response.ok) {
  if (response.status === 409) {
    throw new Error(t('Ansattnummeret er allerede i bruk.'))
  }
  throw new Error(t('Kunne ikke lagre ansattinformasjonen.'))
}

      const saved: Employee = await response.json()

      setEmployees((previous) =>
        sortEmployees(
          editingId === null
            ? [...previous, saved]
            : previous.map((e) => e.id === saved.id ? saved : e)
        )
      )

      setMessage(
        editingId === null
          ? t('Ansatt er registrert.')
          : t('Ansattinformasjonen er oppdatert.')
      )
         clearForm()
    } catch (err) {
      setError(
        err instanceof Error
          ? err.message
          : t('Kunne ikke lagre ansattinformasjonen.')
      )
    } finally {
      setSaving(false)
    }
  }

  async function terminateEmployee(id: number) {
    if (saving || !endDate) return

    const employee = employees.find((e) => e.id === id)
    if (!employee) return

    if (endDate < employee.startDate.slice(0, 10)) {
      setError(t('Sluttdato kan ikke være før startdato.'))
      return
    }

    if (!window.confirm(
      t('Registrere {name} som sluttet?', { name: `${employee.firstName} ${employee.lastName}` })
    )) return

    setSaving(true)
    setError('')
    setMessage('')

    try {
      const response = await fetch(`${API_URL}/${id}/terminate`, {
        method: 'PATCH',
        headers: await getAuthHeaders(),
        body: JSON.stringify({ endDate: toDate(endDate) }),
      })

if (!response.ok) {
  throw new Error(t('Kunne ikke avslutte arbeidsforholdet.'))
}

      const updated: Employee = await response.json()

      setEmployees((previous) =>
        previous.map((e) => e.id === id ? updated : e)
      )

      setTerminatingId(null)
      setEndDate('')
      setMessage(t('Ansatt er registrert som sluttet.'))
    } catch {
      setError(t('Kunne ikke registrere sluttdato.'))
    } finally {
      setSaving(false)
    }
  }

  const filteredEmployees = employees.filter((employee) =>
    [employee.firstName, employee.lastName, employee.email,
      employee.department, employee.jobTitle, employee.employeeNumber ?? '']
      .join(' ').toLocaleLowerCase('no')
      .includes(search.trim().toLocaleLowerCase('no'))
  )

  return (
    <section className="helpdesk">
      <div className="helpdesk-card">
        <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: 12, flexWrap: 'wrap' }}>
          <h3>{t('Ansatte ({n})', { n: employees.length })}</h3>
          {isEmployeeAdmin && (
          <button type="button" disabled={syncing} onClick={() => void syncFromEntra()}>
            {syncing ? t('Synkroniserer...') : t('Synkroniser fra Entra')}
          </button>
          )}
        </div>

        <label htmlFor="employee-search">{t('Søk etter ansatt')}</label>
        <input
          id="employee-search"
          type="search"
          placeholder={t('Navn, e-post, avdeling eller ansattnummer')}
          value={search}
          onChange={(e) => setSearch(e.target.value)}
          style={{ width: '100%', maxWidth: 480, marginBottom: 16 }}
        />

        {error && <p role="alert">{error}</p>}
        {message && <p role="status">{message}</p>}

        {loading ? (
          <p>{t('Henter ansatte...')}</p>
        ) : filteredEmployees.length === 0 ? (
          <p>{employees.length === 0 ? t('Ingen ansatte registrert.') : t('Ingen treff på søket.')}</p>
        ) : (
          <div style={{ overflowX: 'auto' }}>
            <table style={{ width: '100%', borderCollapse: 'collapse', textAlign: 'left' }}>
              <thead>
                <tr>
                  <th style={{ padding: 10 }}>{t('Navn')}</th>
                  <th style={{ padding: 10 }}>{t('Avdeling')}</th>
                  <th style={{ padding: 10 }}>{t('Stilling')}</th>
                  <th style={{ padding: 10 }}>{t('Status')}</th>
                  {isHelpdeskAgent && <th style={{ padding: 10 }}>{t('Entra')}</th>}
                </tr>
              </thead>
              <tbody>
                {filteredEmployees.map((employee) => {
                  const account = entraById.get(employee.id)
                  const mismatch = account ? entraMismatch(employee.status, account.status) : null
                  return (
                  <tr key={employee.id}>
                    <td colSpan={columns} style={{ padding: 0, borderTop: '1px solid #8885' }}>
                      <button
                        type="button"
                        aria-expanded={selectedId === employee.id}
                        onClick={() => {
                          setSelectedId(selectedId === employee.id ? null : employee.id)
                          setTerminatingId(null)
                        }}
                        style={{ width: '100%', display: 'grid', gridTemplateColumns: gridColumns, gap: 8, padding: '12px 10px', textAlign: 'left', background: 'transparent', border: 0, color: 'inherit', cursor: 'pointer', font: 'inherit' }}
                      >
                        <span>{selectedId === employee.id ? '▾' : '▸'} {employee.firstName} {employee.lastName}</span>
                        <span>{employee.department || '–'}</span>
                        <span>{employee.jobTitle || '–'}</span>
                        <span>{t(employee.status)}</span>
                        {isHelpdeskAgent && (
                          <span
                            className={mismatch ? 'settings-error' : account?.status === 'enabled' ? undefined : 'settings-muted'}
                            title={mismatch ?? undefined}
                          >
                            {account ? entraStatusLabel(account.status) : entra.loading ? '…' : '–'}
                            {mismatch && ' ⚠'}
                          </span>
                        )}
                      </button>
                      {selectedId === employee.id && (
                        <div style={{ padding: '12px 16px 20px', borderTop: '1px solid #8883' }}>
                          <p><strong>{t('Ansattnummer')}:</strong> {employee.employeeNumber ?? t('Ikke tildelt')}</p>
                          <p><strong>{t('E-post')}:</strong> {employee.email}</p>
                          <p><strong>{t('Avdeling')}:</strong> {employee.department}</p>
                          <p><strong>{t('Stilling')}:</strong> {employee.jobTitle}</p>
                          <p><strong>{t('Startdato')}:</strong> {employee.startDate.slice(0, 10)}</p>
                          <p><strong>{t('Status')}:</strong> {t(employee.status)}</p>
                          {employee.endDate && <p><strong>{t('Sluttdato')}:</strong> {employee.endDate.slice(0, 10)}</p>}
                          {isHelpdeskAgent && account && (
                            <p>
                              <strong>{t('Konto i Entra')}:</strong>{' '}
                              <span className={mismatch ? 'settings-error' : undefined}>{entraStatusLabel(account.status)}</span>
                              {mismatch && <span className="settings-error"> – {mismatch}</span>}
                              {isHelpdeskAdmin && account.entraObjectId && account.status !== 'missing' && (
                                <> · <a href={m365UserLink(account.entraObjectId)}>{t('Åpne i Microsoft 365')}</a></>
                              )}
                            </p>
                          )}
                          {isHelpdeskAgent && account?.status === 'unknown' && entra.data?.unavailableReason && (
                            <p className="settings-muted">{entra.data.unavailableReason}</p>
                          )}
                          {isHelpdeskAgent && (
                            <EmployeeAssets employeeId={employee.id} endDate={employee.endDate} />
                          )}
                          {isEmployeeAdmin && (
                          <div className="approval-actions">
                            <button type="button" disabled={saving} onClick={() => editEmployee(employee)}>{t('Rediger')}</button>
                            {employee.status === 'Aktiv' && (
                              <button type="button" disabled={saving} onClick={() => {
                                setTerminatingId(employee.id)
                                setEndDate('')
                                setError('')
                                setMessage('')
                              }}>{t('Registrer slutt')}</button>
                            )}
                          </div>
                          )}
                          {terminatingId === employee.id && (
                            <div className="helpdesk-form">
                              <label htmlFor={`enddate-${employee.id}`}>{t('Sluttdato')}</label>
                              <input id={`enddate-${employee.id}`} type="date"
                                min={employee.startDate.slice(0, 10)} value={endDate}
                                onChange={(e) => setEndDate(e.target.value)} />
                              <div className="approval-actions">
                                <button type="button" disabled={saving || !endDate}
                                  onClick={() => void terminateEmployee(employee.id)}>{t('Bekreft slutt')}</button>
                                <button type="button" disabled={saving}
                                  onClick={() => setTerminatingId(null)}>{t('Avbryt')}</button>
                              </div>
                            </div>
                          )}
                        </div>
                      )}
                    </td>
                  </tr>
                  )
                })}
              </tbody>
            </table>
          </div>
        )}
      </div>

      {isEmployeeAdmin && showForm && (
        <div className="helpdesk-card">
          <h3>{editingId === null ? t('Registrer ansatt') : t('Rediger ansatt')}</h3>
          <form className="helpdesk-form" onSubmit={(e) => {
            e.preventDefault()
            void saveEmployee()
          }}>
            {editingId !== null && (
              <>
                <label htmlFor="employee-number">{t('Ansattnummer')}</label>
                <input id="employee-number" type="text" value={employeeNumber}
                  onChange={(e) => setEmployeeNumber(e.target.value)} placeholder={t('Ansattnummer')} />
              </>
            )}
            <label htmlFor="employee-firstname">{t('Fornavn')}</label>
            <input id="employee-firstname" value={firstName} onChange={(e) => setFirstName(e.target.value)} required />
            <label htmlFor="employee-lastname">{t('Etternavn')}</label>
            <input id="employee-lastname" value={lastName} onChange={(e) => setLastName(e.target.value)} required />
            <label htmlFor="employee-email">{t('E-post')}</label>
            <input id="employee-email" type="email" value={email} onChange={(e) => setEmail(e.target.value)} required />
            <label htmlFor="employee-department">{t('Avdeling')}</label>
            <input id="employee-department" value={department} onChange={(e) => setDepartment(e.target.value)} />
            <label htmlFor="employee-jobtitle">{t('Stilling')}</label>
            <input id="employee-jobtitle" value={jobTitle} onChange={(e) => setJobTitle(e.target.value)} />
            <label htmlFor="employee-startdate">{t('Startdato')}</label>
            <input id="employee-startdate" type="date" value={startDate} onChange={(e) => setStartDate(e.target.value)} required />
            <div className="approval-actions">
              <button type="submit" disabled={saving}>
                {saving ? t('Lagrer...') : editingId === null ? t('Registrer ansatt') : t('Lagre endringer')}
              </button>
              <button type="button" disabled={saving} onClick={clearForm}>{t('Avbryt')}</button>
            </div>
          </form>
        </div>
      )}
    </section>
  )
}

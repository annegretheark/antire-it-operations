
import { useEffect, useState } from 'react'
import { useMsal } from '@azure/msal-react'
import { loginRequest } from '../msalConfig'

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

const API_URL = 'http://localhost:5086/api/employees'

const sortEmployees = (items: Employee[]) =>
  [...items].sort(
    (a, b) =>
      a.lastName.localeCompare(b.lastName, 'no') ||
      a.firstName.localeCompare(b.firstName, 'no')
  )

const toDate = (date: string) =>
  new Date(date + 'T12:00:00').toISOString()

export default function Employees() {
  const { instance, accounts } = useMsal()

async function getAuthHeaders(): Promise<HeadersInit> {
  const account = instance.getActiveAccount() ?? accounts[0]

  if (!account) {
    throw new Error('Du må logge inn med Microsoft.')
  }

  const token = await instance.acquireTokenSilent({
    ...loginRequest,
    account,
  })

  return {
    Authorization: `Bearer ${token.accessToken}`,
    'Content-Type': 'application/json',
  }
}
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
          ? `Kunne ikke hente ansatte: ${err.message}`
          : 'Kunne ikke hente ansatte.'
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
  }, [])

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
    throw new Error('Ansattnummeret er allerede i bruk.')
  }
  throw new Error('Kunne ikke lagre ansattinformasjonen.')
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
          ? 'Ansatt er registrert.'
          : 'Ansattinformasjonen er oppdatert.'
      )
         clearForm()
    } catch (err) {
      setError(
        err instanceof Error
          ? err.message
          : 'Kunne ikke lagre ansattinformasjonen.'
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
      setError('Sluttdato kan ikke være før startdato.')
      return
    }

    if (!window.confirm(
      `Registrere ${employee.firstName} ${employee.lastName} som sluttet?`
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
  throw new Error('Kunne ikke avslutte arbeidsforholdet.')
}

      const updated: Employee = await response.json()

      setEmployees((previous) =>
        previous.map((e) => e.id === id ? updated : e)
      )

      setTerminatingId(null)
      setEndDate('')
      setMessage('Ansatt er registrert som sluttet.')
    } catch {
      setError('Kunne ikke registrere sluttdato.')
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
          <h3>Ansatte ({employees.length})</h3>
          <button type="button" onClick={() => {
            if (showForm) {
              clearForm()
            } else {
              clearForm()
              setShowForm(true)
              setError('')
              setMessage('')
            }
          }}>
            {showForm ? 'Lukk skjema' : '+ Ny ansatt'}
          </button>
        </div>

        <label htmlFor="employee-search">Søk etter ansatt</label>
        <input
          id="employee-search"
          type="search"
          placeholder="Navn, e-post, avdeling eller ansattnummer"
          value={search}
          onChange={(e) => setSearch(e.target.value)}
          style={{ width: '100%', maxWidth: 480, marginBottom: 16 }}
        />

        {error && <p role="alert">{error}</p>}
        {message && <p role="status">{message}</p>}

        {loading ? (
          <p>Henter ansatte...</p>
        ) : filteredEmployees.length === 0 ? (
          <p>{employees.length === 0 ? 'Ingen ansatte registrert.' : 'Ingen treff på søket.'}</p>
        ) : (
          <div style={{ overflowX: 'auto' }}>
            <table style={{ width: '100%', borderCollapse: 'collapse', textAlign: 'left' }}>
              <thead>
                <tr>
                  <th style={{ padding: 10 }}>Navn</th>
                  <th style={{ padding: 10 }}>Avdeling</th>
                  <th style={{ padding: 10 }}>Stilling</th>
                  <th style={{ padding: 10 }}>Status</th>
                </tr>
              </thead>
              <tbody>
                {filteredEmployees.map((employee) => (
                  <tr key={employee.id}>
                    <td colSpan={4} style={{ padding: 0, borderTop: '1px solid #8885' }}>
                      <button
                        type="button"
                        aria-expanded={selectedId === employee.id}
                        onClick={() => {
                          setSelectedId(selectedId === employee.id ? null : employee.id)
                          setTerminatingId(null)
                        }}
                        style={{ width: '100%', display: 'grid', gridTemplateColumns: 'minmax(160px, 1.5fr) minmax(110px, 1fr) minmax(110px, 1fr) minmax(70px, .7fr)', gap: 8, padding: '12px 10px', textAlign: 'left', background: 'transparent', border: 0, color: 'inherit', cursor: 'pointer', font: 'inherit' }}
                      >
                        <span>{selectedId === employee.id ? '▾' : '▸'} {employee.firstName} {employee.lastName}</span>
                        <span>{employee.department || '–'}</span>
                        <span>{employee.jobTitle || '–'}</span>
                        <span>{employee.status}</span>
                      </button>
                      {selectedId === employee.id && (
                        <div style={{ padding: '12px 16px 20px', borderTop: '1px solid #8883' }}>
                          <p><strong>Ansattnummer:</strong> {employee.employeeNumber ?? 'Ikke tildelt'}</p>
                          <p><strong>E-post:</strong> {employee.email}</p>
                          <p><strong>Avdeling:</strong> {employee.department}</p>
                          <p><strong>Stilling:</strong> {employee.jobTitle}</p>
                          <p><strong>Startdato:</strong> {employee.startDate.slice(0, 10)}</p>
                          <p><strong>Status:</strong> {employee.status}</p>
                          {employee.endDate && <p><strong>Sluttdato:</strong> {employee.endDate.slice(0, 10)}</p>}
                          <div className="approval-actions">
                            <button type="button" disabled={saving} onClick={() => editEmployee(employee)}>Rediger</button>
                            {employee.status === 'Aktiv' && (
                              <button type="button" disabled={saving} onClick={() => {
                                setTerminatingId(employee.id)
                                setEndDate('')
                                setError('')
                                setMessage('')
                              }}>Registrer slutt</button>
                            )}
                          </div>
                          {terminatingId === employee.id && (
                            <div className="helpdesk-form">
                              <label htmlFor={`enddate-${employee.id}`}>Sluttdato</label>
                              <input id={`enddate-${employee.id}`} type="date"
                                min={employee.startDate.slice(0, 10)} value={endDate}
                                onChange={(e) => setEndDate(e.target.value)} />
                              <div className="approval-actions">
                                <button type="button" disabled={saving || !endDate}
                                  onClick={() => void terminateEmployee(employee.id)}>Bekreft slutt</button>
                                <button type="button" disabled={saving}
                                  onClick={() => setTerminatingId(null)}>Avbryt</button>
                              </div>
                            </div>
                          )}
                        </div>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>

      {showForm && (
        <div className="helpdesk-card">
          <h3>{editingId === null ? 'Registrer ansatt' : 'Rediger ansatt'}</h3>
          <form className="helpdesk-form" onSubmit={(e) => {
            e.preventDefault()
            void saveEmployee()
          }}>
            {editingId !== null && (
              <>
                <label htmlFor="employee-number">Ansattnummer</label>
                <input id="employee-number" type="text" value={employeeNumber}
                  onChange={(e) => setEmployeeNumber(e.target.value)} placeholder="Ansattnummer" />
              </>
            )}
            <label htmlFor="employee-firstname">Fornavn</label>
            <input id="employee-firstname" value={firstName} onChange={(e) => setFirstName(e.target.value)} required />
            <label htmlFor="employee-lastname">Etternavn</label>
            <input id="employee-lastname" value={lastName} onChange={(e) => setLastName(e.target.value)} required />
            <label htmlFor="employee-email">E-post</label>
            <input id="employee-email" type="email" value={email} onChange={(e) => setEmail(e.target.value)} required />
            <label htmlFor="employee-department">Avdeling</label>
            <input id="employee-department" value={department} onChange={(e) => setDepartment(e.target.value)} />
            <label htmlFor="employee-jobtitle">Stilling</label>
            <input id="employee-jobtitle" value={jobTitle} onChange={(e) => setJobTitle(e.target.value)} />
            <label htmlFor="employee-startdate">Startdato</label>
            <input id="employee-startdate" type="date" value={startDate} onChange={(e) => setStartDate(e.target.value)} required />
            <div className="approval-actions">
              <button type="submit" disabled={saving}>
                {saving ? 'Lagrer...' : editingId === null ? 'Registrer ansatt' : 'Lagre endringer'}
              </button>
              <button type="button" disabled={saving} onClick={clearForm}>Avbryt</button>
            </div>
          </form>
        </div>
      )}
    </section>
  )
}

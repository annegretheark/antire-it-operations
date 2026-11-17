import { useMemo, useState } from 'react'
import './Helpdesk.css'
import { useCurrentUser } from '../useCurrentUser'
import { formatDate } from '../assets'
import { errorText, useList, useResource, useSend } from './settings/settingsApi'

type Summary = {
  id: number
  employeeId: number
  employeeName: string
  type: string
  referenceDate: string
  status: string
  createdAt: string
  completedAt: string | null
  taskCount: number
  doneCount: number
  overdueCount: number
}

type Task = {
  id: number
  title: string
  description: string | null
  category: string | null
  dueDate: string
  completedAt: string | null
  completedByName: string | null
  note: string | null
}

type Details = {
  process: Summary
  tasks: Task[]
  assets: { id: number; assetTag: string; model: string | null; status: string }[]
  accesses: { accessRequestId: number; systemName: string; status: string; groupId: string | null }[]
}

type Employee = { id: number; firstName: string; lastName: string; status: string }

const TYPE_LABELS: Record<string, string> = { onboarding: 'Onboarding', offboarding: 'Offboarding' }
const STATUS_LABELS: Record<string, string> = { active: 'Pågår', completed: 'Fullført', cancelled: 'Avbrutt' }

// Onboarding og offboarding (Helpdesk.Agent og Helpdesk.Admin)
export default function Lifecycle() {
  const { isHelpdeskAgent } = useCurrentUser()
  const [status, setStatus] = useState('active')
  const [type, setType] = useState('')
  const [reloadKey, setReloadKey] = useState(0)
  const [openId, setOpenId] = useState<number | null>(null)

  const url = useMemo(() => {
    const params = new URLSearchParams()
    if (status) params.set('status', status)
    if (type) params.set('type', type)
    return `/api/lifecycle${params.size ? `?${params}` : ''}`
  }, [status, type])

  const processes = useList<Summary>(isHelpdeskAgent ? url : null, reloadKey)
  const reload = () => setReloadKey((k) => k + 1)

  if (!isHelpdeskAgent) {
    return (
      <section className="helpdesk">
        <div className="helpdesk-card">
          <h3>Onboarding / Offboarding</h3>
          <p>Du trenger rollen Helpdesk.Agent eller Helpdesk.Admin.</p>
        </div>
      </section>
    )
  }

  return (
    <section className="helpdesk">
      <StartProcess onStarted={reload} />

      <div className="helpdesk-card">
        <h3>Prosesser</h3>
        <p className="audit-intro">
          Onboarding starter automatisk når en ansatt registreres med startdato fram i tid, og
          offboarding når sluttdato registreres. Oppgavene kommer fra malene under Innstillinger →
          Onboarding-maler.
        </p>

        <div className="asset-filters">
          <select aria-label="Status" value={status} onChange={(e) => setStatus(e.target.value)}>
            <option value="active">Pågår</option>
            <option value="completed">Fullført</option>
            <option value="cancelled">Avbrutt</option>
            <option value="">Alle</option>
          </select>
          <select aria-label="Type" value={type} onChange={(e) => setType(e.target.value)}>
            <option value="">Onboarding og offboarding</option>
            <option value="onboarding">Onboarding</option>
            <option value="offboarding">Offboarding</option>
          </select>
        </div>

        {processes.error && <p role="alert" className="settings-error">{processes.error}</p>}

        {processes.loading ? (
          <p>Henter prosesser...</p>
        ) : processes.items.length === 0 ? (
          <p>Ingen prosesser.</p>
        ) : (
          <div className="audit-table-wrapper">
            <table className="audit-table">
              <thead>
                <tr>
                  <th>Ansatt</th>
                  <th>Type</th>
                  <th>Dato</th>
                  <th>Fremdrift</th>
                  <th>Status</th>
                </tr>
              </thead>
              <tbody>
                {processes.items.map((p) => (
                  <ProcessRow
                    key={p.id}
                    process={p}
                    open={openId === p.id}
                    onToggle={() => setOpenId(openId === p.id ? null : p.id)}
                    onChanged={reload}
                  />
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>
    </section>
  )
}

function ProcessRow({
  process,
  open,
  onToggle,
  onChanged,
}: {
  process: Summary
  open: boolean
  onToggle: () => void
  onChanged: () => void
}) {
  const percent = process.taskCount ? Math.round((process.doneCount / process.taskCount) * 100) : 0

  return (
    <>
      <tr>
        <td>
          <button type="button" className="asset-link" aria-expanded={open} onClick={onToggle}>
            {open ? '▾' : '▸'} {process.employeeName}
          </button>
        </td>
        <td>{TYPE_LABELS[process.type] ?? process.type}</td>
        <td className="audit-nowrap">{formatDate(process.referenceDate)}</td>
        <td>
          <div className="progress" aria-label={`${process.doneCount} av ${process.taskCount} oppgaver`}>
            <div className="progress-bar" style={{ width: `${percent}%` }} />
          </div>
          <span className="settings-muted">
            {process.doneCount} av {process.taskCount}
            {process.overdueCount > 0 && <span className="settings-error"> · {process.overdueCount} forfalt</span>}
          </span>
        </td>
        <td>{STATUS_LABELS[process.status] ?? process.status}</td>
      </tr>
      {open && (
        <tr>
          <td colSpan={5}>
            <ProcessDetails id={process.id} onChanged={onChanged} />
          </td>
        </tr>
      )}
    </>
  )
}

function ProcessDetails({ id, onChanged }: { id: number; onChanged: () => void }) {
  const send = useSend()
  const [reloadKey, setReloadKey] = useState(0)
  const details = useResource<Details>(`/api/lifecycle/${id}`, reloadKey)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [today] = useState(() => new Date().toISOString().slice(0, 10))

  async function run(action: () => Promise<unknown>) {
    setBusy(true)
    setError('')

    try {
      await action()
      setReloadKey((k) => k + 1)
      onChanged()
    } catch (e) {
      setError(errorText(e))
    } finally {
      setBusy(false)
    }
  }

  if (details.loading && !details.data) return <p>Henter oppgaver...</p>
  if (!details.data) return <p className="settings-error">{details.error || 'Kunne ikke hente prosessen.'}</p>

  const { process, tasks, assets, accesses } = details.data
  const editable = process.status !== 'cancelled'

  return (
    <div className="lifecycle-details">
      <ul className="lifecycle-tasks">
        {tasks.map((t) => {
          const overdue = !t.completedAt && t.dueDate.slice(0, 10) < today

          return (
            <li key={t.id} className={t.completedAt ? 'done' : undefined}>
              <label className="settings-check">
                <input
                  type="checkbox"
                  checked={Boolean(t.completedAt)}
                  disabled={busy || !editable}
                  onChange={(e) =>
                    void run(() => send('PATCH', `/api/lifecycle/tasks/${t.id}`, { done: e.target.checked }))
                  }
                />
                <span>
                  <strong>{t.title}</strong>
                  <span className={overdue ? 'settings-error' : 'settings-muted'}>
                    {' '}· frist {formatDate(t.dueDate)}{overdue && ' (forfalt)'}
                  </span>
                  {t.category && <span className="settings-muted"> · {t.category}</span>}
                  {t.description && <div className="settings-muted">{t.description}</div>}
                  {t.completedAt && (
                    <div className="settings-muted">
                      Utført {formatDate(t.completedAt)} av {t.completedByName}
                    </div>
                  )}
                </span>
              </label>
            </li>
          )
        })}
      </ul>

      {process.type === 'offboarding' && (
        <div className="asset-checklist">
          <strong>Utstyr som skal leveres inn</strong>
          {assets.length === 0 ? (
            <p>Alt utstyr er levert inn.</p>
          ) : (
            <ul>
              {assets.map((a) => (
                <li key={a.id}>
                  <span>☐ {a.assetTag}{a.model ? ` · ${a.model}` : ''}</span>
                  <button
                    type="button"
                    className="settings-row-action"
                    disabled={busy}
                    onClick={() => void run(() => send('PATCH', `/api/assets/${a.id}/assign`, { employeeId: null }))}
                  >
                    Mottatt
                  </button>
                </li>
              ))}
            </ul>
          )}

          <strong className="lifecycle-subheading">Tilganger fra tilgangsbestillinger</strong>
          {accesses.length === 0 ? (
            <p>Ingen tilganger registrert.</p>
          ) : (
            <ul>
              {accesses.map((a) => (
                <li key={a.accessRequestId}>
                  <span>
                    {a.status === 'Tilgang fjernet' ? '☑' : '☐'} {a.systemName}
                    {!a.groupId && <span className="settings-muted"> (fjernes manuelt)</span>}
                    {a.status === 'Tilgang fjernet' && <span className="settings-muted"> (fjernet)</span>}
                  </span>
                  {a.groupId && a.status === 'Fullført' && (
                    <button
                      type="button"
                      className="settings-row-action"
                      disabled={busy}
                      onClick={() => void run(() => send('POST', `/api/lifecycle/${id}/revoke/${a.accessRequestId}`))}
                    >
                      Fjern fra Entra-gruppen
                    </button>
                  )}
                </li>
              ))}
            </ul>
          )}
        </div>
      )}

      {error && <p role="alert" className="settings-error">{error}</p>}

      {process.status === 'active' && (
        <div className="settings-form-actions">
          <button
            type="button"
            className="settings-row-action"
            disabled={busy}
            onClick={() => {
              if (window.confirm('Avbryte prosessen? Oppgavene beholdes, men kan ikke krysses av.')) {
                void run(() => send('POST', `/api/lifecycle/${id}/cancel`))
              }
            }}
          >
            Avbryt prosessen
          </button>
        </div>
      )}
    </div>
  )
}

function StartProcess({ onStarted }: { onStarted: () => void }) {
  const send = useSend()
  const employees = useList<Employee>('/api/employees')
  const [employeeId, setEmployeeId] = useState('')
  const [type, setType] = useState('onboarding')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [message, setMessage] = useState('')

  async function start() {
    setBusy(true)
    setError('')
    setMessage('')

    try {
      await send('POST', '/api/lifecycle', { employeeId: Number(employeeId), type })
      setMessage(`${TYPE_LABELS[type]} er startet.`)
      setEmployeeId('')
      onStarted()
    } catch (e) {
      setError(errorText(e))
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="helpdesk-card">
      <h3>Start manuelt</h3>
      <div className="asset-filters">
        <select aria-label="Ansatt" value={employeeId} onChange={(e) => setEmployeeId(e.target.value)}>
          <option value="">Velg ansatt</option>
          {employees.items.map((e) => (
            <option key={e.id} value={e.id}>
              {`${e.firstName} ${e.lastName}`.trim()}{e.status !== 'Aktiv' ? ` (${e.status.toLowerCase()})` : ''}
            </option>
          ))}
        </select>
        <select aria-label="Type" value={type} onChange={(e) => setType(e.target.value)}>
          <option value="onboarding">Onboarding</option>
          <option value="offboarding">Offboarding</option>
        </select>
        <button type="button" className="asset-primary" disabled={busy || !employeeId} onClick={() => void start()}>
          Start
        </button>
      </div>
      {error && <p role="alert" className="settings-error">{error}</p>}
      {message && <p className="settings-message">{message}</p>}
    </div>
  )
}

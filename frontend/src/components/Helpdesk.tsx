
import { useEffect, useMemo, useState } from 'react'
import './Helpdesk.css'
import { apiUrl, useAuthHeaders } from '../apiClient'
import { useCurrentUser } from '../useCurrentUser'
import TicketHistory from './TicketHistory'
import { SERVICE_TYPE_LABELS, useServiceCatalog } from '../serviceCatalog'

type Ticket = {
  id: number
  category: string
  description: string
  status: string
  priority: string
  assignedToEmployeeId: number | null
  serviceCatalogItemId: number | null
}

type Employee = {
  id: number
  firstName: string
  lastName: string
  status: string
}
const API_URL = apiUrl('/api/tickets')

export default function Helpdesk() {
  const getAuthHeaders = useAuthHeaders()
  const { isHelpdeskAgent } = useCurrentUser()

  // Tjenester brukerne kan melde inn saker på (tilgang bestilles på egen side)
  const catalog = useServiceCatalog()
  const services = useMemo(
    () => catalog.items.filter((s) => s.type !== 'access'),
    [catalog.items]
  )
  const [serviceId, setServiceId] = useState<number | null>(null)
  const selectedService = services.find((s) => s.id === serviceId) ?? services[0]
  const selectedServiceId = selectedService?.id ?? null
  const serviceNames = useMemo(
    () => new Map(catalog.items.map((s) => [s.id, s.name])),
    [catalog.items]
  )

  // Tjenestenavnet når saken har en tjeneste (og kategorien er en annen)
  function ticketTitle(ticket: Ticket): string {
    const name = ticket.serviceCatalogItemId
      ? serviceNames.get(ticket.serviceCatalogItemId)
      : undefined
    return name && name !== ticket.category ? `${name} (${ticket.category})` : ticket.category
  }

  const [description, setDescription] = useState('')
  const [tickets, setTickets] = useState<Ticket[]>([])
  const [employees, setEmployees] = useState<Employee[]>([])
  const [filter, setFilter] = useState('Alle')
  const [loading, setLoading] = useState(true)
  const [saving, setSaving] = useState(false)
  const [updatingId, setUpdatingId] = useState<number | null>(null)
  const [error, setError] = useState('')
  // Saker der historikken er åpen
  const [openHistory, setOpenHistory] = useState<Set<number>>(new Set())

  const employeeNames = useMemo(
    () => new Map(employees.map((e) => [e.id, `${e.firstName} ${e.lastName}`.trim()])),
    [employees]
  )

  function toggleHistory(ticketId: number) {
    setOpenHistory((previous) => {
      const next = new Set(previous)
      if (next.has(ticketId)) next.delete(ticketId)
      else next.add(ticketId)
      return next
    })
  }

  const filteredTickets = tickets.filter((ticket) => {
    if (filter === 'Åpne') return ticket.status !== 'Løst'
    if (filter === 'Løste') return ticket.status === 'Løst'
    return true
  })

  useEffect(() => {
    async function loadTickets() {
      try {
        const response = await fetch(API_URL, {
  headers: await getAuthHeaders(),
})
        if (!response.ok) throw new Error()

        const data: Ticket[] = await response.json()
        setTickets(data)
        const employeeResponse = await fetch(
  apiUrl('/api/employees'),
  {
    headers: await getAuthHeaders(),
  }
)

if (!employeeResponse.ok) {
  throw new Error('Kunne ikke hente ansatte.')
}

const employeeData: Employee[] = await employeeResponse.json()
setEmployees(employeeData)

      } catch {
        setError('Kunne ikke kontakte backend.')
      } finally {
        setLoading(false)
      }
    }

    void loadTickets()
  }, [getAuthHeaders])

  async function createTicket() {
    if (!description.trim() || selectedServiceId === null || saving) return

    setSaving(true)
    setError('')

    try {
      const response = await fetch(API_URL, {
        method: 'POST',
       headers: await getAuthHeaders(),
        body: JSON.stringify({
          serviceCatalogItemId: selectedServiceId,
          description: description.trim(),
        }),
      })

      if (!response.ok) throw new Error()

      const ticket: Ticket = await response.json()

      setTickets((previous) => [ticket, ...previous])
      setDescription('')
    } catch {
      setError('Kunne ikke lagre saken.')
    } finally {
      setSaving(false)
    }
  }

  async function updateTicket(
    ticketId: number,
    endpoint: string,
    payload: object
  ) {
    if (updatingId !== null) return

    setUpdatingId(ticketId)
    setError('')

    try {
      const response = await fetch(
        `${API_URL}/${ticketId}/${endpoint}`,
        {
          method: 'PATCH',
          headers: await getAuthHeaders(),
          body: JSON.stringify(payload),
        }
      )

      if (!response.ok) throw new Error()

      const updated: Ticket = await response.json()

      setTickets((previous) =>
        previous.map((ticket) =>
          ticket.id === updated.id ? updated : ticket
        )
      )
    } catch {
      setError('Kunne ikke oppdatere saken.')
    } finally {
      setUpdatingId(null)
    }
  }

  return (
    <section className="helpdesk">

      <div className="helpdesk-card">
        <h3>Opprett ny sak</h3>

        <form
          className="helpdesk-form"
          onSubmit={(e) => {
            e.preventDefault()
            void createTicket()
          }}
        >
          <label htmlFor="ticket-service">
            Hva gjelder det?
          </label>

          <select
            id="ticket-service"
            value={selectedServiceId ?? ''}
            onChange={(e) => setServiceId(Number(e.target.value))}
            disabled={catalog.loading || services.length === 0}
            required
          >
            {catalog.loading && <option value="">Henter tjenester...</option>}
            {(['incident', 'request'] as const).map((type) => {
              const group = services.filter((s) => s.type === type)
              if (group.length === 0) return null

              return (
                <optgroup key={type} label={SERVICE_TYPE_LABELS[type]}>
                  {group.map((service) => (
                    <option key={service.id} value={service.id}>
                      {service.name}
                    </option>
                  ))}
                </optgroup>
              )
            })}
          </select>

          {selectedService?.description && (
            <p className="service-description">{selectedService.description}</p>
          )}

          {catalog.error && <p role="alert">{catalog.error}</p>}

          <label htmlFor="ticket-description">
            Beskrivelse
          </label>

          <textarea
            id="ticket-description"
            value={description}
            onChange={(e) => setDescription(e.target.value)}
            rows={4}
            placeholder="Beskriv saken..."
            required
          />

          <button
            type="submit"
            disabled={saving || !description.trim() || selectedServiceId === null}
          >
            {saving ? 'Lagrer...' : 'Opprett sak'}
          </button>
        </form>
      </div>

      {error && (
        <p role="alert" style={{ color: '#dc2626' }}>
          {error}
        </p>
      )}

      <div className="helpdesk-card">
        <h3>Registrerte saker</h3>

        <div className="ticket-filter">
          <label htmlFor="ticket-filter">
            Vis saker:
          </label>

          <select
            id="ticket-filter"
            value={filter}
            onChange={(e) => setFilter(e.target.value)}
          >
            <option value="Alle">Alle</option>
            <option value="Åpne">Åpne</option>
            <option value="Løste">Løste</option>
          </select>
        </div>

        {loading ? (
          <p>Henter saker...</p>
        ) : tickets.length === 0 ? (
          <p>Ingen saker registrert.</p>
        ) : filteredTickets.length === 0 ? (
          <p>Ingen saker i dette filteret.</p>
        ) : (
          filteredTickets.map((ticket) => (
            <div
              className="helpdesk-ticket"
              key={ticket.id}
            >
              <h4>
                #{ticket.id} – {ticketTitle(ticket)}
              </h4>

              <p>{ticket.description}</p>

              <div className="ticket-status">
                <label htmlFor={`status-${ticket.id}`}>
                  <strong>Status: </strong>
                </label>

                <select
                  id={`status-${ticket.id}`}
                  value={ticket.status}
                  disabled={!isHelpdeskAgent || updatingId !== null}
                  onChange={(e) => {
                    void updateTicket(
                      ticket.id,
                      'status',
                      { status: e.target.value }
                    )
                  }}
                >
                  <option value="Ny">Ny</option>
                  <option value="Under behandling">
                    Under behandling
                  </option>
                  <option value="Løst">Løst</option>
                </select>
              </div>

              <div className="ticket-priority">
                <label htmlFor={`priority-${ticket.id}`}>
                  <strong>Prioritet: </strong>
                </label>

                <select
                  id={`priority-${ticket.id}`}
                  className={`priority-${
                    ticket.priority === 'Høy'
                      ? 'hoy'
                      : (ticket.priority ?? 'Normal').toLowerCase()
                  }`}
                  value={ticket.priority ?? 'Normal'}
                  disabled={!isHelpdeskAgent || updatingId !== null}
                  onChange={(e) => {
                    void updateTicket(
                      ticket.id,
                      'priority',
                      { priority: e.target.value }
                    )
                  }}
                >
                  <option value="Lav">Lav</option>
                  <option value="Normal">Normal</option>
                  <option value="Høy">Høy</option>
                  <option value="Kritisk">Kritisk</option>
                </select>
              </div>

              <div className="ticket-assigned">
                <label htmlFor={`assigned-${ticket.id}`}>
                  <strong>Ansvarlig: </strong>
                </label>

                <select
                  id={`assigned-${ticket.id}`}
                  value={ticket.assignedToEmployeeId ?? ''}
                  disabled={!isHelpdeskAgent || updatingId !== null}
                  onChange={(e) => {
                    const value = e.target.value

                    void updateTicket(
                      ticket.id,
                      'assign',
                      { assignedToEmployeeId: value ? Number(value) : null }
                    )
                  }}
                >
                  <option value="">Ikke tildelt</option>
                  {employees
                    .filter(
                      (employee) =>
                        employee.status === 'Aktiv' ||
                        employee.id === ticket.assignedToEmployeeId
                    )
                    .map((employee) => (
                      <option key={employee.id} value={employee.id}>
                        {employee.firstName} {employee.lastName}
                        {employee.status !== 'Aktiv' ? ' (sluttet)' : ''}
                      </option>
                    ))}
                </select>
              </div>

              <button
                type="button"
                className="ticket-history-toggle"
                aria-expanded={openHistory.has(ticket.id)}
                onClick={() => toggleHistory(ticket.id)}
              >
                {openHistory.has(ticket.id) ? 'Skjul historikk' : 'Vis historikk'}
              </button>

              {openHistory.has(ticket.id) && (
                <TicketHistory
                  ticketId={ticket.id}
                  employees={employeeNames}
                  version={`${ticket.status}|${ticket.priority}|${ticket.assignedToEmployeeId ?? ''}`}
                />
              )}
            </div>
          ))
        )}
      </div>
    </section>
  )
}

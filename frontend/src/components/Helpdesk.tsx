
import { useEffect, useState } from 'react'
import './Helpdesk.css'
import { useMsal } from '@azure/msal-react'
import { loginRequest } from '../msalConfig'

type Ticket = {
  id: number
  category: string
  description: string
  status: string
  priority: string
  assignedTo: string | null
  assignedToEmployeeId: number | null
}

type Employee = {
  id: number
  firstName: string
  lastName: string
}
const API_URL = 'http://localhost:5086/api/tickets'

export default function Helpdesk() {
    const { instance, accounts } = useMsal()

  async function getAuthHeaders(): Promise<HeadersInit> {
    const account = instance.getActiveAccount() ?? accounts[0]

    if (!account) {
      throw new Error('Du må logge inn med Microsoft.')
    }

    const tokenResponse = await instance.acquireTokenSilent({
      ...loginRequest,
      account,
    })

    return {
      Authorization: `Bearer ${tokenResponse.accessToken}`,
      'Content-Type': 'application/json',
    }
  }
  const [category, setCategory] = useState('Feil')
  const [description, setDescription] = useState('')
  const [tickets, setTickets] = useState<Ticket[]>([])
  const [employees, setEmployees] = useState<Employee[]>([])
  const [filter, setFilter] = useState('Alle')
  const [loading, setLoading] = useState(true)
  const [saving, setSaving] = useState(false)
  const [updatingId, setUpdatingId] = useState<number | null>(null)
  const [error, setError] = useState('')

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
  'http://localhost:5086/api/employees',
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
  }, [])

  async function createTicket() {
    if (!description.trim() || saving) return

    setSaving(true)
    setError('')

    try {
      const response = await fetch(API_URL, {
        method: 'POST',
       headers: await getAuthHeaders(),
        body: JSON.stringify({
          category,
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
          <label htmlFor="ticket-category">
            Kategori
          </label>

          <select
            id="ticket-category"
            value={category}
            onChange={(e) => setCategory(e.target.value)}
          >
            <option>Feil</option>
            <option>Tilgangsbestilling</option>
            <option>Bestilling av utstyr</option>
            <option>Annet</option>
          </select>

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
            disabled={saving || !description.trim()}
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
                #{ticket.id} – {ticket.category}
              </h4>

              <p>{ticket.description}</p>

              <div className="ticket-status">
                <label htmlFor={`status-${ticket.id}`}>
                  <strong>Status: </strong>
                </label>

                <select
                  id={`status-${ticket.id}`}
                  value={ticket.status}
                  disabled={updatingId !== null}
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
                  disabled={updatingId !== null}
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
                {ticket.assignedToEmployeeId !== null && (
  <p>
    <strong>Automatisk tildelt: </strong>
    {(() => {
      const employee = employees.find(
        (e) => e.id === ticket.assignedToEmployeeId
      )

      return employee
        ? `${employee.firstName} ${employee.lastName}`
        : 'Ukjent ansatt'
    })()}
  </p>
)}
                <label htmlFor={`assigned-${ticket.id}`}>
                  <strong>Ansvarlig konsulent: </strong>
                </label>

                <input
                  id={`assigned-${ticket.id}`}
                  key={`${ticket.id}-${ticket.assignedTo ?? ''}`}
                  type="text"
                  defaultValue={ticket.assignedTo ?? ''}
                  placeholder="Ikke tildelt"
                  disabled={updatingId !== null}
                  onBlur={(e) => {
                    const name = e.target.value.trim()

                    if (name !== (ticket.assignedTo ?? '')) {
                      void updateTicket(
                        ticket.id,
                        'assign',
                        { assignedTo: name || null }
                      )
                    }
                  }}
                  onKeyDown={(e) => {
                    if (e.key === 'Enter') {
                      e.currentTarget.blur()
                    }
                  }}
                />
              </div>
            </div>
          ))
        )}
      </div>
    </section>
  )
}

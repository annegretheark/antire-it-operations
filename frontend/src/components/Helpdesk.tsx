
import { useEffect, useMemo, useState } from 'react'
import './Helpdesk.css'
import { apiUrl, useAuthHeaders } from '../apiClient'
import { useCurrentUser } from '../useCurrentUser'
import TicketHistory from './TicketHistory'
import { SERVICE_TYPE_LABELS, useServiceCatalog } from '../serviceCatalog'
import {
  formatRelative,
  needsAttention,
  PRIORITIES,
  priorityLabel,
  SLA_LABELS,
  STATUSES,
  statusLabel,
  type SlaStatus,
} from '../tickets'
import { formatDateTime } from '../audit'
import { assetName, type Asset } from '../assets'
import { useList } from './settings/settingsApi'
import { ALERT_SOURCE_LABELS, type Alert } from '../alerts'

type Ticket = {
  id: number
  category: string
  description: string
  status: string
  priority: string
  assignedToEmployeeId: number | null
  serviceCatalogItemId: number | null
  responseDueAt: string | null
  resolutionDueAt: string | null
  firstRespondedAt: string | null
  resolvedAt: string | null
  slaStatus: SlaStatus
  assetId: number | null
  // manual | alert
  source: string
  alertId: number | null
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

  // Utstyr saken kan gjelde: eget utstyr, eller alt som ikke er kassert for agenter
  const assets = useList<Asset>('/api/assets')
  const selectableAssets = useMemo(
    () => assets.items.filter((a) => a.status !== 'retired'),
    [assets.items]
  )
  const assetTags = useMemo(
    () => new Map(assets.items.map((a) => [a.id, a.assetTag])),
    [assets.items]
  )
  const [assetId, setAssetId] = useState('')
  const [tickets, setTickets] = useState<Ticket[]>([])
  const [employees, setEmployees] = useState<Employee[]>([])
  const [filter, setFilter] = useState('Alle')
  const [loading, setLoading] = useState(true)
  const [saving, setSaving] = useState(false)
  const [updatingId, setUpdatingId] = useState<number | null>(null)
  const [error, setError] = useState('')
  // Saker der historikken er åpen
  const [openHistory, setOpenHistory] = useState<Set<number>>(new Set())

  // Sak fra lenken ?sak=123 (Teams-melding eller Varsler) markeres og vises
  const [focusId] = useState(() => {
    const id = Number(new URLSearchParams(window.location.search).get('sak'))
    return Number.isInteger(id) && id > 0 ? id : null
  })

  useEffect(() => {
    if (focusId !== null && tickets.some((t) => t.id === focusId)) {
      document.getElementById(`sak-${focusId}`)?.scrollIntoView({ behavior: 'smooth', block: 'center' })
    }
  }, [focusId, tickets])

  // Varselet bak saker fra varsler (bare for agent og admin, som kan se varsler)
  const alerts = useList<Alert>(isHelpdeskAgent ? '/api/alerts' : null)
  const alertsById = useMemo(() => new Map(alerts.items.map((a) => [a.id, a])), [alerts.items])

  function alertInfo(ticket: Ticket): string {
    const alert = ticket.alertId ? alertsById.get(ticket.alertId) : undefined
    if (!alert) return ''
    const forekomster = alert.occurrences === 1 ? '1 forekomst' : `${alert.occurrences} forekomster`
    return ` · ${ALERT_SOURCE_LABELS[alert.source] ?? alert.source} · ${forekomster}`
  }

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
    if (filter === 'Åpne') return ticket.status !== 'resolved'
    if (filter === 'Løste') return ticket.status === 'resolved'
    if (filter === 'SLA i fare') return needsAttention(ticket.slaStatus)
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
          assetId: assetId ? Number(assetId) : null,
          description: description.trim(),
        }),
      })

      if (!response.ok) throw new Error()

      const ticket: Ticket = await response.json()

      setTickets((previous) => [ticket, ...previous])
      setDescription('')
      setAssetId('')
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

          {selectableAssets.length > 0 && (
            <>
              <label htmlFor="ticket-asset">Gjelder utstyr (valgfritt)</label>
              <select
                id="ticket-asset"
                value={assetId}
                onChange={(e) => setAssetId(e.target.value)}
              >
                <option value="">Ikke valgt</option>
                {selectableAssets.map((asset) => (
                  <option key={asset.id} value={asset.id}>
                    {assetName(asset)}
                    {isHelpdeskAgent && asset.assignedToName ? ` (${asset.assignedToName})` : ''}
                  </option>
                ))}
              </select>
            </>
          )}

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
            <option value="SLA i fare">SLA i fare (brudd eller under 25 % igjen)</option>
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
              className={`helpdesk-ticket${ticket.id === focusId ? ' ticket-focus' : ''}`}
              id={`sak-${ticket.id}`}
              key={ticket.id}
            >
              <h4>
                #{ticket.id} – {ticketTitle(ticket)}
              </h4>

              {ticket.source === 'alert' && (
                <p className="ticket-alert-badge">Fra varsel{alertInfo(ticket)}</p>
              )}

              <TicketSla ticket={ticket} />

              {ticket.assetId && (
                <p className="service-description">
                  Utstyr: {assetTags.get(ticket.assetId) ?? `#${ticket.assetId}`}
                </p>
              )}

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
                  {STATUSES.map((status) => (
                    <option key={status} value={status}>
                      {statusLabel(status)}
                    </option>
                  ))}
                </select>
              </div>

              <div className="ticket-priority">
                <label htmlFor={`priority-${ticket.id}`}>
                  <strong>Prioritet: </strong>
                </label>

                <select
                  id={`priority-${ticket.id}`}
                  className={`priority-${ticket.priority}`}
                  value={ticket.priority}
                  disabled={!isHelpdeskAgent || updatingId !== null}
                  onChange={(e) => {
                    void updateTicket(
                      ticket.id,
                      'priority',
                      { priority: e.target.value }
                    )
                  }}
                >
                  {PRIORITIES.map((priority) => (
                    <option key={priority} value={priority}>
                      {priorityLabel(priority)}
                    </option>
                  ))}
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

// SLA-merke og frister for én sak
function TicketSla({ ticket }: { ticket: Ticket }) {
  if (ticket.slaStatus === 'none') return null

  const open = ticket.status !== 'resolved'

  return (
    <div className="ticket-sla">
      <span className={`sla-badge sla-${ticket.slaStatus}`}>
        SLA: {SLA_LABELS[ticket.slaStatus]}
      </span>

      {ticket.responseDueAt && (
        <span>
          Respons: {ticket.firstRespondedAt
            ? `gitt ${formatDateTime(ticket.firstRespondedAt)}`
            : `frist ${formatDateTime(ticket.responseDueAt)}${open ? ` (${formatRelative(ticket.responseDueAt)})` : ''}`}
        </span>
      )}

      {ticket.resolutionDueAt && (
        <span>
          Løsning: {ticket.resolvedAt
            ? `løst ${formatDateTime(ticket.resolvedAt)}`
            : `frist ${formatDateTime(ticket.resolutionDueAt)} (${formatRelative(ticket.resolutionDueAt)})`}
        </span>
      )}
    </div>
  )
}

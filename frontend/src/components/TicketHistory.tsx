import { useEffect, useState } from 'react'
import { apiUrl, useAuthHeaders } from '../apiClient'
import {
  actionLabel,
  describeChanges,
  formatDateTime,
  type AuditEvent,
  type EmployeeNames,
} from '../audit'
import { t } from '../i18n'

type Props = {
  ticketId: number
  employees: EmployeeNames
  // Endres når saken er oppdatert, slik at historikken hentes på nytt
  version: string
}

// Hvem endret hva og når på én sak
export default function TicketHistory({ ticketId, employees, version }: Props) {
  const getAuthHeaders = useAuthHeaders()
  const [events, setEvents] = useState<AuditEvent[] | null>(null)
  const [error, setError] = useState('')

  useEffect(() => {
    let cancelled = false

    async function load() {
      setError('')

      try {
        const response = await fetch(apiUrl(`/api/tickets/${ticketId}/history`), {
          headers: await getAuthHeaders(),
        })
        if (!response.ok) throw new Error()

        const data: AuditEvent[] = await response.json()
        if (!cancelled) setEvents(data)
      } catch {
        if (!cancelled) setError(t('Kunne ikke hente historikk.'))
      }
    }

    void load()

    return () => {
      cancelled = true
    }
  }, [ticketId, version, getAuthHeaders])

  if (error) return <p role="alert" className="ticket-history-error">{error}</p>
  if (events === null) return <p className="ticket-history-empty">{t('Henter historikk...')}</p>
  if (events.length === 0) {
    return <p className="ticket-history-empty">{t('Ingen historikk registrert ennå.')}</p>
  }

  return (
    <ol className="ticket-history">
      {events.map((event) => (
        <li key={event.id}>
          <div className="ticket-history-meta">
            <time dateTime={event.occurredAt}>{formatDateTime(event.occurredAt)}</time>
            {' · '}
            <strong>{event.actorName}</strong>
            {' · '}
            {actionLabel(event.action)}
          </div>
          {event.action !== 'created' && (
            <ul>
              {describeChanges(event, employees).map((line) => (
                <li key={line}>{line}</li>
              ))}
            </ul>
          )}
        </li>
      ))}
    </ol>
  )
}

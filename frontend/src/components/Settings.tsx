import { useCallback, useEffect, useMemo, useState } from 'react'
import { apiUrl, useAuthHeaders } from '../apiClient'
import { useCurrentUser } from '../useCurrentUser'
import ServiceCatalogSettings from './settings/ServiceCatalogSettings'
import ResponsibilitiesSettings from './settings/ResponsibilitiesSettings'
import CategoryGroupsSettings from './settings/CategoryGroupsSettings'
import SlaSettings from './settings/SlaSettings'
import {
  actionLabel,
  describeChanges,
  ENTITY_LABELS,
  formatDateTime,
  type AuditEvent,
  type EmployeeNames,
} from '../audit'

type Employee = {
  id: number
  firstName: string
  lastName: string
}

const PAGE_SIZE = 50

const TABS = [
  { key: 'catalog', label: 'Tjenestekatalog' },
  { key: 'responsibilities', label: 'Ansvarsområder' },
  { key: 'groups', label: 'Kategorigrupper' },
  { key: 'sla', label: 'SLA' },
  { key: 'audit', label: 'Revisjonslogg' },
] as const

type TabKey = (typeof TABS)[number]['key']

// Innstillinger (Helpdesk.Admin): tjenestekatalog, automatisk tildeling, SLA og revisjonslogg
export default function Settings() {
  const { isHelpdeskAdmin } = useCurrentUser()
  const [tab, setTab] = useState<TabKey>('catalog')

  if (!isHelpdeskAdmin) {
    return (
      <section className="helpdesk">
        <div className="helpdesk-card">
          <h3>Innstillinger</h3>
          <p>Du trenger rollen Helpdesk.Admin for å se innstillingene.</p>
        </div>
      </section>
    )
  }

  return (
    <section className="helpdesk">
      <div className="settings-tabs" role="tablist" aria-label="Innstillinger">
        {TABS.map((t) => (
          <button
            key={t.key}
            type="button"
            role="tab"
            aria-selected={tab === t.key}
            onClick={() => setTab(t.key)}
          >
            {t.label}
          </button>
        ))}
      </div>

      {tab === 'catalog' && <ServiceCatalogSettings />}
      {tab === 'responsibilities' && <ResponsibilitiesSettings />}
      {tab === 'groups' && <CategoryGroupsSettings />}
      {tab === 'sla' && <SlaSettings />}
      {tab === 'audit' && <AuditLog />}
    </section>
  )
}

function AuditLog() {
  const getAuthHeaders = useAuthHeaders()
  const [events, setEvents] = useState<AuditEvent[]>([])
  const [employees, setEmployees] = useState<Employee[]>([])
  const [entityType, setEntityType] = useState('')
  const [loading, setLoading] = useState(true)
  const [hasMore, setHasMore] = useState(false)
  const [error, setError] = useState('')

  const employeeNames: EmployeeNames = useMemo(
    () => new Map(employees.map((e) => [e.id, `${e.firstName} ${e.lastName}`.trim()])),
    [employees]
  )

  // Henter én side, nyeste først. before = ID til siste hendelse på forrige side.
  const fetchPage = useCallback(
    async (before: number | null): Promise<AuditEvent[]> => {
      const params = new URLSearchParams({ take: String(PAGE_SIZE) })
      if (entityType) params.set('entityType', entityType)
      if (before !== null) params.set('before', String(before))

      const response = await fetch(apiUrl(`/api/audit?${params}`), {
        headers: await getAuthHeaders(),
      })
      if (!response.ok) throw new Error()

      return response.json()
    },
    [entityType, getAuthHeaders]
  )

  // Første side, og på nytt når filteret endres (onChange setter loading)
  useEffect(() => {
    let cancelled = false

    async function loadFirstPage() {
      try {
        const page = await fetchPage(null)
        if (cancelled) return
        setEvents(page)
        setHasMore(page.length === PAGE_SIZE)
      } catch {
        if (!cancelled) setError('Kunne ikke hente revisjonsloggen.')
      } finally {
        if (!cancelled) setLoading(false)
      }
    }

    void loadFirstPage()

    return () => {
      cancelled = true
    }
  }, [fetchPage])

  async function loadMore() {
    setLoading(true)
    setError('')

    try {
      const page = await fetchPage(events[events.length - 1].id)
      setEvents((previous) => [...previous, ...page])
      setHasMore(page.length === PAGE_SIZE)
    } catch {
      setError('Kunne ikke hente revisjonsloggen.')
    } finally {
      setLoading(false)
    }
  }

  // Navn på ansatte i endringene (ansvarlig, meldt inn av). Feiler stille.
  useEffect(() => {
    async function loadEmployees() {
      try {
        const response = await fetch(apiUrl('/api/employees'), {
          headers: await getAuthHeaders(),
        })
        if (response.ok) setEmployees(await response.json())
      } catch {
        // ID-ene vises da som #ID
      }
    }

    void loadEmployees()
  }, [getAuthHeaders])

  return (
    <div className="helpdesk-card">
      <h3>Revisjonslogg</h3>
      <p className="audit-intro">
        Alle endringer på saker, tilgangsbestillinger, ansatte og konfigurasjon i denne
        tenanten, med hvem og når. Loggen kan ikke endres eller slettes.
      </p>

      <div className="ticket-filter">
        <label htmlFor="audit-entity">Vis:</label>
        <select
          id="audit-entity"
          value={entityType}
          onChange={(e) => {
            setLoading(true)
            setError('')
            setEntityType(e.target.value)
          }}
        >
          <option value="">Alt</option>
          {Object.entries(ENTITY_LABELS).map(([value, label]) => (
            <option key={value} value={value}>
              {label}
            </option>
          ))}
        </select>
      </div>

      {error && <p role="alert" style={{ color: '#dc2626' }}>{error}</p>}

      {!loading && events.length === 0 && !error ? (
        <p>Ingen hendelser registrert.</p>
      ) : (
        <div className="audit-table-wrapper">
          <table className="audit-table">
            <thead>
              <tr>
                <th>Tidspunkt</th>
                <th>Hvem</th>
                <th>Hva</th>
                <th>Hendelse</th>
                <th>Endringer</th>
              </tr>
            </thead>
            <tbody>
              {events.map((event) => (
                <tr key={event.id}>
                  <td className="audit-nowrap">{formatDateTime(event.occurredAt)}</td>
                  <td>{event.actorName}</td>
                  <td className="audit-nowrap">
                    {ENTITY_LABELS[event.entityType] ?? event.entityType}
                    {event.entityType !== 'EmployeeSync' && ` #${event.entityId}`}
                  </td>
                  <td>{actionLabel(event.action)}</td>
                  <td>
                    {describeChanges(event, employeeNames).map((line) => (
                      <div key={line}>{line}</div>
                    ))}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {loading && <p>Henter hendelser...</p>}

      {hasMore && !loading && (
        <button
          type="button"
          className="audit-more"
          onClick={() => void loadMore()}
        >
          Vis eldre hendelser
        </button>
      )}
    </div>
  )
}

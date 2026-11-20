import { useEffect, useState } from 'react'
import { apiUrl, useAuthHeaders } from '../apiClient'
import { useCurrentUser } from '../useCurrentUser'
import { useServiceCatalog } from '../serviceCatalog'
import { useList } from './settings/settingsApi'
import { t } from '../i18n'

type AccessRequest = {
  id: number
  employeeName: string
  systemName: string
  reason: string
  managerName: string
  status: string
  createdAt: string
  approvedAt: string | null
  serviceCatalogItemId: number | null
}

type Employee = {
  id: number
  firstName: string
  lastName: string
  status: string
  entraObjectId: string | null
}

// Svar fra godkjenning og nytt forsøk: bestillingen, og om tilgangen ble gitt i Entra
type AccessRequestResult = {
  request: AccessRequest
  grant: { attempted: boolean; granted: boolean; message: string } | null
}

const API_URL = apiUrl('/api/accessrequests')

export default function AccessRequests() {
  const getAuthHeaders = useAuthHeaders()
  const { isHelpdeskAgent, isApprover, objectId } = useCurrentUser()

  // Den ansatte som skal ha tilgang; deg selv som standard
  const employees = useList<Employee>('/api/employees')
  const activeEmployees = employees.items.filter((e) => e.status === 'Aktiv')
  const [employeeId, setEmployeeId] = useState('')
  const selectedEmployeeId =
    employeeId ||
    (activeEmployees.find((e) => objectId && e.entraObjectId === objectId)?.id.toString() ?? '')

  // Systemene kommer fra tjenestekatalogen (type access)
  const systems = useServiceCatalog('type=access')
  const [systemId, setSystemId] = useState<number | null>(null)
  const selectedSystem =
    systems.items.find((s) => s.id === systemId) ?? systems.items[0]

  const [reason, setReason] = useState('')
  const [managerName, setManagerName] = useState('')
  const [requests, setRequests] = useState<AccessRequest[]>([])
  const [loading, setLoading] = useState(true)
  const [saving, setSaving] = useState(false)
  const [updatingId, setUpdatingId] = useState<number | null>(null)
  const [error, setError] = useState('')
  const [message, setMessage] = useState('')

  useEffect(() => {
    let cancelled = false

    async function loadRequests() {
      try {
        const response = await fetch(API_URL, {
          headers: await getAuthHeaders(),
        })
        if (!response.ok) throw new Error()

        const data: AccessRequest[] = await response.json()

        if (!cancelled) setRequests(data)
      } catch {
        if (!cancelled) {
          setError(t('Kunne ikke hente tilgangsbestillinger.'))
        }
      } finally {
        if (!cancelled) setLoading(false)
      }
    }

    void loadRequests()

    return () => {
      cancelled = true
    }
  }, [getAuthHeaders])

  async function createRequest() {
    if (
      saving ||
      !selectedSystem ||
      !selectedEmployeeId ||
      !reason.trim() ||
      !managerName.trim()
    ) return

    setSaving(true)
    setError('')
    setMessage('')

    try {
      const response = await fetch(API_URL, {
        method: 'POST',
        headers: await getAuthHeaders(),
        body: JSON.stringify({
          employeeId: Number(selectedEmployeeId),
          serviceCatalogItemId: selectedSystem.id,
          reason: reason.trim(),
          managerName: managerName.trim(),
        }),
      })

      if (!response.ok) throw new Error()

      const created: AccessRequest = await response.json()

      setRequests((previous) => [created, ...previous])
      setEmployeeId('')
      setReason('')
      setManagerName('')
      setMessage(
        created.status === 'Fullført'
          ? t('Systemet krever ikke godkjenning, og tilgangen er gitt.')
          : t('Tilgangsbestillingen er registrert.')
      )
    } catch {
      setError(t('Kunne ikke opprette tilgangsbestillingen.'))
    } finally {
      setSaving(false)
    }
  }

  // Oppdaterer bestillingen i listen og viser resultatet fra Entra (om noe ble forsøkt)
  function showResult(result: AccessRequestResult, fallback: string) {
    setRequests((previous) =>
      previous.map((request) => (request.id === result.request.id ? result.request : request))
    )

    if (result.grant && !result.grant.granted) setError(result.grant.message)
    else setMessage(result.grant?.message ?? fallback)
  }

  async function updateApproval(
    id: number,
    status: 'Godkjent' | 'Avslått'
  ) {
    if (updatingId !== null) return

    setUpdatingId(id)
    setError('')
    setMessage('')

    try {
      const response = await fetch(`${API_URL}/${id}/approval`, {
        method: 'PATCH',
        headers: await getAuthHeaders(),
        body: JSON.stringify({ status }),
      })

      if (!response.ok) throw new Error()

      showResult(await response.json(), status === 'Godkjent' ? t('Bestillingen er godkjent.') : t('Bestillingen er avslått.'))
    } catch {
      setError(t('Kunne ikke oppdatere godkjenningen.'))
    } finally {
      setUpdatingId(null)
    }
  }

  // Nytt forsøk på å gi tilgangen i Entra
  async function grantRequest(id: number) {
    if (updatingId !== null) return

    setUpdatingId(id)
    setError('')
    setMessage('')

    try {
      const response = await fetch(`${API_URL}/${id}/grant`, {
        method: 'POST',
        headers: await getAuthHeaders(),
      })

      if (!response.ok) throw new Error()

      showResult(await response.json(), '')
    } catch {
      setError(t('Kunne ikke gi tilgangen.'))
    } finally {
      setUpdatingId(null)
    }
  }

  // IT fullfører godkjent tilgangsbestilling (tilgangen er gitt manuelt)
  async function completeRequest(id: number) {
    if (updatingId !== null) return

    setUpdatingId(id)
    setError('')
    setMessage('')

    try {
      const response = await fetch(`${API_URL}/${id}/complete`, {
        method: 'PATCH',
        headers: await getAuthHeaders(),
      })
      if (!response.ok) throw new Error()

      const updated: AccessRequest = await response.json()

      setRequests((previous) =>
        previous.map((request) =>
          request.id === updated.id ? updated : request
        )
      )

      setMessage(t('Tilgangsbestillingen er fullført.'))
    } catch {
      setError(t('Kunne ikke fullføre tilgangsbestillingen.'))
    } finally {
      setUpdatingId(null)
    }
  }

  // Systemer med Entra-gruppe gir tilgangen automatisk når bestillingen godkjennes
  function hasGroup(request: AccessRequest): boolean {
    return systems.items.some(
      (s) => s.id === request.serviceCatalogItemId && Boolean(s.entraGroupId)
    )
  }

  return (
    <section className="helpdesk">
      <div className="helpdesk-card">
        <h3>{t('Bestill tilgang')}</h3>

        <form
          className="helpdesk-form"
          onSubmit={(e) => {
            e.preventDefault()
            void createRequest()
          }}
        >
          <label htmlFor="access-employee">{t('Ansatt')}</label>
          <select
            id="access-employee"
            value={selectedEmployeeId}
            onChange={(e) => setEmployeeId(e.target.value)}
            disabled={employees.loading}
            required
          >
            <option value="">{employees.loading ? t('Henter ansatte...') : t('Velg ansatt')}</option>
            {activeEmployees.map((e) => (
              <option key={e.id} value={e.id}>
                {`${e.firstName} ${e.lastName}`.trim()}
              </option>
            ))}
          </select>

          <label htmlFor="access-system">{t('System')}</label>
          <select
            id="access-system"
            value={selectedSystem?.id ?? ''}
            onChange={(e) => setSystemId(Number(e.target.value))}
            disabled={systems.loading || systems.items.length === 0}
            required
          >
            {systems.loading && <option value="">{t('Henter systemer...')}</option>}
            {!systems.loading && systems.items.length === 0 && (
              <option value="">{t('Ingen systemer i tjenestekatalogen')}</option>
            )}
            {systems.items.map((system) => (
              <option key={system.id} value={system.id}>
                {system.name}
              </option>
            ))}
          </select>

          {selectedSystem?.description && (
            <p className="service-description">{selectedSystem.description}</p>
          )}
          {selectedSystem?.entraGroupId && (
            <p className="service-description">
              {selectedSystem.requiresApproval
                ? t('Tilgangen gis automatisk når bestillingen er godkjent.')
                : t('Krever ikke godkjenning når du bestiller til deg selv: tilgangen gis med en gang.')}
            </p>
          )}
          {systems.error && <p role="alert">{systems.error}</p>}

          <label htmlFor="access-reason">{t('Begrunnelse')}</label>
          <textarea
            id="access-reason"
            value={reason}
            onChange={(e) => setReason(e.target.value)}
            placeholder={t('Hvorfor trengs tilgangen?')}
            rows={4}
            required
          />

          <label htmlFor="access-manager">{t('Leder')}</label>
          <input
            id="access-manager"
            value={managerName}
            onChange={(e) => setManagerName(e.target.value)}
            placeholder={t('Navn på leder som skal godkjenne')}
            required
          />

          <button
            type="submit"
            disabled={
              saving ||
              !selectedSystem ||
              !selectedEmployeeId ||
              !reason.trim() ||
              !managerName.trim()
            }
          >
            {saving ? t('Sender...') : t('Send bestilling')}
          </button>
        </form>
      </div>

      {error && <p role="alert" className="settings-error">{error}</p>}
      {message && <p role="status" className="settings-message">{message}</p>}

      <div className="helpdesk-card">
        <h3>{t('Registrerte tilgangsbestillinger')}</h3>

        {loading ? (
          <p>{t('Henter bestillinger...')}</p>
        ) : requests.length === 0 ? (
          <p>{t('Ingen tilgangsbestillinger registrert.')}</p>
        ) : (
          requests.map((request) => (
            <div className="helpdesk-ticket" key={request.id}>
              <h4>#{request.id} – {request.systemName}</h4>

              <p><strong>{t('Ansatt')}:</strong> {request.employeeName}</p>
              <p><strong>{t('Begrunnelse')}:</strong> {request.reason}</p>
              <p><strong>{t('Leder')}:</strong> {request.managerName}</p>
              <p><strong>{t('Status')}:</strong> {t(request.status)}</p>

              {isApprover && request.status === 'Venter på godkjenning' && (
                <div className="approval-actions">
                  <button
                    type="button"
                    disabled={updatingId !== null}
                    onClick={() => {
                      void updateApproval(request.id, 'Godkjent')
                    }}
                  >
                    {t('Godkjenn')}
                  </button>

                  <button
                    type="button"
                    disabled={updatingId !== null}
                    onClick={() => {
                      void updateApproval(request.id, 'Avslått')
                    }}
                  >
                    {t('Avslå')}
                  </button>
                </div>
              )}

              {isHelpdeskAgent && request.status === 'Godkjent' && (
                <div className="approval-actions">
                  {hasGroup(request) && (
                    <button
                      type="button"
                      disabled={updatingId !== null}
                      onClick={() => {
                        void grantRequest(request.id)
                      }}
                    >
                      {updatingId === request.id ? t('Gir tilgang...') : t('Gi tilgang i Entra')}
                    </button>
                  )}

                  <button
                    type="button"
                    disabled={updatingId !== null}
                    onClick={() => {
                      void completeRequest(request.id)
                    }}
                  >
                    {hasGroup(request) ? t('Fullfør (gitt manuelt)') : t('Fullfør')}
                  </button>
                </div>
              )}
            </div>
          ))
        )}
      </div>
    </section>
  )
}

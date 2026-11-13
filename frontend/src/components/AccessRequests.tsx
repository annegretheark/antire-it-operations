
import { useEffect, useState } from 'react'
import { useMsal } from '@azure/msal-react'
import { loginRequest } from '../msalConfig'

type AccessRequest = {
  id: number
  employeeName: string
  systemName: string
  reason: string
  managerName: string
  status: string
  createdAt: string
  approvedAt: string | null
}

const API_URL = 'http://localhost:5086/api/accessrequests'

export default function AccessRequests() {
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
  const [employeeName, setEmployeeName] = useState('')
  const [systemName, setSystemName] = useState('Microsoft 365')
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
          setError('Kunne ikke hente tilgangsbestillinger.')
        }
      } finally {
        if (!cancelled) setLoading(false)
      }
    }

    void loadRequests()

    return () => {
      cancelled = true
    }
  }, [])

  async function createRequest() {
    if (
      saving ||
      !employeeName.trim() ||
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
          employeeName: employeeName.trim(),
          systemName,
          reason: reason.trim(),
          managerName: managerName.trim(),
        }),
      })

      if (!response.ok) throw new Error()

      const created: AccessRequest = await response.json()

      setRequests((previous) => [created, ...previous])
      setEmployeeName('')
      setReason('')
      setManagerName('')
      setMessage('Tilgangsbestillingen er registrert.')
    } catch {
      setError('Kunne ikke opprette tilgangsbestillingen.')
    } finally {
      setSaving(false)
    }
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

      const updated: AccessRequest = await response.json()

      setRequests((previous) =>
        previous.map((request) =>
          request.id === updated.id ? updated : request
        )
      )

      setMessage(`Bestillingen er ${status.toLowerCase()}.`)
    } catch {
      setError('Kunne ikke oppdatere godkjenningen.')
    } finally {
      setUpdatingId(null)
    }
  }

  // IT fullfører godkjent tilgangsbestilling
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

      setMessage('Tilgangsbestillingen er fullført.')
    } catch {
      setError('Kunne ikke fullføre tilgangsbestillingen.')
    } finally {
      setUpdatingId(null)
    }
  }

  return (
    <section className="helpdesk">
      <div className="helpdesk-card">
        <h3>Bestill tilgang</h3>

        <form
          className="helpdesk-form"
          onSubmit={(e) => {
            e.preventDefault()
            void createRequest()
          }}
        >
          <label htmlFor="access-employee">Ansatt</label>
          <input
            id="access-employee"
            value={employeeName}
            onChange={(e) => setEmployeeName(e.target.value)}
            placeholder="Navn på ansatt"
            required
          />

          <label htmlFor="access-system">System</label>
          <select
            id="access-system"
            value={systemName}
            onChange={(e) => setSystemName(e.target.value)}
          >
            <option>Microsoft 365</option>
            <option>Teams</option>
            <option>SharePoint</option>
            <option>Azure</option>
            <option>Annet</option>
          </select>

          <label htmlFor="access-reason">Begrunnelse</label>
          <textarea
            id="access-reason"
            value={reason}
            onChange={(e) => setReason(e.target.value)}
            placeholder="Hvorfor trengs tilgangen?"
            rows={4}
            required
          />

          <label htmlFor="access-manager">Leder</label>
          <input
            id="access-manager"
            value={managerName}
            onChange={(e) => setManagerName(e.target.value)}
            placeholder="Navn på leder som skal godkjenne"
            required
          />

          <button
            type="submit"
            disabled={
              saving ||
              !employeeName.trim() ||
              !reason.trim() ||
              !managerName.trim()
            }
          >
            {saving ? 'Sender...' : 'Send bestilling'}
          </button>
        </form>
      </div>

      {error && <p role="alert">{error}</p>}
      {message && <p role="status">{message}</p>}

      <div className="helpdesk-card">
        <h3>Registrerte tilgangsbestillinger</h3>

        {loading ? (
          <p>Henter bestillinger...</p>
        ) : requests.length === 0 ? (
          <p>Ingen tilgangsbestillinger registrert.</p>
        ) : (
          requests.map((request) => (
            <div className="helpdesk-ticket" key={request.id}>
              <h4>#{request.id} – {request.systemName}</h4>

              <p><strong>Ansatt:</strong> {request.employeeName}</p>
              <p><strong>Begrunnelse:</strong> {request.reason}</p>
              <p><strong>Leder:</strong> {request.managerName}</p>
              <p><strong>Status:</strong> {request.status}</p>

              {request.status === 'Venter på godkjenning' && (
                <div className="approval-actions">
                  <button
                    type="button"
                    disabled={updatingId !== null}
                    onClick={() => {
                      void updateApproval(request.id, 'Godkjent')
                    }}
                  >
                    Godkjenn
                  </button>

                  <button
                    type="button"
                    disabled={updatingId !== null}
                    onClick={() => {
                      void updateApproval(request.id, 'Avslått')
                    }}
                  >
                    Avslå
                  </button>
                </div>
              )}

              {request.status === 'Godkjent' && (
                <div className="approval-actions">
                  <button
                    type="button"
                    disabled={updatingId !== null}
                    onClick={() => {
                      void completeRequest(request.id)
                    }}
                  >
                    {updatingId === request.id
                      ? 'Fullfører...'
                      : 'Fullfør'}
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


import { useEffect, useState } from 'react'
import Helpdesk from './components/Helpdesk'
import './App.css'
import AccessRequests from './components/AccessRequests'
import './components/Helpdesk.css'
import Employees from './components/Employees'
import {
  useMsal,
  useIsAuthenticated
} from '@azure/msal-react'

import { loginRequest } from './msalConfig'
import { apiUrl, useAuthHeaders } from './apiClient'
import { useCurrentUser } from './useCurrentUser'


type Ticket = {
  id: number
  status: string
  priority: string
}

const API_URL = apiUrl('/api/tickets')

function App() {
    const { instance, accounts } = useMsal()
  const isAuthenticated = useIsAuthenticated()
  const getAuthHeaders = useAuthHeaders()
  const { tenantName } = useCurrentUser()

  const login = async () => {
  try {
    // select_account: velg konto, og dermed tenant (f.eks. test eller prod)
    const result = await instance.loginPopup({
      ...loginRequest,
      prompt: 'select_account',
    })
    instance.setActiveAccount(result.account)
  } catch (error) {
    console.error('Microsoft-innlogging feilet:', error)
  }
}
  

  const logout = async () => {
    try {
await instance.logoutPopup({
  postLogoutRedirectUri: `${window.location.origin}/auth-popup.html`,
  mainWindowRedirectUri: `${window.location.origin}/`,
})
    } catch (error) {
      console.error('Utlogging feilet:', error)
    }
  }
  const [activePage, setActivePage] = useState('Dashboard')
  const [tickets, setTickets] = useState<Ticket[]>([])
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState('')

  const menu = [
    'Dashboard',
    'Helpdesk',
    'Tilgangsbestillinger',
    'Ansatte',
    'IT-utstyr',
    'Onboarding / Offboarding',
    'GDPR',
    'Innstillinger',
  ]

  useEffect(() => {
    if (activePage !== 'Dashboard') return

    let cancelled = false

    async function loadTickets() {
      setLoading(true)
      setError('')

      try {
        const response = await fetch(API_URL, {
          headers: await getAuthHeaders(),
        })
        if (!response.ok) throw new Error()

        const data: Ticket[] = await response.json()

        if (!cancelled) {
          setTickets(data)
        }
      } catch {
        if (!cancelled) {
          setError('Kunne ikke hente saker fra backend.')
        }
      } finally {
        if (!cancelled) {
          setLoading(false)
        }
      }
    }

    void loadTickets()

    return () => {
      cancelled = true
    }
}, [activePage, getAuthHeaders, isAuthenticated])

  const stats = {
    new: tickets.filter((t) => t.status === 'Ny').length,
    inProgress: tickets.filter(
      (t) => t.status === 'Under behandling'
    ).length,
    resolved: tickets.filter(
      (t) => t.status === 'Løst'
    ).length,
    critical: tickets.filter(
      (t) => t.priority === 'Kritisk' && t.status !== 'Løst'
    ).length,
  }

  return (
    <div className="app">
      <aside className="sidebar">
        <h2>Antire IT Operations</h2>

        <nav>
          {menu.map((item) => (
            <button
              key={item}
              className={activePage === item ? 'active' : ''}
              onClick={() => setActivePage(item)}
            >
              {item}
            </button>
          ))}
        </nav>
      </aside>

      <main className="main">
        
<header>
  <h1>{activePage}</h1>

  <div className="auth-controls">
    {/* Tenanten du er logget inn i (navn fra Tenants:Allowed i backend) */}
    <span className="tenant-badge">
      {isAuthenticated && tenantName ? `Tenant: ${tenantName}` : 'Ikke innlogget'}
    </span>

    {isAuthenticated ? (
      <>
        <span>
          {accounts[0]?.name || accounts[0]?.username}
        </span>
        <button type="button" onClick={logout}>
          Logg ut
        </button>
      </>
    ) : (
      <button type="button" onClick={login}>
        Logg inn med Microsoft
      </button>
    )}
  </div>
</header>


        {activePage === 'Dashboard' ? (
          <section className="card">
            <h3>IT Operations – Dashboard</h3>

            {loading ? (
              <p>Henter saker...</p>
            ) : error ? (
              <p role="alert">{error}</p>
            ) : (
              <div className="dashboard-grid">
                <div className="dashboard-stat">
                  <h4>Nye saker</h4>
                  <strong>{stats.new}</strong>
                </div>

                <div className="dashboard-stat">
                  <h4>Under behandling</h4>
                  <strong>{stats.inProgress}</strong>
                </div>

                <div className="dashboard-stat">
                  <h4>Løste saker</h4>
                  <strong>{stats.resolved}</strong>
                </div>

                <div className="dashboard-stat">
                  <h4>Kritiske åpne saker</h4>
                  <strong>{stats.critical}</strong>
                </div>
              </div>
            )}
          </section>
      ) : activePage === 'Helpdesk' ? (
  <Helpdesk />
) : activePage === 'Tilgangsbestillinger' ? (
  <AccessRequests />
) : activePage === 'Ansatte' ? (
  <Employees />
) : (
          <section className="card">
            <h3>{activePage}</h3>
            <p>Denne modulen kommer snart.</p>
          </section>
        )}
      </main>
    </div>
  )
}

export default App

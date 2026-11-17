import { useMemo, useState } from 'react'
import './Helpdesk.css'
import { useCurrentUser } from '../useCurrentUser'
import { formatDateTime } from '../audit'
import { useList, useResource } from './settings/settingsApi'

type M365User = {
  id: string
  displayName: string | null
  userPrincipalName: string | null
  mail: string | null
  accountEnabled: boolean | null
  userType: string | null
  department: string | null
  jobTitle: string | null
  createdAt: string | null
  licenses: string[]
  lastSignInAt: string | null
  mfaRegistered: boolean | null
}

type UserOverview = {
  users: M365User[]
  signInUnavailableReason: string | null
  mfaUnavailableReason: string | null
  licensesUnavailableReason: string | null
  fetchedAt: string
}

type M365Group = {
  id: string
  displayName: string | null
  description: string | null
  mail: string | null
  kind: string
  isDynamic: boolean
}

type GroupMember = {
  id: string
  displayName: string | null
  userPrincipalName: string | null
  type: string
}

const GROUP_KIND_LABELS: Record<string, string> = {
  microsoft365: 'Microsoft 365',
  security: 'Sikkerhet',
  mail_security: 'E-postaktivert sikkerhet',
  distribution: 'Distribusjonsliste',
}

const MEMBER_TYPE_LABELS: Record<string, string> = {
  user: 'Bruker',
  group: 'Gruppe',
  device: 'Enhet',
  other: 'Annet',
}

// Microsoft 365 i innlogget tenant (Helpdesk.Admin): brukere og grupper fra Entra ID
export default function M365() {
  const { isHelpdeskAdmin, tenantName } = useCurrentUser()
  const [tab, setTab] = useState<'users' | 'groups'>('users')

  if (!isHelpdeskAdmin) {
    return (
      <section className="helpdesk">
        <div className="helpdesk-card">
          <h3>Microsoft 365</h3>
          <p>Du trenger rollen Helpdesk.Admin for å se Microsoft 365-oversikten.</p>
        </div>
      </section>
    )
  }

  return (
    <section className="helpdesk">
      <p className="audit-intro">
        Data hentes fra Entra ID i tenanten du er logget inn i{tenantName ? ` (${tenantName})` : ''}.
      </p>

      <div className="settings-tabs" role="tablist" aria-label="Microsoft 365">
        <button type="button" role="tab" aria-selected={tab === 'users'} onClick={() => setTab('users')}>
          Brukere
        </button>
        <button type="button" role="tab" aria-selected={tab === 'groups'} onClick={() => setTab('groups')}>
          Grupper
        </button>
      </div>

      {tab === 'users' ? <Users /> : <Groups />}
    </section>
  )
}

// Ny henting forbi cachen i backend når brukeren ber om det
function useRefresh(path: string) {
  const [count, setCount] = useState(0)
  const url = count > 0 ? `${path}?refresh=true&n=${count}` : path
  return { url, refresh: () => setCount((c) => c + 1) }
}

function Users() {
  const { url, refresh } = useRefresh('/api/m365/users')
  const overview = useResource<UserOverview>(url)
  const [search, setSearch] = useState('')
  const [onlyProblems, setOnlyProblems] = useState(false)

  const users = useMemo(() => {
    const term = search.trim().toLowerCase()

    return (overview.data?.users ?? []).filter((u) => {
      if (onlyProblems && u.accountEnabled !== false && u.mfaRegistered !== false) return false
      if (!term) return true
      return [u.displayName, u.userPrincipalName, u.department, u.jobTitle]
        .some((v) => v?.toLowerCase().includes(term))
    })
  }, [overview.data, search, onlyProblems])

  const data = overview.data
  const notices = [
    data?.signInUnavailableReason && `Siste pålogging: ${data.signInUnavailableReason}`,
    data?.mfaUnavailableReason && `MFA: ${data.mfaUnavailableReason}`,
    data?.licensesUnavailableReason && `Lisensnavn: ${data.licensesUnavailableReason}`,
  ].filter(Boolean) as string[]

  return (
    <div className="helpdesk-card">
      <h3>Brukere</h3>

      <div className="asset-filters">
        <input
          type="search"
          aria-label="Søk"
          placeholder="Søk på navn, e-post, avdeling eller stilling"
          value={search}
          onChange={(e) => setSearch(e.target.value)}
        />
        <label className="settings-check">
          <input type="checkbox" checked={onlyProblems} onChange={(e) => setOnlyProblems(e.target.checked)} />
          Bare deaktiverte eller uten MFA
        </label>
        <button type="button" className="settings-row-action" onClick={refresh} disabled={overview.loading}>
          Oppdater
        </button>
      </div>

      {notices.length > 0 && (
        <div className="m365-notice">
          <strong>Noe data er ikke tilgjengelig:</strong>
          <ul>
            {notices.map((n) => <li key={n}>{n}</li>)}
          </ul>
        </div>
      )}

      {overview.error && <p role="alert" className="settings-error">{overview.error}</p>}

      {overview.loading ? (
        <p>Henter brukere fra Entra ID...</p>
      ) : data && (
        <>
          <p className="settings-muted">
            {users.length} av {data.users.length} brukere · hentet {formatDateTime(data.fetchedAt)}
          </p>
          <div className="audit-table-wrapper">
            <table className="audit-table">
              <thead>
                <tr>
                  <th>Navn</th>
                  <th>Type</th>
                  <th>Konto</th>
                  <th>Lisenser</th>
                  <th>Siste pålogging</th>
                  <th>MFA</th>
                </tr>
              </thead>
              <tbody>
                {users.map((u) => (
                  <tr key={u.id}>
                    <td>
                      <strong>{u.displayName ?? '–'}</strong>
                      <div className="settings-muted">{u.userPrincipalName}</div>
                      {(u.jobTitle || u.department) && (
                        <div className="settings-muted">
                          {[u.jobTitle, u.department].filter(Boolean).join(', ')}
                        </div>
                      )}
                    </td>
                    <td>{u.userType === 'Guest' ? 'Gjest' : 'Medlem'}</td>
                    <td className={u.accountEnabled === false ? 'settings-error' : undefined}>
                      {u.accountEnabled === false ? 'Deaktivert' : 'Aktiv'}
                    </td>
                    <td>{u.licenses.length > 0 ? u.licenses.join(', ') : '–'}</td>
                    <td className="audit-nowrap">
                      {data.signInUnavailableReason
                        ? <span className="settings-muted">Krever lisens</span>
                        : u.lastSignInAt ? formatDateTime(u.lastSignInAt) : 'Aldri'}
                    </td>
                    <td>
                      {data.mfaUnavailableReason
                        ? <span className="settings-muted">Krever lisens</span>
                        : u.mfaRegistered === false
                          ? <span className="settings-error">Ikke registrert</span>
                          : u.mfaRegistered ? 'Registrert' : '–'}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </>
      )}
    </div>
  )
}

function Groups() {
  const { url, refresh } = useRefresh('/api/m365/groups')
  const groups = useList<M365Group>(url)
  const [search, setSearch] = useState('')
  const [openId, setOpenId] = useState<string | null>(null)

  const filtered = groups.items.filter((g) =>
    !search.trim() ||
    [g.displayName, g.description, g.mail].some((v) => v?.toLowerCase().includes(search.trim().toLowerCase()))
  )

  return (
    <div className="helpdesk-card">
      <h3>Grupper</h3>

      <div className="asset-filters">
        <input
          type="search"
          aria-label="Søk"
          placeholder="Søk på navn, beskrivelse eller e-post"
          value={search}
          onChange={(e) => setSearch(e.target.value)}
        />
        <button type="button" className="settings-row-action" onClick={refresh} disabled={groups.loading}>
          Oppdater
        </button>
      </div>

      {groups.error && <p role="alert" className="settings-error">{groups.error}</p>}

      {groups.loading ? (
        <p>Henter grupper fra Entra ID...</p>
      ) : !groups.error && filtered.length === 0 ? (
        <p>Ingen grupper funnet.</p>
      ) : (
        <div className="audit-table-wrapper">
          <table className="audit-table">
            <thead>
              <tr>
                <th>Gruppe</th>
                <th>Type</th>
                <th>Gruppe-ID</th>
              </tr>
            </thead>
            <tbody>
              {filtered.map((g) => (
                <GroupRow
                  key={g.id}
                  group={g}
                  open={openId === g.id}
                  onToggle={() => setOpenId(openId === g.id ? null : g.id)}
                />
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  )
}

function GroupRow({ group, open, onToggle }: { group: M365Group; open: boolean; onToggle: () => void }) {
  return (
    <>
      <tr>
        <td>
          <button type="button" className="asset-link" aria-expanded={open} onClick={onToggle}>
            {open ? '▾' : '▸'} {group.displayName ?? '(uten navn)'}
          </button>
          {group.description && <div className="settings-muted">{group.description}</div>}
        </td>
        <td>
          {GROUP_KIND_LABELS[group.kind] ?? group.kind}
          {group.isDynamic && <div className="settings-muted">Dynamisk medlemskap</div>}
        </td>
        <td><code>{group.id}</code></td>
      </tr>
      {open && (
        <tr>
          <td colSpan={3}>
            <GroupMembers groupId={group.id} />
          </td>
        </tr>
      )}
    </>
  )
}

function GroupMembers({ groupId }: { groupId: string }) {
  const members = useList<GroupMember>(`/api/m365/groups/${groupId}/members`)

  if (members.loading) return <p>Henter medlemmer...</p>
  if (members.error) return <p className="settings-error">{members.error}</p>
  if (members.items.length === 0) return <p className="settings-muted">Ingen medlemmer.</p>

  return (
    <ul className="asset-tickets">
      {members.items.map((m) => (
        <li key={m.id}>
          {m.displayName ?? m.id}
          {m.userPrincipalName && <span className="settings-muted"> · {m.userPrincipalName}</span>}
          {m.type !== 'user' && <span className="settings-muted"> ({MEMBER_TYPE_LABELS[m.type] ?? m.type})</span>}
        </li>
      ))}
    </ul>
  )
}

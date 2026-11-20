import { useMemo, useState } from 'react'
import './Helpdesk.css'
import { useCurrentUser } from '../useCurrentUser'
import { formatDateTime } from '../audit'
import { formatDate } from '../assets'
import { t } from '../i18n'
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
  lastSuccessfulSignInAt: string | null
}

type InactiveUsers = {
  users: M365User[]
  totalOfType: number
  signInUnavailableReason: string | null
  fetchedAt: string
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
  const [tab, setTab] = useState<'users' | 'inactive' | 'groups'>('users')

  if (!isHelpdeskAdmin) {
    return (
      <section className="helpdesk">
        <div className="helpdesk-card">
          <h3>Microsoft 365</h3>
          <p>{t('Du trenger rollen Helpdesk.Admin for å se Microsoft 365-oversikten.')}</p>
        </div>
      </section>
    )
  }

  return (
    <section className="helpdesk">
      <p className="audit-intro">
        {t('Data hentes fra Entra ID i tenanten du er logget inn i')}{tenantName ? ` (${tenantName})` : ''}.
      </p>

      <div className="settings-tabs" role="tablist" aria-label="Microsoft 365">
        <button type="button" role="tab" aria-selected={tab === 'users'} onClick={() => setTab('users')}>
          {t('Brukere')}
        </button>
        <button type="button" role="tab" aria-selected={tab === 'inactive'} onClick={() => setTab('inactive')}>
          {t('Inaktive brukere')}
        </button>
        <button type="button" role="tab" aria-selected={tab === 'groups'} onClick={() => setTab('groups')}>
          {t('Grupper')}
        </button>
      </div>

      {tab === 'users' ? <Users /> : tab === 'inactive' ? <Inactive /> : <Groups />}
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
    data?.signInUnavailableReason && t('Siste pålogging: {reason}', { reason: data.signInUnavailableReason }),
    data?.mfaUnavailableReason && t('MFA: {reason}', { reason: data.mfaUnavailableReason }),
    data?.licensesUnavailableReason && t('Lisensnavn: {reason}', { reason: data.licensesUnavailableReason }),
  ].filter(Boolean) as string[]

  return (
    <div className="helpdesk-card">
      <h3>{t('Brukere')}</h3>

      <div className="asset-filters">
        <input
          type="search"
          aria-label={t('Søk')}
          placeholder={t('Søk på navn, e-post, avdeling eller stilling')}
          value={search}
          onChange={(e) => setSearch(e.target.value)}
        />
        <label className="settings-check">
          <input type="checkbox" checked={onlyProblems} onChange={(e) => setOnlyProblems(e.target.checked)} />
          {t('Bare deaktiverte eller uten MFA')}
        </label>
        <button type="button" className="settings-row-action" onClick={refresh} disabled={overview.loading}>
          {t('Oppdater')}
        </button>
      </div>

      {notices.length > 0 && (
        <div className="m365-notice">
          <strong>{t('Noe data er ikke tilgjengelig:')}</strong>
          <ul>
            {notices.map((n) => <li key={n}>{n}</li>)}
          </ul>
        </div>
      )}

      {overview.error && <p role="alert" className="settings-error">{overview.error}</p>}

      {overview.loading ? (
        <p>{t('Henter brukere fra Entra ID...')}</p>
      ) : data && (
        <>
          <p className="settings-muted">
            {t('{shown} av {total} brukere · hentet {time}', {
              shown: users.length,
              total: data.users.length,
              time: formatDateTime(data.fetchedAt),
            })}
          </p>
          <div className="audit-table-wrapper">
            <table className="audit-table">
              <thead>
                <tr>
                  <th>{t('Navn')}</th>
                  <th>{t('Type')}</th>
                  <th>{t('Konto')}</th>
                  <th>{t('Lisenser')}</th>
                  <th>{t('Siste pålogging')}</th>
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
                    <td>{u.userType === 'Guest' ? t('Gjest') : t('Medlem')}</td>
                    <td className={u.accountEnabled === false ? 'settings-error' : undefined}>
                      {u.accountEnabled === false ? t('Deaktivert') : t('Aktiv')}
                    </td>
                    <td>{u.licenses.length > 0 ? u.licenses.join(', ') : '–'}</td>
                    <td className="audit-nowrap">
                      {data.signInUnavailableReason
                        ? <span className="settings-muted">{t('Krever lisens')}</span>
                        : u.lastSignInAt ? formatDateTime(u.lastSignInAt) : t('Aldri')}
                    </td>
                    <td>
                      {data.mfaUnavailableReason
                        ? <span className="settings-muted">{t('Krever lisens')}</span>
                        : u.mfaRegistered === false
                          ? <span className="settings-error">{t('Ikke registrert')}</span>
                          : u.mfaRegistered ? t('Registrert') : '–'}
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

const INACTIVE_PRESETS = [
  { label: '90 dager', days: 90 },
  { label: '180 dager', days: 180 },
  { label: '1 år', days: 365 },
  { label: '2 år', days: 730 },
]

// ÅÅÅÅ-MM-DD for <input type="date">, i lokal tid
function isoDate(date: Date): string {
  const pad = (n: number) => String(n).padStart(2, '0')
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`
}

function daysAgo(days: number): string {
  const date = new Date()
  date.setDate(date.getDate() - days)
  return isoDate(date)
}

function daysSince(value: string | null): number | null {
  return value ? Math.floor((Date.now() - new Date(value).getTime()) / 86_400_000) : null
}

type InactiveQuery = { userType: 'Guest' | 'Member'; before: string; includeNever: boolean }

// Gjester eller medlemmer uten vellykket pålogging siden en dato. Kun lesing og CSV.
function Inactive() {
  const [today] = useState(() => isoDate(new Date()))
  const [userType, setUserType] = useState<'Guest' | 'Member'>('Guest')
  const [before, setBefore] = useState(daysAgo(180))
  const [includeNever, setIncludeNever] = useState(true)
  const [query, setQuery] = useState<InactiveQuery | null>(null)
  const [count, setCount] = useState(0)

  const url = query
    ? `/api/m365/inactive-users?userType=${query.userType}&before=${query.before}` +
      `&includeNeverSignedIn=${query.includeNever}${count > 0 ? `&refresh=true&n=${count}` : ''}`
    : null
  const result = useResource<InactiveUsers>(url)
  const data = result.data
  const typeText = (type: string) => (type === 'Guest' ? t('gjester') : t('medlemmer'))

  function fetchList() {
    setQuery({ userType, before, includeNever })
  }

  function downloadCsv() {
    if (!data || !query) return
    const columns: [string, (u: M365User) => string][] = [
      [t('Navn'), (u) => u.displayName ?? ''],
      [t('Brukernavn'), (u) => u.userPrincipalName ?? ''],
      [t('E-post'), (u) => u.mail ?? ''],
      [t('Type'), (u) => (u.userType === 'Guest' ? t('Gjest') : t('Medlem'))],
      [t('Konto'), (u) => (u.accountEnabled === false ? t('Deaktivert') : t('Aktiv'))],
      [t('Opprettet'), (u) => u.createdAt?.slice(0, 10) ?? ''],
      [t('Siste vellykkede pålogging'), (u) => u.lastSuccessfulSignInAt?.slice(0, 10) ?? ''],
      [t('Siste interaktive pålogging'), (u) => u.lastSignInAt?.slice(0, 10) ?? ''],
      [t('Dager siden'), (u) => String(daysSince(u.lastSuccessfulSignInAt) ?? '')],
      [t('Lisenser'), (u) => u.licenses.join(', ')],
      [t('Objekt-ID'), (u) => u.id],
    ]
    const quote = (v: string) => `"${v.replace(/"/g, '""')}"`
    const csv = [
      columns.map(([h]) => quote(h)).join(';'),
      ...data.users.map((u) => columns.map(([, get]) => quote(get(u))).join(';')),
    ].join('\r\n')

    const link = document.createElement('a')
    link.href = `data:text/csv;charset=utf-8,${encodeURIComponent('﻿' + csv)}`
    link.download = t('inaktive-{type}-{date}.csv', { type: typeText(query.userType), date: query.before })
    link.click()
  }

  return (
    <div className="helpdesk-card">
      <h3>{t('Inaktive brukere')}</h3>
      <p className="audit-intro">
        {t('Finn gjester eller medlemmer som ikke har hatt en vellykket pålogging siden en dato (interaktiv eller ikke-interaktiv). Microsoft registrerer denne datoen fra desember 2023, så «Ingen registrert» betyr ingen vellykket pålogging siden da. Listen er kun til lesing.')}
      </p>

      <div className="asset-filters">
        <label className="settings-check">
          <input type="radio" name="inactive-type" checked={userType === 'Guest'} onChange={() => setUserType('Guest')} />
          {t('Gjester')}
        </label>
        <label className="settings-check">
          <input type="radio" name="inactive-type" checked={userType === 'Member'} onChange={() => setUserType('Member')} />
          {t('Medlemmer')}
        </label>
        <label>
          {t('Ingen vellykket pålogging siden')}{' '}
          <input
            type="date"
            value={before}
            max={today}
            onChange={(e) => setBefore(e.target.value)}
          />
        </label>
        {INACTIVE_PRESETS.map((p) => (
          <button key={p.days} type="button" className="settings-row-action" onClick={() => setBefore(daysAgo(p.days))}>
            {t(p.label)}
          </button>
        ))}
      </div>

      <div className="asset-filters">
        <label className="settings-check">
          <input type="checkbox" checked={includeNever} onChange={(e) => setIncludeNever(e.target.checked)} />
          {t('Ta med brukere uten registrert pålogging (opprettet før datoen)')}
        </label>
        <button type="button" className="asset-primary" onClick={fetchList} disabled={!before || result.loading}>
          {t('Hent liste')}
        </button>
        {query && (
          <button type="button" className="settings-row-action" onClick={() => setCount((c) => c + 1)} disabled={result.loading}>
            {t('Oppdater fra Entra ID')}
          </button>
        )}
        <button
          type="button"
          className="settings-row-action"
          onClick={downloadCsv}
          disabled={!data || data.users.length === 0}
        >
          {t('Last ned som CSV')}
        </button>
      </div>

      {result.error && <p role="alert" className="settings-error">{result.error}</p>}

      {data?.signInUnavailableReason && (
        <div className="m365-notice">
          <strong>{t('Påloggingsdatoene er ikke tilgjengelige:')}</strong> {data.signInUnavailableReason}
        </div>
      )}

      {!query ? (
        <p className="settings-muted">{t('Velg brukertype og dato, og trykk «Hent liste».')}</p>
      ) : result.loading ? (
        <p>{t('Henter brukere fra Entra ID...')}</p>
      ) : data && !data.signInUnavailableReason && (
        <>
          <p className="settings-muted">
            {t('{shown} av {total} {type} uten vellykket pålogging siden {date} · hentet {time}', {
              shown: data.users.length,
              total: data.totalOfType,
              type: typeText(query.userType),
              date: formatDate(query.before),
              time: formatDateTime(data.fetchedAt),
            })}
          </p>
          {data.users.length > 0 && (
            <div className="audit-table-wrapper">
              <table className="audit-table">
                <thead>
                  <tr>
                    <th>{t('Navn')}</th>
                    <th>{t('Konto')}</th>
                    <th>{t('Opprettet')}</th>
                    <th>{t('Siste vellykkede pålogging')}</th>
                    <th>{t('Dager siden')}</th>
                    <th>{t('Lisenser')}</th>
                  </tr>
                </thead>
                <tbody>
                  {data.users.map((u) => (
                    <tr key={u.id}>
                      <td>
                        <strong>{u.displayName ?? '–'}</strong>
                        <div className="settings-muted">{u.userPrincipalName}</div>
                        {u.mail && u.mail !== u.userPrincipalName && (
                          <div className="settings-muted">{u.mail}</div>
                        )}
                      </td>
                      <td className={u.accountEnabled === false ? 'settings-error' : undefined}>
                        {u.accountEnabled === false ? t('Deaktivert') : t('Aktiv')}
                      </td>
                      <td className="audit-nowrap">{formatDate(u.createdAt)}</td>
                      <td className="audit-nowrap">
                        {u.lastSuccessfulSignInAt ? formatDateTime(u.lastSuccessfulSignInAt) : t('Ingen registrert')}
                      </td>
                      <td>{daysSince(u.lastSuccessfulSignInAt) ?? '–'}</td>
                      <td>{u.licenses.length > 0 ? u.licenses.join(', ') : '–'}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
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
      <h3>{t('Grupper')}</h3>

      <div className="asset-filters">
        <input
          type="search"
          aria-label={t('Søk')}
          placeholder={t('Søk på navn, beskrivelse eller e-post')}
          value={search}
          onChange={(e) => setSearch(e.target.value)}
        />
        <button type="button" className="settings-row-action" onClick={refresh} disabled={groups.loading}>
          {t('Oppdater')}
        </button>
      </div>

      {groups.error && <p role="alert" className="settings-error">{groups.error}</p>}

      {groups.loading ? (
        <p>{t('Henter grupper fra Entra ID...')}</p>
      ) : !groups.error && filtered.length === 0 ? (
        <p>{t('Ingen grupper funnet.')}</p>
      ) : (
        <div className="audit-table-wrapper">
          <table className="audit-table">
            <thead>
              <tr>
                <th>{t('Gruppe')}</th>
                <th>{t('Type')}</th>
                <th>{t('Gruppe-ID')}</th>
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
            {open ? '▾' : '▸'} {group.displayName ?? t('(uten navn)')}
          </button>
          {group.description && <div className="settings-muted">{group.description}</div>}
        </td>
        <td>
          {GROUP_KIND_LABELS[group.kind] ? t(GROUP_KIND_LABELS[group.kind]) : group.kind}
          {group.isDynamic && <div className="settings-muted">{t('Dynamisk medlemskap')}</div>}
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

  if (members.loading) return <p>{t('Henter medlemmer...')}</p>
  if (members.error) return <p className="settings-error">{members.error}</p>
  if (members.items.length === 0) return <p className="settings-muted">{t('Ingen medlemmer.')}</p>

  return (
    <ul className="asset-tickets">
      {members.items.map((m) => (
        <li key={m.id}>
          {m.displayName ?? m.id}
          {m.userPrincipalName && <span className="settings-muted"> · {m.userPrincipalName}</span>}
          {m.type !== 'user' && <span className="settings-muted"> ({MEMBER_TYPE_LABELS[m.type] ? t(MEMBER_TYPE_LABELS[m.type]) : m.type})</span>}
        </li>
      ))}
    </ul>
  )
}

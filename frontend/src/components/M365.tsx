import { useMemo, useState } from 'react'
import './Helpdesk.css'
import { useCurrentUser } from '../useCurrentUser'
import { formatDateTime } from '../audit'
import { formatDate } from '../assets'
import { t } from '../i18n'
import { errorText, useList, useResource, useSend } from './settings/settingsApi'

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

type UserChangeResult = { userId: string; displayName: string | null; ok: boolean; error: string | null }

type UserAction = 'disable' | 'enable' | 'delete'

// Lisenser kort: første lisens og antallet resten («E3 +2»), hele listen i title
function shortLicenses(licenses: string[]): string {
  if (licenses.length === 0) return '–'
  return licenses.length === 1 ? licenses[0] : `${licenses[0]} +${licenses.length - 1}`
}

// Brukere i kompakte rader. Valgte brukere kan deaktiveres, aktiveres eller slettes
// med den innloggede personens egne rettigheter i Entra (backend: on-behalf-of).
function Users() {
  const { objectId } = useCurrentUser()
  const { url, refresh } = useRefresh('/api/m365/users')
  const overview = useResource<UserOverview>(url)
  const send = useSend()
  const [search, setSearch] = useState('')
  const [onlyProblems, setOnlyProblems] = useState(false)
  const [selected, setSelected] = useState<Set<string>>(new Set())
  const [busy, setBusy] = useState(false)
  const [results, setResults] = useState<UserChangeResult[]>([])
  const [error, setError] = useState('')

  const users = useMemo(() => {
    const term = search.trim().toLowerCase()

    return (overview.data?.users ?? []).filter((u) => {
      if (onlyProblems && u.accountEnabled !== false && u.mfaRegistered !== false) return false
      if (!term) return true
      return [u.displayName, u.userPrincipalName, u.department, u.jobTitle]
        .some((v) => v?.toLowerCase().includes(term))
    })
  }, [overview.data, search, onlyProblems])

  const self = objectId?.toLowerCase()
  const isSelf = (u: M365User) => u.id.toLowerCase() === self
  const selectable = users.filter((u) => !isSelf(u))
  // Bare valgte som fortsatt vises (søk og filter kan skjule noen)
  const chosen = selectable.filter((u) => selected.has(u.id))
  const allChosen = selectable.length > 0 && chosen.length === selectable.length

  function toggle(id: string) {
    setSelected((current) => {
      const next = new Set(current)
      if (next.has(id)) next.delete(id)
      else next.add(id)
      return next
    })
  }

  function toggleAll() {
    setSelected(allChosen ? new Set() : new Set(selectable.map((u) => u.id)))
  }

  async function run(action: UserAction) {
    if (chosen.length === 0) return

    const names = chosen.map((u) => u.displayName ?? u.userPrincipalName ?? u.id)
    const list = names.length > 10 ? `${names.slice(0, 10).join('\n')}\n${t('… og {n} til', { n: names.length - 10 })}` : names.join('\n')

    if (action === 'delete') {
      const answer = window.prompt(
        t('Slette {n} brukere i Entra ID? De kan gjenopprettes i Entra i 30 dager.\n\n{list}\n\nSkriv SLETT for å bekrefte.', {
          n: chosen.length,
          list,
        })
      )
      if (answer?.trim().toUpperCase() !== 'SLETT') return
    } else if (
      !window.confirm(
        action === 'disable'
          ? t('Deaktivere {n} brukere? De kan ikke logge på før kontoen aktiveres igjen.\n\n{list}', { n: chosen.length, list })
          : t('Aktivere {n} brukere?\n\n{list}', { n: chosen.length, list })
      )
    ) {
      return
    }

    setBusy(true)
    setError('')
    setResults([])

    try {
      const userIds = chosen.map((u) => u.id)
      const response =
        action === 'delete'
          ? await send('POST', '/api/m365/users/delete', { userIds })
          : await send('POST', '/api/m365/users/account-enabled', { userIds, enabled: action === 'enable' })
      const outcome: UserChangeResult[] = await response.json()

      setResults(outcome)
      setSelected(new Set(outcome.filter((r) => !r.ok).map((r) => r.userId)))
      refresh()
    } catch (e) {
      setError(errorText(e))
    } finally {
      setBusy(false)
    }
  }

  const data = overview.data
  const notices = [
    data?.signInUnavailableReason && t('Siste pålogging: {reason}', { reason: data.signInUnavailableReason }),
    data?.mfaUnavailableReason && t('MFA: {reason}', { reason: data.mfaUnavailableReason }),
    data?.licensesUnavailableReason && t('Lisensnavn: {reason}', { reason: data.licensesUnavailableReason }),
  ].filter(Boolean) as string[]

  const succeeded = results.filter((r) => r.ok)
  const failed = results.filter((r) => !r.ok)

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

      <div className="m365-bulk-actions" aria-live="polite">
        <span>{chosen.length > 0 ? t('{n} valgt', { n: chosen.length }) : t('Velg brukere for å endre dem')}</span>
        <button type="button" className="settings-row-action" disabled={busy || chosen.length === 0} onClick={() => void run('disable')}>
          {t('Deaktiver valgte')}
        </button>
        <button type="button" className="settings-row-action" disabled={busy || chosen.length === 0} onClick={() => void run('enable')}>
          {t('Aktiver valgte')}
        </button>
        <button type="button" className="settings-row-action m365-danger" disabled={busy || chosen.length === 0} onClick={() => void run('delete')}>
          {t('Slett valgte')}
        </button>
        {busy && <span className="settings-muted">{t('Endrer...')}</span>}
      </div>
      <p className="settings-muted m365-bulk-hint">
        {t('Endringene gjøres med dine egne rettigheter i Entra ID (f.eks. User Administrator). Du kan ikke endre deg selv.')}
      </p>

      {error && <p role="alert" className="settings-error">{error}</p>}
      {succeeded.length > 0 && (
        <p className="settings-message">
          {t('Endret: {names}', { names: succeeded.map((r) => r.displayName ?? r.userId).join(', ') })}
        </p>
      )}
      {failed.length > 0 && (
        <div role="alert" className="settings-error">
          {t('Ikke endret:')}
          <ul>
            {failed.map((r) => <li key={r.userId}>{r.displayName ?? r.userId}: {r.error}</li>)}
          </ul>
        </div>
      )}

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
            <table className="audit-table m365-compact">
              <thead>
                <tr>
                  <th className="m365-check">
                    <input
                      type="checkbox"
                      aria-label={t('Velg alle som vises')}
                      checked={allChosen}
                      disabled={selectable.length === 0}
                      onChange={toggleAll}
                    />
                  </th>
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
                  <tr key={u.id} className={selected.has(u.id) ? 'm365-selected' : undefined}>
                    <td className="m365-check">
                      <input
                        type="checkbox"
                        aria-label={t('Velg {name}', { name: u.displayName ?? u.userPrincipalName ?? u.id })}
                        checked={selected.has(u.id)}
                        disabled={isSelf(u)}
                        title={isSelf(u) ? t('Du kan ikke endre deg selv') : undefined}
                        onChange={() => toggle(u.id)}
                      />
                    </td>
                    <td>
                      {/* Gjester vises med e-post i stedet for …#EXT#@… */}
                      <div
                        className="m365-name"
                        title={[
                          u.displayName,
                          u.userPrincipalName,
                          [u.jobTitle, u.department].filter(Boolean).join(', '),
                        ].filter(Boolean).join('\n')}
                      >
                        <strong>{u.displayName ?? '–'}</strong>{' '}
                        <span className="settings-muted">
                          {u.userType === 'Guest' ? (u.mail ?? u.userPrincipalName) : u.userPrincipalName}
                        </span>
                      </div>
                    </td>
                    <td>{u.userType === 'Guest' ? t('Gjest') : t('Medlem')}</td>
                    <td className={u.accountEnabled === false ? 'settings-error' : undefined}>
                      {u.accountEnabled === false ? t('Deaktivert') : t('Aktiv')}
                    </td>
                    <td className="audit-nowrap" title={u.licenses.join(', ') || undefined}>
                      {shortLicenses(u.licenses)}
                    </td>
                    <td className="audit-nowrap">
                      {data.signInUnavailableReason
                        ? <span className="settings-muted">{t('Krever lisens')}</span>
                        : u.lastSignInAt ? formatDate(u.lastSignInAt) : t('Aldri')}
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

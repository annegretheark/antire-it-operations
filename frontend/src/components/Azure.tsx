import { useMemo, useState } from 'react'
import './Helpdesk.css'
import { useCurrentUser } from '../useCurrentUser'
import { formatDateTime } from '../audit'
import { errorText, useList, useResource, useSend } from './settings/settingsApi'
import { t } from '../i18n'

type Subscription = {
  id: number
  subscriptionId: string
  displayName: string
  state: string
  isVisible: boolean
  firstSeenAt: string
  lastSeenAt: string
}

type Resource = {
  id: string
  name: string
  type: string
  location: string
  resourceGroup: string
  subscriptionId: string
  tags: Record<string, string>
}

type ResourceList = {
  resources: Resource[]
  total: number
  types: string[]
  locations: string[]
  resourceGroups: string[]
  fetchedAt: string
  message: string | null
}

type SyncResult = { added: number; updated: number; hidden: number; total: number }

const STATE_LABELS: Record<string, string> = {
  Enabled: 'Aktiv',
  Warned: 'Advarsel',
  PastDue: 'Forfalt',
  Disabled: 'Deaktivert',
  Deleted: 'Slettet',
}

// «microsoft.compute/virtualmachines» → «virtualmachines (compute)»
function shortType(type: string): string {
  const [provider, ...rest] = type.split('/')
  const name = rest.join('/') || type
  return provider ? `${name} (${provider.replace(/^microsoft\./i, '')})` : name
}

// Azure i innlogget tenant (Helpdesk.Admin): abonnementer og ressurser
export default function Azure() {
  const { isHelpdeskAdmin } = useCurrentUser()
  const [tab, setTab] = useState<'subscriptions' | 'resources'>('subscriptions')

  if (!isHelpdeskAdmin) {
    return (
      <section className="helpdesk">
        <div className="helpdesk-card">
          <h3>Azure</h3>
          <p>{t('Du trenger rollen Helpdesk.Admin for å se Azure-oversikten.')}</p>
        </div>
      </section>
    )
  }

  return (
    <section className="helpdesk">
      <div className="settings-tabs" role="tablist" aria-label="Azure">
        <button type="button" role="tab" aria-selected={tab === 'subscriptions'} onClick={() => setTab('subscriptions')}>
          {t('Abonnementer')}
        </button>
        <button type="button" role="tab" aria-selected={tab === 'resources'} onClick={() => setTab('resources')}>
          {t('Ressurser')}
        </button>
      </div>

      {tab === 'subscriptions' ? <Subscriptions /> : <Resources />}
    </section>
  )
}

function Subscriptions() {
  const send = useSend()
  const [reloadKey, setReloadKey] = useState(0)
  const subscriptions = useList<Subscription>('/api/azure/subscriptions', reloadKey)
  const [syncing, setSyncing] = useState(false)
  const [error, setError] = useState('')
  const [message, setMessage] = useState('')

  async function sync() {
    setSyncing(true)
    setError('')
    setMessage('')

    try {
      const response = await send('POST', '/api/azure/subscriptions/sync')
      const result: SyncResult = await response.json()
      setMessage(
        t('Synk ferdig: {total} abonnementer synlige ({added} nye, {hidden} ikke lenger synlige).', {
          total: result.total,
          added: result.added,
          hidden: result.hidden,
        })
      )
      setReloadKey((k) => k + 1)
    } catch (e) {
      setError(errorText(e))
    } finally {
      setSyncing(false)
    }
  }

  return (
    <div className="helpdesk-card">
      <h3>{t('Abonnementer')}</h3>
      <p className="audit-intro">
        {t('Appen ser bare abonnementer der den har rollen Reader. Rollen gis med skriptet')}{' '}
        <code>scripts/Grant-AzureReader.ps1</code>{' '}
        {t('(krever Owner eller User Access Administrator på abonnementet). Trykk deretter Synk.')}
      </p>

      <div className="settings-form-actions">
        <button type="button" className="asset-primary" onClick={() => void sync()} disabled={syncing}>
          {syncing ? t('Synker...') : t('Synk fra Azure')}
        </button>
      </div>

      {error && <p role="alert" className="settings-error">{error}</p>}
      {message && <p className="settings-message">{message}</p>}
      {subscriptions.error && <p role="alert" className="settings-error">{subscriptions.error}</p>}

      {subscriptions.loading ? (
        <p>{t('Henter abonnementer...')}</p>
      ) : subscriptions.items.length === 0 ? (
        <p>{t('Ingen abonnementer er synket ennå.')}</p>
      ) : (
        <div className="audit-table-wrapper">
          <table className="audit-table">
            <thead>
              <tr>
                <th>{t('Navn')}</th>
                <th>{t('Abonnements-ID')}</th>
                <th>{t('Tilstand')}</th>
                <th>{t('Sist sett')}</th>
              </tr>
            </thead>
            <tbody>
              {subscriptions.items.map((s) => (
                <tr key={s.id} className={s.isVisible ? undefined : 'settings-muted'}>
                  <td>
                    <strong>{s.displayName}</strong>
                    {!s.isVisible && <div>{t('Ikke synlig for appen lenger')}</div>}
                  </td>
                  <td><code>{s.subscriptionId}</code></td>
                  <td>{STATE_LABELS[s.state] ? t(STATE_LABELS[s.state]) : s.state}</td>
                  <td className="audit-nowrap">{formatDateTime(s.lastSeenAt)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  )
}

function Resources() {
  const subscriptions = useList<Subscription>('/api/azure/subscriptions')
  const [subscriptionId, setSubscriptionId] = useState('')
  const [type, setType] = useState('')
  const [location, setLocation] = useState('')
  const [resourceGroup, setResourceGroup] = useState('')
  const [search, setSearch] = useState('')
  const [refreshCount, setRefreshCount] = useState(0)

  const url = useMemo(() => {
    const params = new URLSearchParams()
    if (subscriptionId) params.set('subscriptionId', subscriptionId)
    if (type) params.set('type', type)
    if (location) params.set('location', location)
    if (resourceGroup) params.set('resourceGroup', resourceGroup)
    if (search.trim()) params.set('search', search.trim())
    if (refreshCount > 0) {
      params.set('refresh', 'true')
      params.set('n', String(refreshCount))
    }
    return `/api/azure/resources${params.size ? `?${params}` : ''}`
  }, [subscriptionId, type, location, resourceGroup, search, refreshCount])

  const result = useResource<ResourceList>(url)
  const data = result.data
  const subscriptionNames = new Map(subscriptions.items.map((s) => [s.subscriptionId, s.displayName]))

  return (
    <div className="helpdesk-card">
      <h3>{t('Ressurser')}</h3>

      <div className="asset-filters">
        <input
          type="search"
          aria-label={t('Søk')}
          placeholder={t('Søk på navn, ressursgruppe eller tagg')}
          value={search}
          onChange={(e) => setSearch(e.target.value)}
        />
        <select aria-label={t('Abonnement')} value={subscriptionId} onChange={(e) => setSubscriptionId(e.target.value)}>
          <option value="">{t('Alle abonnementer')}</option>
          {subscriptions.items.filter((s) => s.isVisible).map((s) => (
            <option key={s.subscriptionId} value={s.subscriptionId}>{s.displayName}</option>
          ))}
        </select>
        <select aria-label={t('Type')} value={type} onChange={(e) => setType(e.target.value)}>
          <option value="">{t('Alle typer')}</option>
          {data?.types.map((typeName) => <option key={typeName} value={typeName}>{shortType(typeName)}</option>)}
        </select>
        <select aria-label={t('Region')} value={location} onChange={(e) => setLocation(e.target.value)}>
          <option value="">{t('Alle regioner')}</option>
          {data?.locations.map((l) => <option key={l} value={l}>{l}</option>)}
        </select>
        <select aria-label={t('Ressursgruppe')} value={resourceGroup} onChange={(e) => setResourceGroup(e.target.value)}>
          <option value="">{t('Alle ressursgrupper')}</option>
          {data?.resourceGroups.map((g) => <option key={g} value={g}>{g}</option>)}
        </select>
        <button
          type="button"
          className="settings-row-action"
          onClick={() => setRefreshCount((c) => c + 1)}
          disabled={result.loading}
        >
          {t('Oppdater')}
        </button>
      </div>

      {result.error && <p role="alert" className="settings-error">{result.error}</p>}
      {data?.message && <div className="m365-notice">{data.message}</div>}

      {result.loading ? (
        <p>{t('Henter ressurser fra Azure...')}</p>
      ) : data && data.total > 0 && (
        <>
          <p className="settings-muted">
            {t('{shown} av {total} ressurser · hentet {time}', {
              shown: data.resources.length,
              total: data.total,
              time: formatDateTime(data.fetchedAt),
            })}
          </p>
          <div className="audit-table-wrapper">
            <table className="audit-table">
              <thead>
                <tr>
                  <th>{t('Navn')}</th>
                  <th>{t('Type')}</th>
                  <th>{t('Ressursgruppe')}</th>
                  <th>{t('Region')}</th>
                  <th>{t('Abonnement')}</th>
                  <th>{t('Tagger')}</th>
                </tr>
              </thead>
              <tbody>
                {data.resources.map((r) => (
                  <tr key={r.id}>
                    <td><strong>{r.name}</strong></td>
                    <td>{shortType(r.type)}</td>
                    <td>{r.resourceGroup}</td>
                    <td>{r.location}</td>
                    <td>{subscriptionNames.get(r.subscriptionId) ?? r.subscriptionId}</td>
                    <td>
                      {Object.entries(r.tags).map(([k, v]) => (
                        <span key={k} className="azure-tag">{k}: {v}</span>
                      ))}
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

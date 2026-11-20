import { useState } from 'react'
import { formatDateTime } from '../../audit'
import { useList } from './settingsApi'
import { t } from '../../i18n'

type AppSignIn = {
  entraObjectId: string
  name: string | null
  userPrincipalName: string | null
  firstSeenAt: string
  lastSeenAt: string
  visits: number
}

// Hvem som har brukt appen i denne tenanten, sist sett først
export default function AppSignInsSettings() {
  const [reloadKey, setReloadKey] = useState(0)
  const signIns = useList<AppSignIn>('/api/appsignins', reloadKey)
  const [search, setSearch] = useState('')

  const term = search.trim().toLowerCase()
  const filtered = signIns.items.filter((s) =>
    !term || [s.name, s.userPrincipalName].some((v) => v?.toLowerCase().includes(term))
  )

  return (
    <div className="helpdesk-card">
      <h3>{t('Pålogginger')}</h3>
      <p className="audit-intro">
        {t('Hvem som har brukt appen i denne tenanten, og når. Oppdateres når brukeren åpner appen (høyst hvert 5. minutt). Et nytt besøk regnes når det har gått mer enn 30 minutter siden sist. Personer som blir stoppet før de kommer inn, for eksempel fordi de logger inn fra en annen tenant, vises ikke her; se påloggingsloggen i Entra ID for dem.')}
      </p>

      <div className="asset-filters">
        <input
          type="search"
          aria-label={t('Søk')}
          placeholder={t('Søk på navn eller brukernavn')}
          value={search}
          onChange={(e) => setSearch(e.target.value)}
        />
        <button
          type="button"
          className="settings-row-action"
          onClick={() => setReloadKey((k) => k + 1)}
          disabled={signIns.loading}
        >
          {t('Oppdater')}
        </button>
      </div>

      {signIns.error && <p role="alert" className="settings-error">{signIns.error}</p>}

      {signIns.loading ? (
        <p>{t('Henter pålogginger...')}</p>
      ) : !signIns.error && filtered.length === 0 ? (
        <p>{t('Ingen pålogginger registrert.')}</p>
      ) : (
        <>
          <p className="settings-muted">{t('{n} brukere', { n: filtered.length })}</p>
          <div className="audit-table-wrapper">
            <table className="audit-table">
              <thead>
                <tr>
                  <th>{t('Bruker')}</th>
                  <th>{t('Sist sett')}</th>
                  <th>{t('Første gang')}</th>
                  <th>{t('Besøk')}</th>
                </tr>
              </thead>
              <tbody>
                {filtered.map((s) => (
                  <tr key={s.entraObjectId}>
                    <td>
                      <strong>{s.name ?? '–'}</strong>
                      {s.userPrincipalName && <div className="settings-muted">{s.userPrincipalName}</div>}
                    </td>
                    <td className="audit-nowrap">{formatDateTime(s.lastSeenAt)}</td>
                    <td className="audit-nowrap">{formatDateTime(s.firstSeenAt)}</td>
                    <td>{s.visits}</td>
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

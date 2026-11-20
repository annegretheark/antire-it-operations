import { useState } from 'react'
import { ASSET_STATUS_LABELS, assetName, type Asset } from '../assets'
import { errorText, useList, useSend } from './settings/settingsApi'
import { t } from '../i18n'

type Props = {
  employeeId: number
  // Satt når den ansatte slutter: utstyret vises da som sjekkliste for innlevering
  endDate: string | null
}

// Utstyr tildelt en ansatt (Helpdesk.Agent og Helpdesk.Admin)
export default function EmployeeAssets({ employeeId, endDate }: Props) {
  const send = useSend()
  const [reloadKey, setReloadKey] = useState(0)
  const assets = useList<Asset>(`/api/assets?employeeId=${employeeId}`, reloadKey)
  const [savingId, setSavingId] = useState<number | null>(null)
  const [error, setError] = useState('')

  async function markReturned(asset: Asset) {
    setSavingId(asset.id)
    setError('')

    try {
      await send('PATCH', `/api/assets/${asset.id}/assign`, { employeeId: null })
      setReloadKey((k) => k + 1)
    } catch (e) {
      setError(errorText(e))
    } finally {
      setSavingId(null)
    }
  }

  if (assets.loading) return <p>{t('Henter utstyr...')}</p>
  if (assets.error) return <p className="settings-error">{assets.error}</p>

  if (endDate) {
    return (
      <div className="asset-checklist">
        <strong>{t('Innlevering av utstyr (sluttdato {date})', { date: endDate.slice(0, 10) })}</strong>
        {assets.items.length === 0 ? (
          <p>{t('Alt utstyr er levert inn.')}</p>
        ) : (
          <ul>
            {assets.items.map((asset) => (
              <li key={asset.id}>
                <span>☐ {assetName(asset)}</span>
                <button
                  type="button"
                  className="settings-row-action"
                  disabled={savingId !== null}
                  onClick={() => void markReturned(asset)}
                >
                  {savingId === asset.id ? t('Lagrer...') : t('Mottatt')}
                </button>
              </li>
            ))}
          </ul>
        )}
        {error && <p role="alert" className="settings-error">{error}</p>}
      </div>
    )
  }

  return (
    <div>
      <p><strong>{t('Utstyr:')}</strong>{assets.items.length === 0 && ` ${t('Ingen')}`}</p>
      {assets.items.length > 0 && (
        <ul className="asset-tickets">
          {assets.items.map((asset) => (
            <li key={asset.id}>
              {assetName(asset)} · {ASSET_STATUS_LABELS[asset.status] ? t(ASSET_STATUS_LABELS[asset.status]) : asset.status}
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}

import { useMemo, useState } from 'react'
import './Helpdesk.css'
import { useCurrentUser } from '../useCurrentUser'
import {
  ASSET_STATUS_LABELS,
  ASSET_TYPE_LABELS,
  assetName,
  formatDate,
  type Asset,
  type AssetImportResult,
  type AssetTicket,
} from '../assets'
import { priorityLabel, statusLabel } from '../tickets'
import { errorText, useList, useResource, useSend } from './settings/settingsApi'
import { apiUrl, useAuthHeaders } from '../apiClient'

type Employee = {
  id: number
  firstName: string
  lastName: string
  status: string
}

type Draft = {
  assetTag: string
  type: string
  manufacturer: string
  model: string
  serialNumber: string
  status: string
  assignedToEmployeeId: string
  purchaseDate: string
  warrantyEndsAt: string
  notes: string
}

const EMPTY: Draft = {
  assetTag: '',
  type: 'laptop',
  manufacturer: '',
  model: '',
  serialNumber: '',
  status: 'in_stock',
  assignedToEmployeeId: '',
  purchaseDate: '',
  warrantyEndsAt: '',
  notes: '',
}

function toDraft(asset: Asset): Draft {
  return {
    assetTag: asset.assetTag,
    type: asset.type,
    manufacturer: asset.manufacturer ?? '',
    model: asset.model ?? '',
    serialNumber: asset.serialNumber ?? '',
    status: asset.status,
    assignedToEmployeeId: asset.assignedToEmployeeId?.toString() ?? '',
    purchaseDate: asset.purchaseDate?.slice(0, 10) ?? '',
    warrantyEndsAt: asset.warrantyEndsAt?.slice(0, 10) ?? '',
    notes: asset.notes ?? '',
  }
}

// IT-utstyr. Agent og admin registrerer, tildeler og importerer; andre ser eget utstyr.
export default function Assets() {
  const { isHelpdeskAgent } = useCurrentUser()
  const send = useSend()

  const [reloadKey, setReloadKey] = useState(0)
  const [today] = useState(() => new Date().toISOString().slice(0, 10))
  const [status, setStatus] = useState('')
  const [type, setType] = useState('')
  const [search, setSearch] = useState('')

  const query = useMemo(() => {
    const params = new URLSearchParams()
    if (status) params.set('status', status)
    if (type) params.set('type', type)
    if (search.trim()) params.set('search', search.trim())
    return `/api/assets${params.size ? `?${params}` : ''}`
  }, [status, type, search])

  const assets = useList<Asset>(query, reloadKey)
  const employees = useList<Employee>('/api/employees')
  const activeEmployees = employees.items.filter((e) => e.status === 'Aktiv')

  const [selectedId, setSelectedId] = useState<number | null>(null)
  const [editingId, setEditingId] = useState<number | null>(null)
  const [showForm, setShowForm] = useState(false)
  const [draft, setDraft] = useState<Draft>(EMPTY)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState('')
  const [message, setMessage] = useState('')

  function reload() {
    setReloadKey((k) => k + 1)
  }

  function startCreate() {
    setEditingId(null)
    setDraft(EMPTY)
    setShowForm(true)
    setError('')
    setMessage('')
  }

  function startEdit(asset: Asset) {
    setEditingId(asset.id)
    setDraft(toDraft(asset))
    setShowForm(true)
    setError('')
    setMessage('')
  }

  async function save() {
    setSaving(true)
    setError('')
    setMessage('')

    const body = {
      ...draft,
      assignedToEmployeeId: draft.assignedToEmployeeId ? Number(draft.assignedToEmployeeId) : null,
      purchaseDate: draft.purchaseDate || null,
      warrantyEndsAt: draft.warrantyEndsAt || null,
    }

    try {
      if (editingId === null) await send('POST', '/api/assets', body)
      else await send('PUT', `/api/assets/${editingId}`, body)

      setMessage(editingId === null ? 'Utstyret er registrert.' : 'Utstyret er oppdatert.')
      setShowForm(false)
      reload()
    } catch (e) {
      setError(errorText(e))
    } finally {
      setSaving(false)
    }
  }

  function update<K extends keyof Draft>(key: K, value: Draft[K]) {
    setDraft((previous) => ({ ...previous, [key]: value }))
  }

  return (
    <section className="helpdesk">
      <div className="helpdesk-card">
        <h3>IT-utstyr</h3>

        {!isHelpdeskAgent && (
          <p className="audit-intro">Her ser du utstyret som er registrert på deg.</p>
        )}

        <div className="asset-filters">
          <input
            type="search"
            aria-label="Søk"
            placeholder="Søk på utstyrsnummer, serienummer eller modell"
            value={search}
            onChange={(e) => setSearch(e.target.value)}
          />
          <select aria-label="Status" value={status} onChange={(e) => setStatus(e.target.value)}>
            <option value="">Alle statuser</option>
            {Object.entries(ASSET_STATUS_LABELS).map(([value, label]) => (
              <option key={value} value={value}>{label}</option>
            ))}
          </select>
          <select aria-label="Type" value={type} onChange={(e) => setType(e.target.value)}>
            <option value="">Alle typer</option>
            {Object.entries(ASSET_TYPE_LABELS).map(([value, label]) => (
              <option key={value} value={value}>{label}</option>
            ))}
          </select>
          {isHelpdeskAgent && (
            <button type="button" className="asset-primary" onClick={startCreate}>
              Registrer utstyr
            </button>
          )}
        </div>

        {error && <p role="alert" className="settings-error">{error}</p>}
        {message && <p className="settings-message">{message}</p>}

        {isHelpdeskAgent && showForm && (
          <form
            className="helpdesk-form settings-form"
            onSubmit={(e) => {
              e.preventDefault()
              void save()
            }}
          >
            <h4>{editingId === null ? 'Nytt utstyr' : `Endre ${draft.assetTag}`}</h4>

            <div className="settings-form-grid">
              <div>
                <label htmlFor="asset-tag">Utstyrsnummer</label>
                <input id="asset-tag" value={draft.assetTag} maxLength={50} required
                  onChange={(e) => update('assetTag', e.target.value)} />
              </div>
              <div>
                <label htmlFor="asset-type">Type</label>
                <select id="asset-type" value={draft.type} onChange={(e) => update('type', e.target.value)}>
                  {Object.entries(ASSET_TYPE_LABELS).map(([value, label]) => (
                    <option key={value} value={value}>{label}</option>
                  ))}
                </select>
              </div>
              <div>
                <label htmlFor="asset-manufacturer">Produsent</label>
                <input id="asset-manufacturer" value={draft.manufacturer} maxLength={100}
                  onChange={(e) => update('manufacturer', e.target.value)} />
              </div>
              <div>
                <label htmlFor="asset-model">Modell</label>
                <input id="asset-model" value={draft.model} maxLength={100}
                  onChange={(e) => update('model', e.target.value)} />
              </div>
              <div>
                <label htmlFor="asset-serial">Serienummer</label>
                <input id="asset-serial" value={draft.serialNumber} maxLength={100}
                  onChange={(e) => update('serialNumber', e.target.value)} />
              </div>
              <div>
                <label htmlFor="asset-status">Status</label>
                <select id="asset-status" value={draft.status} onChange={(e) => update('status', e.target.value)}>
                  {Object.entries(ASSET_STATUS_LABELS).map(([value, label]) => (
                    <option key={value} value={value}>{label}</option>
                  ))}
                </select>
              </div>
              <div>
                <label htmlFor="asset-assignee">Tildelt</label>
                <select id="asset-assignee" value={draft.assignedToEmployeeId}
                  onChange={(e) => update('assignedToEmployeeId', e.target.value)}>
                  <option value="">Ingen (på lager)</option>
                  {activeEmployees.map((e) => (
                    <option key={e.id} value={e.id}>{`${e.firstName} ${e.lastName}`.trim()}</option>
                  ))}
                </select>
              </div>
              <div>
                <label htmlFor="asset-purchase">Kjøpsdato</label>
                <input id="asset-purchase" type="date" value={draft.purchaseDate}
                  onChange={(e) => update('purchaseDate', e.target.value)} />
              </div>
              <div>
                <label htmlFor="asset-warranty">Garanti til</label>
                <input id="asset-warranty" type="date" value={draft.warrantyEndsAt}
                  onChange={(e) => update('warrantyEndsAt', e.target.value)} />
              </div>
            </div>

            <div>
              <label htmlFor="asset-notes">Notater</label>
              <textarea id="asset-notes" rows={2} maxLength={2000} value={draft.notes}
                onChange={(e) => update('notes', e.target.value)} />
            </div>

            <p className="service-description">
              Tildelt utstyr settes til «I bruk», og utstyr uten ansatt kan ikke være i bruk.
            </p>

            <div className="settings-form-actions">
              <button type="submit" disabled={saving}>
                {saving ? 'Lagrer...' : editingId === null ? 'Registrer' : 'Lagre endringer'}
              </button>
              <button type="button" className="secondary" onClick={() => setShowForm(false)}>
                Avbryt
              </button>
            </div>
          </form>
        )}

        {assets.error && <p role="alert" className="settings-error">{assets.error}</p>}

        {assets.loading ? (
          <p>Henter utstyr...</p>
        ) : assets.items.length === 0 ? (
          <p>Ingen utstyr funnet.</p>
        ) : (
          <div className="audit-table-wrapper">
            <table className="audit-table">
              <thead>
                <tr>
                  <th>Utstyr</th>
                  <th>Type</th>
                  <th>Status</th>
                  <th>Tildelt</th>
                  <th>Garanti til</th>
                </tr>
              </thead>
              <tbody>
                {assets.items.map((asset) => (
                  <AssetRow
                    key={asset.id}
                    asset={asset}
                    open={selectedId === asset.id}
                    onToggle={() => setSelectedId(selectedId === asset.id ? null : asset.id)}
                    onEdit={isHelpdeskAgent ? () => startEdit(asset) : undefined}
                    today={today}
                  />
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>

      {isHelpdeskAgent && <AssetImport onImported={reload} />}
    </section>
  )
}

function AssetRow({
  asset,
  open,
  onToggle,
  onEdit,
  today,
}: {
  asset: Asset
  open: boolean
  onToggle: () => void
  onEdit?: () => void
  // Dagens dato (åååå-mm-dd), for utløpt garanti
  today: string
}) {
  const warrantyExpired = asset.warrantyEndsAt !== null && asset.warrantyEndsAt.slice(0, 10) < today

  return (
    <>
      <tr>
        <td>
          <button type="button" className="asset-link" aria-expanded={open} onClick={onToggle}>
            {open ? '▾' : '▸'} {assetName(asset)}
          </button>
        </td>
        <td>{ASSET_TYPE_LABELS[asset.type] ?? asset.type}</td>
        <td>{ASSET_STATUS_LABELS[asset.status] ?? asset.status}</td>
        <td>{asset.assignedToName ?? '–'}</td>
        <td className={warrantyExpired ? 'settings-muted' : undefined}>
          {formatDate(asset.warrantyEndsAt)}
          {warrantyExpired && ' (utløpt)'}
        </td>
      </tr>
      {open && (
        <tr>
          <td colSpan={5}>
            <AssetDetails id={asset.id} onEdit={onEdit} />
          </td>
        </tr>
      )}
    </>
  )
}

// Detaljer med saker som gjelder utstyret
function AssetDetails({ id, onEdit }: { id: number; onEdit?: () => void }) {
  const details = useResource<{ asset: Asset; tickets: AssetTicket[] }>(`/api/assets/${id}`)

  if (details.loading) return <p>Henter detaljer...</p>
  if (!details.data) return <p className="settings-error">Kunne ikke hente utstyret.</p>

  const { asset, tickets } = details.data

  return (
    <div className="asset-details">
      <dl>
        <dt>Serienummer</dt><dd>{asset.serialNumber ?? '–'}</dd>
        <dt>Kjøpsdato</dt><dd>{formatDate(asset.purchaseDate)}</dd>
        <dt>Kilde</dt><dd>{asset.source === 'intune' ? 'Intune' : 'Registrert manuelt'}</dd>
        {asset.notes && (<><dt>Notater</dt><dd>{asset.notes}</dd></>)}
      </dl>

      {onEdit && (
        <button type="button" className="settings-row-action" onClick={onEdit}>
          Endre
        </button>
      )}

      <h4>Saker på utstyret</h4>
      {tickets.length === 0 ? (
        <p className="settings-muted">Ingen saker.</p>
      ) : (
        <ul className="asset-tickets">
          {tickets.map((t) => (
            <li key={t.id}>
              #{t.id} {t.category} · {statusLabel(t.status)} · {priorityLabel(t.priority)} ·{' '}
              {formatDate(t.createdAt)} – {t.description}
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}

const CSV_TEMPLATE =
  'Utstyrsnummer;Type;Produsent;Modell;Serienummer;Status;Ansatt;Kjøpsdato;Garanti til;Notater\n' +
  'PC-001;Bærbar PC;Lenovo;ThinkPad T14;SN123;;ola.nordmann@firma.no;15.01.2026;15.01.2029;\n'

function AssetImport({ onImported }: { onImported: () => void }) {
  const getAuthHeaders = useAuthHeaders()
  const [result, setResult] = useState<AssetImportResult | null>(null)
  const [error, setError] = useState('')
  const [importing, setImporting] = useState(false)

  async function importFile(file: File) {
    setImporting(true)
    setResult(null)
    setError('')

    try {
      // Både OK og 400 gir { created, updated, errors }
      const response = await fetch(apiUrl('/api/assets/import'), {
        method: 'POST',
        headers: await getAuthHeaders(),
        body: JSON.stringify({ csv: await file.text() }),
      })

      if (response.status !== 200 && response.status !== 400) throw new Error()

      const data: AssetImportResult = await response.json()
      setResult(data)
      if (data.errors.length === 0) onImported()
    } catch {
      setError('Importen feilet. Prøv igjen.')
    } finally {
      setImporting(false)
    }
  }

  return (
    <div className="helpdesk-card">
      <h3>Importer fra CSV</h3>
      <p className="audit-intro">
        Én rad per utstyr, med overskrifter på første rad (norske eller engelske navn). Semikolon og
        komma fungerer begge, så filer fra Excel kan brukes direkte. Finnes utstyrsnummeret fra før,
        oppdateres utstyret med kolonnene i filen. Er én rad feil, importeres ingenting.
      </p>

      <a
        className="settings-row-action"
        download="utstyr-mal.csv"
        href={`data:text/csv;charset=utf-8,${encodeURIComponent('﻿' + CSV_TEMPLATE)}`}
      >
        Last ned mal
      </a>

      <div className="helpdesk-form asset-import">
        <label htmlFor="asset-csv">CSV-fil</label>
        <input
          id="asset-csv"
          type="file"
          accept=".csv,text/csv"
          disabled={importing}
          onChange={(e) => {
            const file = e.target.files?.[0]
            e.target.value = ''
            if (file) void importFile(file)
          }}
        />
      </div>

      {importing && <p>Importerer...</p>}
      {error && <p role="alert" className="settings-error">{error}</p>}

      {result && result.errors.length === 0 && (
        <p className="settings-message">
          Ferdig: {result.created} nye og {result.updated} oppdatert.
        </p>
      )}

      {result && result.errors.length > 0 && (
        <div role="alert" className="settings-error">
          <p>Ingenting ble importert. Rett feilene og prøv igjen:</p>
          <ul>
            {result.errors.map((line) => (
              <li key={line}>{line}</li>
            ))}
          </ul>
        </div>
      )}
    </div>
  )
}

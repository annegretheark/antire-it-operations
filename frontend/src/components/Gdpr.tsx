import { useState } from 'react'
import './Helpdesk.css'
import { apiUrl, useAuthHeaders } from '../apiClient'
import { useCurrentUser } from '../useCurrentUser'
import { formatDate } from '../assets'
import { errorText, useList, useResource, useSend } from './settings/settingsApi'

type Employee = { id: number; firstName: string; lastName: string; status: string; anonymizedAt: string | null }

type Candidate = {
  employeeId: number
  name: string
  endDate: string | null
  assetCount: number
  activeProcess: boolean
  automaticAt: string | null
}

type Settings = { anonymizeAfterMonths: number | null }

type Activity = {
  id: number
  name: string
  system: string | null
  purpose: string
  dataSubjects: string
  dataCategories: string
  legalBasis: string
  retention: string
  recipients: string | null
  thirdCountryTransfers: string | null
  securityMeasures: string | null
  owner: string | null
  lastReviewedAt: string | null
}

const LEGAL_BASIS_LABELS: Record<string, string> = {
  consent: 'Samtykke (a)',
  contract: 'Avtale (b)',
  legal_obligation: 'Rettslig forpliktelse (c)',
  vital_interests: 'Vitale interesser (d)',
  public_task: 'Allmennhetens interesse / offentlig myndighet (e)',
  legitimate_interests: 'Berettiget interesse (f)',
}

// GDPR (Helpdesk.Admin): innsyn, anonymisering og behandlingsprotokoll
export default function Gdpr() {
  const { isHelpdeskAdmin } = useCurrentUser()
  const [tab, setTab] = useState<'persons' | 'protocol'>('persons')

  if (!isHelpdeskAdmin) {
    return (
      <section className="helpdesk">
        <div className="helpdesk-card">
          <h3>GDPR</h3>
          <p>Du trenger rollen Helpdesk.Admin for å se GDPR-sidene.</p>
        </div>
      </section>
    )
  }

  return (
    <section className="helpdesk">
      <div className="settings-tabs" role="tablist" aria-label="GDPR">
        <button type="button" role="tab" aria-selected={tab === 'persons'} onClick={() => setTab('persons')}>
          Innsyn og anonymisering
        </button>
        <button type="button" role="tab" aria-selected={tab === 'protocol'} onClick={() => setTab('protocol')}>
          Behandlingsprotokoll
        </button>
      </div>

      {tab === 'persons' ? (
        <>
          <Access />
          <Anonymization />
        </>
      ) : (
        <Protocol />
      )}
    </section>
  )
}

// Innsyn (art. 15): last ned alt appen har om en person
function Access() {
  const getAuthHeaders = useAuthHeaders()
  const employees = useList<Employee>('/api/employees')
  const [employeeId, setEmployeeId] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')

  async function download() {
    setBusy(true)
    setError('')

    try {
      const response = await fetch(apiUrl(`/api/gdpr/employees/${employeeId}/export`), {
        headers: await getAuthHeaders(),
      })
      if (!response.ok) throw new Error()

      const name = response.headers.get('content-disposition')?.match(/filename="?([^";]+)/)?.[1]
        ?? `innsyn-ansatt-${employeeId}.json`
      const link = document.createElement('a')
      link.href = URL.createObjectURL(await response.blob())
      link.download = name
      link.click()
      URL.revokeObjectURL(link.href)
    } catch {
      setError('Kunne ikke hente innsynet.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="helpdesk-card">
      <h3>Innsyn</h3>
      <p className="audit-intro">
        Last ned alt appen har om en person (ansattopplysninger, saker, tilgangsbestillinger,
        utstyr, onboarding og offboarding, og revisjonslogg) som en JSON-fil. Uttrekket logges.
      </p>
      <div className="asset-filters">
        <select aria-label="Ansatt" value={employeeId} onChange={(e) => setEmployeeId(e.target.value)}>
          <option value="">Velg ansatt</option>
          {employees.items.map((e) => (
            <option key={e.id} value={e.id}>
              {`${e.firstName} ${e.lastName}`.trim()}
              {e.anonymizedAt ? ' (anonymisert)' : e.status !== 'Aktiv' ? ` (${e.status.toLowerCase()})` : ''}
            </option>
          ))}
        </select>
        <button type="button" className="asset-primary" disabled={busy || !employeeId} onClick={() => void download()}>
          {busy ? 'Henter...' : 'Last ned innsyn'}
        </button>
      </div>
      {error && <p role="alert" className="settings-error">{error}</p>}
    </div>
  )
}

// Anonymisering (art. 17 og lagringsbegrensning): manuelt og automatisk etter frist
function Anonymization() {
  const send = useSend()
  const [reloadKey, setReloadKey] = useState(0)
  const candidates = useList<Candidate>('/api/gdpr/candidates', reloadKey)
  const settings = useResource<Settings>('/api/gdpr/settings', reloadKey)
  const [months, setMonths] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [message, setMessage] = useState('')

  const currentMonths = months ?? settings.data?.anonymizeAfterMonths?.toString() ?? ''

  async function run(action: () => Promise<string>) {
    setBusy(true)
    setError('')
    setMessage('')

    try {
      setMessage(await action())
      setMonths(null)
      setReloadKey((k) => k + 1)
    } catch (e) {
      setError(errorText(e))
    } finally {
      setBusy(false)
    }
  }

  function anonymize(c: Candidate) {
    if (!window.confirm(
      `Anonymisere ${c.name}? Navn, e-post, ansattnummer og Entra-ID fjernes fra appen og revisjonsloggen. Det kan ikke angres.`
    )) return

    void run(async () => {
      await send('POST', `/api/gdpr/employees/${c.employeeId}/anonymize`)
      return `${c.name} er anonymisert.`
    })
  }

  return (
    <div className="helpdesk-card">
      <h3>Anonymisering</h3>
      <p className="audit-intro">
        Ansatte som har sluttet, kan anonymiseres når utstyret er levert inn og offboarding er
        fullført. Saker, utstyr og revisjonslogg beholdes, men uten navn og identifikatorer.
        Fritekst i saker (beskrivelser) må vurderes for seg.
      </p>

      <form
        className="helpdesk-form settings-form"
        onSubmit={(e) => {
          e.preventDefault()
          void run(async () => {
            await send('PUT', '/api/gdpr/settings', {
              anonymizeAfterMonths: currentMonths ? Number(currentMonths) : null,
            })
            return currentMonths
              ? `Ansatte anonymiseres automatisk ${currentMonths} måneder etter sluttdato.`
              : 'Automatisk anonymisering er slått av.'
          })
        }}
      >
        <div className="settings-form-grid">
          <div>
            <label htmlFor="gdpr-months">Anonymiser automatisk etter (måneder, tom = av)</label>
            <input id="gdpr-months" type="number" min={1} max={120} value={currentMonths}
              onChange={(e) => setMonths(e.target.value)} />
          </div>
        </div>
        <div className="settings-form-actions">
          <button type="submit" disabled={busy}>Lagre</button>
        </div>
      </form>

      {error && <p role="alert" className="settings-error">{error}</p>}
      {message && <p className="settings-message">{message}</p>}

      {candidates.loading ? (
        <p>Henter...</p>
      ) : candidates.items.length === 0 ? (
        <p>Ingen som har sluttet og ikke er anonymisert.</p>
      ) : (
        <div className="audit-table-wrapper">
          <table className="audit-table">
            <thead>
              <tr>
                <th>Ansatt</th>
                <th>Sluttdato</th>
                <th>Gjenstår</th>
                <th>Automatisk</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {candidates.items.map((c) => {
                const blockers = [
                  c.assetCount > 0 && `${c.assetCount} utstyr ikke levert`,
                  c.activeProcess && 'offboarding pågår',
                ].filter(Boolean)

                return (
                  <tr key={c.employeeId}>
                    <td>{c.name}</td>
                    <td>{formatDate(c.endDate)}</td>
                    <td>{blockers.length ? <span className="settings-error">{blockers.join(', ')}</span> : '–'}</td>
                    <td>{c.automaticAt ? formatDate(c.automaticAt) : 'Av'}</td>
                    <td>
                      <button
                        type="button"
                        className="settings-row-action"
                        disabled={busy || blockers.length > 0}
                        onClick={() => anonymize(c)}
                      >
                        Anonymiser
                      </button>
                    </td>
                  </tr>
                )
              })}
            </tbody>
          </table>
        </div>
      )}
    </div>
  )
}

const EMPTY_ACTIVITY = {
  name: '', system: '', purpose: '', dataSubjects: '', dataCategories: '', legalBasis: 'contract',
  retention: '', recipients: '', thirdCountryTransfers: '', securityMeasures: '', owner: '', lastReviewedAt: '',
}

type ActivityDraft = typeof EMPTY_ACTIVITY

const ACTIVITY_FIELDS: { key: keyof ActivityDraft; label: string; long?: boolean; required?: boolean }[] = [
  { key: 'name', label: 'Behandling', required: true },
  { key: 'system', label: 'System' },
  { key: 'owner', label: 'Ansvarlig' },
  { key: 'purpose', label: 'Formål', long: true, required: true },
  { key: 'dataSubjects', label: 'Registrerte (hvem)', required: true },
  { key: 'dataCategories', label: 'Personopplysninger (hva)', long: true, required: true },
  { key: 'retention', label: 'Lagringstid og sletting', long: true, required: true },
  { key: 'recipients', label: 'Mottakere og databehandlere', long: true },
  { key: 'thirdCountryTransfers', label: 'Overføring utenfor EØS', long: true },
  { key: 'securityMeasures', label: 'Sikkerhetstiltak', long: true },
]

// CSV med semikolon (åpnes direkte i norsk Excel)
function toCsv(activities: Activity[]): string {
  const columns: [string, (a: Activity) => string][] = [
    ['Behandling', (a) => a.name], ['System', (a) => a.system ?? ''], ['Formål', (a) => a.purpose],
    ['Registrerte', (a) => a.dataSubjects], ['Personopplysninger', (a) => a.dataCategories],
    ['Behandlingsgrunnlag', (a) => LEGAL_BASIS_LABELS[a.legalBasis] ?? a.legalBasis],
    ['Lagringstid', (a) => a.retention], ['Mottakere', (a) => a.recipients ?? ''],
    ['Overføring utenfor EØS', (a) => a.thirdCountryTransfers ?? ''],
    ['Sikkerhetstiltak', (a) => a.securityMeasures ?? ''], ['Ansvarlig', (a) => a.owner ?? ''],
    ['Sist gjennomgått', (a) => (a.lastReviewedAt ? a.lastReviewedAt.slice(0, 10) : '')],
  ]
  const quote = (v: string) => `"${v.replace(/"/g, '""')}"`
  return [
    columns.map(([h]) => quote(h)).join(';'),
    ...activities.map((a) => columns.map(([, get]) => quote(get(a))).join(';')),
  ].join('\r\n')
}

// Behandlingsprotokoll (art. 30)
function Protocol() {
  const send = useSend()
  const [reloadKey, setReloadKey] = useState(0)
  const activities = useList<Activity>('/api/gdpr/processing', reloadKey)
  const [editingId, setEditingId] = useState<number | null>(null)
  const [draft, setDraft] = useState<ActivityDraft | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')

  function edit(a: Activity | null) {
    setEditingId(a?.id ?? null)
    setDraft(a ? {
      name: a.name, system: a.system ?? '', purpose: a.purpose, dataSubjects: a.dataSubjects,
      dataCategories: a.dataCategories, legalBasis: a.legalBasis, retention: a.retention,
      recipients: a.recipients ?? '', thirdCountryTransfers: a.thirdCountryTransfers ?? '',
      securityMeasures: a.securityMeasures ?? '', owner: a.owner ?? '',
      lastReviewedAt: a.lastReviewedAt?.slice(0, 10) ?? '',
    } : { ...EMPTY_ACTIVITY })
    setError('')
  }

  async function run(action: () => Promise<unknown>) {
    setBusy(true)
    setError('')

    try {
      await action()
      setDraft(null)
      setEditingId(null)
      setReloadKey((k) => k + 1)
    } catch (e) {
      setError(errorText(e))
    } finally {
      setBusy(false)
    }
  }

  function downloadCsv() {
    const link = document.createElement('a')
    link.href = `data:text/csv;charset=utf-8,${encodeURIComponent('﻿' + toCsv(activities.items))}`
    link.download = 'behandlingsprotokoll.csv'
    link.click()
  }

  return (
    <div className="helpdesk-card">
      <h3>Behandlingsprotokoll</h3>
      <p className="audit-intro">
        Oversikt over behandlinger av personopplysninger (GDPR art. 30). Behandlingen i denne appen
        er lagt inn som utgangspunkt; gå gjennom den og legg til de andre systemene dere bruker.
      </p>

      <div className="settings-form-actions">
        <button type="button" className="asset-primary" onClick={() => edit(null)}>Ny behandling</button>
        <button type="button" className="settings-row-action" disabled={activities.items.length === 0} onClick={downloadCsv}>
          Last ned som CSV
        </button>
      </div>

      {draft && (
        <form
          className="helpdesk-form settings-form"
          onSubmit={(e) => {
            e.preventDefault()
            const body = { ...draft, lastReviewedAt: draft.lastReviewedAt || null }
            void run(() => editingId === null
              ? send('POST', '/api/gdpr/processing', body)
              : send('PUT', `/api/gdpr/processing/${editingId}`, body))
          }}
        >
          <h4>{editingId === null ? 'Ny behandling' : `Endre «${draft.name}»`}</h4>
          <div className="settings-form-grid">
            {ACTIVITY_FIELDS.filter((f) => !f.long).map((f) => (
              <div key={f.key}>
                <label htmlFor={`pa-${f.key}`}>{f.label}</label>
                <input id={`pa-${f.key}`} value={draft[f.key]} required={f.required}
                  onChange={(e) => setDraft({ ...draft, [f.key]: e.target.value })} />
              </div>
            ))}
            <div>
              <label htmlFor="pa-basis">Behandlingsgrunnlag (art. 6)</label>
              <select id="pa-basis" value={draft.legalBasis}
                onChange={(e) => setDraft({ ...draft, legalBasis: e.target.value })}>
                {Object.entries(LEGAL_BASIS_LABELS).map(([v, l]) => <option key={v} value={v}>{l}</option>)}
              </select>
            </div>
            <div>
              <label htmlFor="pa-reviewed">Sist gjennomgått</label>
              <input id="pa-reviewed" type="date" value={draft.lastReviewedAt}
                onChange={(e) => setDraft({ ...draft, lastReviewedAt: e.target.value })} />
            </div>
          </div>
          {ACTIVITY_FIELDS.filter((f) => f.long).map((f) => (
            <div key={f.key}>
              <label htmlFor={`pa-${f.key}`}>{f.label}</label>
              <textarea id={`pa-${f.key}`} rows={2} value={draft[f.key]} required={f.required}
                onChange={(e) => setDraft({ ...draft, [f.key]: e.target.value })} />
            </div>
          ))}
          <div className="settings-form-actions">
            <button type="submit" disabled={busy}>Lagre</button>
            <button type="button" className="secondary" onClick={() => { setDraft(null); setEditingId(null) }}>
              Avbryt
            </button>
          </div>
        </form>
      )}

      {error && <p role="alert" className="settings-error">{error}</p>}

      {activities.loading ? (
        <p>Henter...</p>
      ) : (
        activities.items.map((a) => (
          <div key={a.id} className="helpdesk-ticket">
            <h4>{a.name}{a.system && <span className="settings-muted"> · {a.system}</span>}</h4>
            <dl className="protocol-entry">
              <dt>Formål</dt><dd>{a.purpose}</dd>
              <dt>Registrerte</dt><dd>{a.dataSubjects}</dd>
              <dt>Personopplysninger</dt><dd>{a.dataCategories}</dd>
              <dt>Grunnlag</dt><dd>{LEGAL_BASIS_LABELS[a.legalBasis] ?? a.legalBasis}</dd>
              <dt>Lagringstid</dt><dd>{a.retention}</dd>
              {a.recipients && (<><dt>Mottakere</dt><dd>{a.recipients}</dd></>)}
              {a.thirdCountryTransfers && (<><dt>Utenfor EØS</dt><dd>{a.thirdCountryTransfers}</dd></>)}
              {a.securityMeasures && (<><dt>Sikkerhet</dt><dd>{a.securityMeasures}</dd></>)}
              {a.owner && (<><dt>Ansvarlig</dt><dd>{a.owner}</dd></>)}
              <dt>Sist gjennomgått</dt><dd>{a.lastReviewedAt ? formatDate(a.lastReviewedAt) : 'Ikke registrert'}</dd>
            </dl>
            <div className="settings-form-actions">
              <button type="button" className="settings-row-action" onClick={() => edit(a)}>Endre</button>
              <button
                type="button"
                className="settings-row-action"
                disabled={busy}
                onClick={() => {
                  if (window.confirm(`Slette «${a.name}» fra protokollen?`)) {
                    void run(() => send('DELETE', `/api/gdpr/processing/${a.id}`))
                  }
                }}
              >
                Slett
              </button>
            </div>
          </div>
        ))
      )}
    </div>
  )
}

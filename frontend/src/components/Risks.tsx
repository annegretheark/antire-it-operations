import { useMemo, useState } from 'react'
import './Helpdesk.css'
import { formatDate } from '../assets'
import { formatDateTime } from '../audit'
import {
  RISK_CATEGORIES,
  RISK_LEVEL_CODES,
  RISK_STATUS_CODES,
  RISK_STRATEGIES,
  riskCategoryLabel,
  riskLevel,
  riskLevelLabel,
  riskStatusLabel,
  riskStrategyLabel,
  type ChangeOptions,
  type RiskDetails,
  type RiskMatrix,
  type RiskSummary,
} from '../governance'
import { Badge, History } from './Changes'
import { errorText, useList, useResource, useSend } from './settings/settingsApi'
import { t } from '../i18n'

type View = { kind: 'list' } | { kind: 'new' } | { kind: 'details'; id: number }

type Filters = { category: string; status: string; level: string; flag: string; cell: [number, number] | null }

// Risikoregisteret (ENDRING_OG_RISIKO.md, C4). Aksept og statuser avgjøres på serveren.
export default function Risks() {
  const [view, setView] = useState<View>({ kind: 'list' })
  const [reloadKey, setReloadKey] = useState(0)
  const options = useResource<ChangeOptions>('/api/changes/options')

  return (
    <section className="helpdesk">
      <div className="settings-tabs" role="tablist" aria-label={t('Risiko')}>
        <button type="button" role="tab" aria-selected={view.kind !== 'new'} onClick={() => setView({ kind: 'list' })}>
          {t('Risikoregister')}
        </button>
        <button type="button" role="tab" aria-selected={view.kind === 'new'} onClick={() => setView({ kind: 'new' })}>
          {t('Ny risiko')}
        </button>
      </div>

      {options.error && (
        <div className="helpdesk-card">
          <p>{t('Du trenger rollen Helpdesk.Agent eller en rolle i endrings- og risikoprosessen for å se risikoer.')}</p>
        </div>
      )}

      {view.kind === 'list' && <RiskOverview reloadKey={reloadKey} onOpen={(id) => setView({ kind: 'details', id })} />}
      {view.kind === 'new' && (
        <RiskForm options={options.data} onSaved={(d) => { setReloadKey((k) => k + 1); setView({ kind: 'details', id: d.risk.id }) }} />
      )}
      {view.kind === 'details' && (
        <RiskDetailsView id={view.id} options={options.data} onChanged={() => setReloadKey((k) => k + 1)} />
      )}
    </section>
  )
}

function RiskOverview({ reloadKey, onOpen }: { reloadKey: number; onOpen: (id: number) => void }) {
  const [filters, setFilters] = useState<Filters>({ category: '', status: '', level: '', flag: '', cell: null })
  const [residual, setResidual] = useState(false)
  const matrix = useResource<RiskMatrix>('/api/risks/matrix', reloadKey)

  const url = useMemo(() => {
    const p = new URLSearchParams()
    if (filters.category) p.set('category', filters.category)
    if (filters.status) p.set('status', filters.status)
    if (filters.level) p.set('level', filters.level)
    if (filters.flag) p.set(filters.flag, 'true')
    return `/api/risks${p.size ? `?${p}` : ''}`
  }, [filters])

  const risks = useList<RiskSummary>(url, reloadKey)
  // Filter fra matrisen (bare i visningen før tiltak; rutene er ikke klikkbare for restrisiko)
  const cell = residual ? null : filters.cell
  const shown = cell
    ? risks.items.filter((r) => r.likelihood === cell[0] && r.consequence === cell[1])
    : risks.items

  const grid = residual ? matrix.data?.residual : matrix.data?.inherent

  return (
    <>
      <div className="helpdesk-card">
        <h3>{t('Risikomatrise')}</h3>
        <label className="settings-check">
          <input type="checkbox" checked={residual} onChange={(e) => { setResidual(e.target.checked); setFilters((f) => ({ ...f, cell: null })) }} />
          {t('Vis restrisiko (etter tiltak)')}
        </label>
        {grid && (
          <div className="gov-matrix" role="grid" aria-label={t('Risikomatrise')}>
            <div className="gov-matrix-axis">{t('Sannsynlighet')}</div>
            {[5, 4, 3, 2, 1].map((l) => (
              <div key={l} className="gov-matrix-row" role="row">
                <span className="gov-matrix-label">{l}</span>
                {[1, 2, 3, 4, 5].map((c) => {
                  const count = grid[l - 1][c - 1]
                  const selected = filters.cell?.[0] === l && filters.cell?.[1] === c
                  return (
                    <button key={c} type="button" role="gridcell"
                      className={`gov-matrix-cell gov-level-${riskLevel(l * c)}${selected ? ' selected' : ''}`}
                      disabled={residual}
                      title={t('Sannsynlighet {l}, konsekvens {c}', { l, c })}
                      onClick={() => setFilters((f) => ({ ...f, cell: selected ? null : [l, c] }))}>
                      {count || ''}
                    </button>
                  )
                })}
              </div>
            ))}
            <div className="gov-matrix-row">
              <span className="gov-matrix-label" />
              {[1, 2, 3, 4, 5].map((c) => <span key={c} className="gov-matrix-label">{c}</span>)}
            </div>
            <div className="gov-matrix-axis">{t('Konsekvens')}</div>
          </div>
        )}
        <p className="settings-muted">{t('{n} åpne risikoer. Klikk i en rute for å filtrere listen.', { n: matrix.data?.openRisks ?? 0 })}</p>
      </div>

      <div className="helpdesk-card">
        <h3>{t('Risikoer')}</h3>
        <div className="asset-filters">
          <select aria-label={t('Kategori')} value={filters.category} onChange={(e) => setFilters({ ...filters, category: e.target.value })}>
            <option value="">{t('Alle kategorier')}</option>
            {Object.keys(RISK_CATEGORIES).map((c) => <option key={c} value={c}>{riskCategoryLabel(c)}</option>)}
          </select>
          <select aria-label={t('Status')} value={filters.status} onChange={(e) => setFilters({ ...filters, status: e.target.value })}>
            <option value="">{t('Alle statuser')}</option>
            {RISK_STATUS_CODES.map((c) => <option key={c} value={c}>{riskStatusLabel(c)}</option>)}
          </select>
          <select aria-label={t('Nivå')} value={filters.level} onChange={(e) => setFilters({ ...filters, level: e.target.value })}>
            <option value="">{t('Alle nivåer')}</option>
            {RISK_LEVEL_CODES.map((c) => <option key={c} value={c}>{riskLevelLabel(c)}</option>)}
          </select>
          <select aria-label={t('Vis')} value={filters.flag} onChange={(e) => setFilters({ ...filters, flag: e.target.value })}>
            <option value="">{t('Alle')}</option>
            <option value="unowned">{t('Uten eier')}</option>
            <option value="overdue">{t('Forfalte tiltak')}</option>
            <option value="awaitingAcceptance">{t('Venter på aksept')}</option>
          </select>
          {filters.cell && (
            <button type="button" className="settings-row-action" onClick={() => setFilters({ ...filters, cell: null })}>
              {t('Fjern filter fra matrisen')}
            </button>
          )}
        </div>

        {risks.error && <p role="alert" className="settings-error">{risks.error}</p>}
        {risks.loading ? <p>{t('Henter risikoer...')}</p> : shown.length === 0 ? <p>{t('Ingen risikoer.')}</p> : (
          <div className="audit-table-wrapper">
            <table className="audit-table">
              <thead>
                <tr>
                  <th>{t('Nummer')}</th><th>{t('Tittel')}</th><th>{t('Kategori')}</th><th>{t('Score')}</th>
                  <th>{t('Status')}</th><th>{t('Strategi')}</th><th>{t('Frist')}</th>
                </tr>
              </thead>
              <tbody>
                {shown.map((r) => (
                  <tr key={r.id} className="gov-row" onClick={() => onOpen(r.id)}>
                    <td className="audit-nowrap"><button type="button" className="gov-link" onClick={() => onOpen(r.id)}>{r.number}</button></td>
                    <td>{r.title}{r.ownerEmployeeId == null && <span className="settings-error"> · {t('uten eier')}</span>}</td>
                    <td>{riskCategoryLabel(r.category)}</td>
                    <td><Badge kind={`level-${r.level}`}>{r.score} · {riskLevelLabel(r.level)}</Badge></td>
                    <td>{riskStatusLabel(r.status)}</td>
                    <td>{riskStrategyLabel(r.strategy)}</td>
                    <td className="audit-nowrap">{r.dueDate ? formatDate(r.dueDate) : '–'}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>
    </>
  )
}

type RiskFormState = {
  title: string; description: string; category: string; likelihood: string; consequence: string
  existingControls: string; recommendedActions: string; residualLikelihood: string; residualConsequence: string
  strategy: string; ownerEmployeeId: string; actionOwnerEmployeeId: string; dueDate: string; nextReviewAt: string
}

function RiskForm({ options, existing, onSaved, onCancel }: {
  options: ChangeOptions | null; existing?: RiskDetails; onSaved: (d: RiskDetails) => void; onCancel?: () => void
}) {
  const send = useSend()
  const r = existing?.risk
  const s = (v: string | number | null | undefined) => (v == null ? '' : String(v))
  const [f, setF] = useState<RiskFormState>({
    title: s(r?.title), description: s(r?.description), category: r?.category ?? 'operational_risk',
    likelihood: s(r?.likelihood ?? 3), consequence: s(r?.consequence ?? 3),
    existingControls: s(r?.existingControls), recommendedActions: s(r?.recommendedActions),
    residualLikelihood: s(r?.residualLikelihood), residualConsequence: s(r?.residualConsequence),
    strategy: s(r?.strategy), ownerEmployeeId: s(r?.ownerEmployeeId), actionOwnerEmployeeId: s(r?.actionOwnerEmployeeId),
    dueDate: r?.dueDate?.slice(0, 10) ?? '', nextReviewAt: r?.nextReviewAt?.slice(0, 10) ?? '',
  })
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const set = <K extends keyof RiskFormState>(k: K, v: RiskFormState[K]) => setF((p) => ({ ...p, [k]: v }))

  async function save() {
    const num = (v: string) => (v === '' ? null : Number(v))
    const str = (v: string) => (v.trim() === '' ? null : v)
    const date = (v: string) => (v ? new Date(`${v}T12:00:00`).toISOString() : null)
    const body = {
      title: f.title, description: f.description, category: f.category,
      likelihood: Number(f.likelihood), consequence: Number(f.consequence),
      existingControls: str(f.existingControls), recommendedActions: str(f.recommendedActions),
      residualLikelihood: num(f.residualLikelihood), residualConsequence: num(f.residualConsequence),
      strategy: str(f.strategy), ownerEmployeeId: num(f.ownerEmployeeId), actionOwnerEmployeeId: num(f.actionOwnerEmployeeId),
      dueDate: date(f.dueDate), nextReviewAt: date(f.nextReviewAt),
    }
    if (existing?.acceptanceActive && Number(f.likelihood) * Number(f.consequence) > existing.risk.score &&
        !window.confirm(t('Høyere score gjør at aksepten ikke gjelder lenger. Fortsette?')))
      return
    setBusy(true)
    setError('')
    try {
      const response = existing
        ? await send('PUT', `/api/risks/${existing.risk.id}`, body)
        : await send('POST', '/api/risks', body)
      onSaved(await response.json())
    } catch (e) {
      setError(errorText(e))
    } finally {
      setBusy(false)
    }
  }

  const scale = (id: string, label: string, key: keyof RiskFormState, optional = false) => (
    <div>
      <label htmlFor={id}>{label}</label>
      <select id={id} value={f[key]} onChange={(e) => set(key, e.target.value)}>
        {optional && <option value="">–</option>}
        {[1, 2, 3, 4, 5].map((n) => <option key={n} value={n}>{n}</option>)}
      </select>
    </div>
  )

  const employee = (id: string, label: string, key: 'ownerEmployeeId' | 'actionOwnerEmployeeId') => (
    <div>
      <label htmlFor={id}>{label}</label>
      <select id={id} value={f[key]} onChange={(e) => set(key, e.target.value)}>
        <option value="">{t('Ingen')}</option>
        {options?.employees.map((e) => <option key={e.id} value={e.id}>{e.name}</option>)}
      </select>
    </div>
  )

  const score = Number(f.likelihood) * Number(f.consequence)

  return (
    <div className="helpdesk-card">
      <h3>{existing ? t('Endre {number}', { number: existing.risk.number }) : t('Ny risiko')}</h3>
      <form className="helpdesk-form settings-form" onSubmit={(e) => { e.preventDefault(); void save() }}>
        <div className="settings-form-grid">
          <div>
            <label htmlFor="risk-title">{t('Tittel')}</label>
            <input id="risk-title" value={f.title} maxLength={200} required onChange={(e) => set('title', e.target.value)} />
          </div>
          <div>
            <label htmlFor="risk-cat">{t('Kategori')}</label>
            <select id="risk-cat" value={f.category} onChange={(e) => set('category', e.target.value)}>
              {Object.keys(RISK_CATEGORIES).map((c) => <option key={c} value={c}>{riskCategoryLabel(c)}</option>)}
            </select>
          </div>
          {scale('risk-l', t('Sannsynlighet (1–5)'), 'likelihood')}
          {scale('risk-c', t('Konsekvens (1–5)'), 'consequence')}
        </div>
        <p>{t('Score')}: <Badge kind={`level-${riskLevel(score)}`}>{score} · {riskLevelLabel(riskLevel(score))}</Badge></p>
        <div>
          <label htmlFor="risk-desc">{t('Beskrivelse')}</label>
          <textarea id="risk-desc" rows={3} required value={f.description} onChange={(e) => set('description', e.target.value)} />
        </div>
        <div>
          <label htmlFor="risk-controls">{t('Eksisterende sikkerhetstiltak')}</label>
          <textarea id="risk-controls" rows={2} value={f.existingControls} onChange={(e) => set('existingControls', e.target.value)} />
        </div>
        <div>
          <label htmlFor="risk-actions">{t('Anbefalte tiltak')}</label>
          <textarea id="risk-actions" rows={2} value={f.recommendedActions} onChange={(e) => set('recommendedActions', e.target.value)} />
        </div>
        <div className="settings-form-grid">
          {scale('risk-rl', t('Restrisiko: sannsynlighet'), 'residualLikelihood', true)}
          {scale('risk-rc', t('Restrisiko: konsekvens'), 'residualConsequence', true)}
          <div>
            <label htmlFor="risk-strategy">{t('Strategi')}</label>
            <select id="risk-strategy" value={f.strategy} onChange={(e) => set('strategy', e.target.value)}>
              <option value="">{t('Ikke valgt')}</option>
              {Object.keys(RISK_STRATEGIES).map((c) => <option key={c} value={c}>{riskStrategyLabel(c)}</option>)}
            </select>
          </div>
          {employee('risk-owner', t('Risikoeier'), 'ownerEmployeeId')}
          {employee('risk-action-owner', t('Ansvarlig for tiltak'), 'actionOwnerEmployeeId')}
          <div>
            <label htmlFor="risk-due">{t('Frist for tiltak')}</label>
            <input id="risk-due" type="date" value={f.dueDate} onChange={(e) => set('dueDate', e.target.value)} />
          </div>
          <div>
            <label htmlFor="risk-review">{t('Neste vurdering')}</label>
            <input id="risk-review" type="date" value={f.nextReviewAt} onChange={(e) => set('nextReviewAt', e.target.value)} />
          </div>
        </div>
        {error && <p role="alert" className="settings-error">{error}</p>}
        <div className="settings-form-actions">
          <button type="submit" disabled={busy}>{existing ? t('Lagre') : t('Registrer risiko')}</button>
          {onCancel && <button type="button" className="secondary" onClick={onCancel}>{t('Avbryt')}</button>}
        </div>
      </form>
    </div>
  )
}

const RISK_TRANSITIONS: Record<string, string> = {
  assessed: 'Marker som vurdert',
  in_treatment: 'Start behandling',
  monitoring: 'Tiltak gjennomført (overvåkes)',
  closed: 'Lukk risikoen',
}

function RiskDetailsView({ id, options, onChanged }: { id: number; options: ChangeOptions | null; onChanged: () => void }) {
  const send = useSend()
  const [reloadKey, setReloadKey] = useState(0)
  const details = useResource<RiskDetails>(`/api/risks/${id}`, reloadKey)
  const [editing, setEditing] = useState(false)
  const [showHistory, setShowHistory] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [justification, setJustification] = useState('')
  const [validUntil, setValidUntil] = useState(() => {
    const d = new Date()
    d.setMonth(d.getMonth() + 6)
    return d.toISOString().slice(0, 10)
  })

  async function run(action: () => Promise<unknown>) {
    setBusy(true)
    setError('')
    try {
      await action()
      setJustification('')
      setReloadKey((k) => k + 1)
      onChanged()
    } catch (e) {
      setError(errorText(e))
    } finally {
      setBusy(false)
    }
  }

  if (details.loading && !details.data) return <div className="helpdesk-card"><p>{t('Henter risikoen...')}</p></div>
  if (!details.data) return <div className="helpdesk-card"><p className="settings-error">{details.error || t('Fant ikke risikoen.')}</p></div>

  const d = details.data
  const r = d.risk
  const employee = (eid: number | null) => (eid == null ? '–' : options?.employees.find((e) => e.id === eid)?.name ?? `#${eid}`)

  if (editing) {
    return <RiskForm options={options} existing={d} onCancel={() => setEditing(false)}
      onSaved={() => { setEditing(false); setReloadKey((k) => k + 1); onChanged() }} />
  }

  function transition(to: string) {
    let note: string | null = null
    if (to === 'closed') {
      note = window.prompt(t('Hvorfor lukkes risikoen?'))
      if (!note) return
    }
    void run(() => send('POST', `/api/risks/${r.id}/transitions`, { to, note }))
  }

  const canAcceptNow = d.canAccept && r.strategy === 'accept' && ['assessed', 'monitoring'].includes(r.status)

  return (
    <div className="helpdesk-card">
      <h3>{r.number} · {r.title}</h3>
      <p>
        <Badge kind={`level-${d.level}`}>{r.score} · {riskLevelLabel(d.level)}</Badge>{' '}
        <strong>{riskStatusLabel(r.status)}</strong> · {riskCategoryLabel(r.category)}
        {d.acceptanceActive && <span className="settings-message"> · {t('Akseptert')}</span>}
        {d.acceptanceInactiveReason && <span className="settings-muted"> · {d.acceptanceInactiveReason}</span>}
      </p>

      {error && <p role="alert" className="settings-error">{error}</p>}

      <div className="gov-actions">
        {d.allowedTransitions.map((to) => (
          <button key={to} type="button" className="settings-row-action" disabled={busy} onClick={() => transition(to)}>
            {t(RISK_TRANSITIONS[to] ?? to)}
          </button>
        ))}
        {d.canEdit && r.status !== 'closed' && (
          <button type="button" className="settings-row-action" onClick={() => setEditing(true)}>{t('Endre')}</button>
        )}
        <button type="button" className="settings-row-action" onClick={() => setShowHistory((s) => !s)}>
          {showHistory ? t('Skjul historikk') : t('Vis historikk')}
        </button>
      </div>

      {(canAcceptNow || (d.canAccept && d.acceptanceActive)) && (
        <div className="gov-decision">
          <h4>{t('Risikoaksept')}</h4>
          <label htmlFor="risk-justification">{t('Begrunnelse')}</label>
          <textarea id="risk-justification" rows={2} value={justification} onChange={(e) => setJustification(e.target.value)} />
          {canAcceptNow && (
            <div className="gov-actions">
              <label htmlFor="risk-valid">{t('Gyldig til')}</label>
              <input id="risk-valid" type="date" value={validUntil} onChange={(e) => setValidUntil(e.target.value)} />
              <button type="button" className="settings-row-action" disabled={busy || !justification.trim()}
                onClick={() => void run(() => send('POST', `/api/risks/${r.id}/acceptances`, {
                  justification, validUntil: new Date(`${validUntil}T12:00:00`).toISOString(), expectedScore: r.score,
                }))}>
                {t('Aksepter risikoen')}
              </button>
            </div>
          )}
          {d.acceptanceActive && (
            <button type="button" className="settings-row-action m365-danger" disabled={busy || !justification.trim()}
              onClick={() => void run(() => send('POST', `/api/risks/${r.id}/acceptances/revoke`, { justification }))}>
              {t('Trekk tilbake aksept')}
            </button>
          )}
        </div>
      )}

      <dl className="gov-fields">
        <dt>{t('Sannsynlighet × konsekvens')}</dt><dd>{r.likelihood} × {r.consequence} = {r.score}</dd>
        <dt>{t('Restrisiko')}</dt><dd>{r.residualScore != null ? `${r.residualLikelihood} × ${r.residualConsequence} = ${r.residualScore} · ${riskLevelLabel(d.residualLevel)}` : '–'}</dd>
        <dt>{t('Strategi')}</dt><dd>{riskStrategyLabel(r.strategy)}</dd>
        <dt>{t('Risikoeier')}</dt><dd>{employee(r.ownerEmployeeId)}</dd>
        <dt>{t('Ansvarlig for tiltak')}</dt><dd>{employee(r.actionOwnerEmployeeId)}</dd>
        <dt>{t('Frist for tiltak')}</dt><dd>{r.dueDate ? formatDate(r.dueDate) : '–'}</dd>
        <dt>{t('Neste vurdering')}</dt><dd>{r.nextReviewAt ? formatDate(r.nextReviewAt) : '–'}</dd>
        <dt>{t('Koblede endringer')}</dt><dd>{d.links.changeIds.map((x) => `#${x}`).join(', ') || '–'}</dd>
        <dt>{t('Systemer og tjenester')}</dt><dd>{d.links.serviceIds.map((x) => options?.services.find((s) => s.id === x)?.name ?? `#${x}`).join(', ') || '–'}</dd>
        <dt>{t('Saker og varsler')}</dt><dd>{[...d.links.ticketIds.map((x) => `${t('sak')} #${x}`), ...d.links.alertIds.map((x) => `${t('varsel')} #${x}`)].join(', ') || '–'}</dd>
      </dl>

      {d.canEdit && <RiskLinksEditor riskId={r.id} links={d.links} options={options} onSaved={() => setReloadKey((k) => k + 1)} />}

      {([['Beskrivelse', r.description], ['Eksisterende sikkerhetstiltak', r.existingControls], ['Anbefalte tiltak', r.recommendedActions]] as [string, string | null][])
        .filter(([, v]) => v).map(([label, value]) => (
          <div key={label} className="gov-text"><h4>{t(label)}</h4><p>{value}</p></div>
        ))}

      {r.acceptances.length > 0 && (
        <>
          <h4>{t('Akseptshistorikk')}</h4>
          <ul>
            {r.acceptances.map((a) => (
              <li key={a.id}>
                {formatDateTime(a.decidedAt)} · {a.actorName}: <strong>{t(a.kind === 'accepted' ? 'Akseptert' : 'Trukket tilbake')}</strong>
                {' '}({t('score')} {a.scoreAtDecision}{a.validUntil ? `, ${t('gyldig til')} ${formatDate(a.validUntil)}` : ''})
                {a.justification && ` – ${a.justification}`}
              </li>
            ))}
          </ul>
        </>
      )}

      {showHistory && <History url={`/api/risks/${r.id}/history`} />}
    </div>
  )
}

// Koblinger til endringer, tjenester, saker og varsler (ID-er; tjenester fra listen)
function RiskLinksEditor({ riskId, links, options, onSaved }: {
  riskId: number; links: RiskDetails['links']; options: ChangeOptions | null; onSaved: () => void
}) {
  const send = useSend()
  const [open, setOpen] = useState(false)
  const [changeIds, setChangeIds] = useState(links.changeIds.join(', '))
  const [ticketIds, setTicketIds] = useState(links.ticketIds.join(', '))
  const [alertIds, setAlertIds] = useState(links.alertIds.join(', '))
  const [serviceIds, setServiceIds] = useState<number[]>(links.serviceIds)
  const [error, setError] = useState('')

  const ids = (v: string) => v.split(/[\s,]+/).filter(Boolean).map(Number).filter((n) => Number.isInteger(n) && n > 0)

  if (!open) return <button type="button" className="settings-row-action" onClick={() => setOpen(true)}>{t('Endre koblinger')}</button>

  return (
    <div className="gov-decision">
      <h4>{t('Koblinger')}</h4>
      <label htmlFor="link-changes">{t('Endringer (ID-er, kommaseparert)')}</label>
      <input id="link-changes" value={changeIds} onChange={(e) => setChangeIds(e.target.value)} />
      <label htmlFor="link-tickets">{t('Saker (ID-er)')}</label>
      <input id="link-tickets" value={ticketIds} onChange={(e) => setTicketIds(e.target.value)} />
      <label htmlFor="link-alerts">{t('Varsler (ID-er)')}</label>
      <input id="link-alerts" value={alertIds} onChange={(e) => setAlertIds(e.target.value)} />
      <fieldset className="gov-checklist">
        <legend>{t('Systemer og tjenester')}</legend>
        {options?.services.map((s) => (
          <label key={s.id} className="settings-check">
            <input type="checkbox" checked={serviceIds.includes(s.id)}
              onChange={(e) => setServiceIds(e.target.checked ? [...serviceIds, s.id] : serviceIds.filter((x) => x !== s.id))} />
            {s.name}
          </label>
        ))}
      </fieldset>
      {error && <p role="alert" className="settings-error">{error}</p>}
      <div className="gov-actions">
        <button type="button" className="settings-row-action" onClick={async () => {
          setError('')
          try {
            await send('PUT', `/api/risks/${riskId}/links`, {
              changeIds: ids(changeIds), serviceIds, ticketIds: ids(ticketIds), alertIds: ids(alertIds),
            })
            setOpen(false)
            onSaved()
          } catch (e) {
            setError(errorText(e))
          }
        }}>{t('Lagre koblinger')}</button>
        <button type="button" className="settings-row-action" onClick={() => setOpen(false)}>{t('Avbryt')}</button>
      </div>
    </div>
  )
}

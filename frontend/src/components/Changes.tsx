import { useMemo, useState, type ReactNode } from 'react'
import './Helpdesk.css'
import { useCurrentUser } from '../useCurrentUser'
import { formatDateTime } from '../audit'
import { priorityLabel, PRIORITIES } from '../tickets'
import {
  BACKUP_STATUS_CODES,
  CHANGE_STATUS_CODES,
  CHANGE_TYPE_CODES,
  ENVIRONMENT_CODES,
  backupStatusLabel,
  changeStatusLabel,
  changeTypeLabel,
  environmentLabel,
  fromLocalInput,
  roleLabel,
  linkedId,
  startOfWeek,
  toLocalInput,
  type AuditEntry,
  type ChangeDetails,
  type ChangeOptions,
  type ChangeSummary,
} from '../governance'
import { errorText, useList, useResource, useSend } from './settings/settingsApi'
import { apiUrl, useAuthHeaders } from '../apiClient'
import { locale, t } from '../i18n'

type View = { kind: 'list' } | { kind: 'calendar' } | { kind: 'new' } | { kind: 'details'; id: number }

// Endringsforespørsler (ENDRING_OG_RISIKO.md, C4). Knappene vises ut fra hva API-et
// sier at innlogget person kan gjøre; serveren sjekker uansett på nytt.
export default function Changes() {
  const { isHelpdeskAgent } = useCurrentUser()
  // ?endring=<id> (lenke fra Teams) åpner endringen
  const [view, setView] = useState<View>(() => linkedId('endring') ?? { kind: 'list' })
  const [reloadKey, setReloadKey] = useState(0)
  const options = useResource<ChangeOptions>('/api/changes/options')

  return (
    <section className="helpdesk">
      <div className="settings-tabs" role="tablist" aria-label={t('Endringer')}>
        <button type="button" role="tab" aria-selected={view.kind === 'list' || view.kind === 'details'}
          onClick={() => setView({ kind: 'list' })}>{t('Endringer')}</button>
        <button type="button" role="tab" aria-selected={view.kind === 'calendar'}
          onClick={() => setView({ kind: 'calendar' })}>{t('Kalender')}</button>
        <button type="button" role="tab" aria-selected={view.kind === 'new'}
          onClick={() => setView({ kind: 'new' })}>{t('Ny endring')}</button>
      </div>

      {options.error && !isHelpdeskAgent && (
        <div className="helpdesk-card">
          <p>{t('Du trenger rollen Helpdesk.Agent eller en rolle i endringsprosessen for å se endringer.')}</p>
        </div>
      )}

      {view.kind === 'list' && (
        <ChangeList reloadKey={reloadKey} onOpen={(id) => setView({ kind: 'details', id })} />
      )}
      {view.kind === 'calendar' && <ChangeCalendar onOpen={(id) => setView({ kind: 'details', id })} />}
      {view.kind === 'new' && (
        <ChangeForm
          options={options.data}
          onSaved={(d) => {
            setReloadKey((k) => k + 1)
            setView({ kind: 'details', id: d.change.id })
          }}
        />
      )}
      {view.kind === 'details' && (
        <ChangeDetailsView id={view.id} options={options.data} onChanged={() => setReloadKey((k) => k + 1)} />
      )}
    </section>
  )
}

export function Badge({ kind, children }: { kind: string; children: ReactNode }) {
  return <span className={`gov-badge gov-${kind}`}>{children}</span>
}

function ChangeList({ reloadKey, onOpen }: { reloadKey: number; onOpen: (id: number) => void }) {
  const [status, setStatus] = useState('')
  const [type, setType] = useState('')
  const [environment, setEnvironment] = useState('')
  const [mine, setMine] = useState(false)

  const url = useMemo(() => {
    const p = new URLSearchParams()
    if (status) p.set('status', status)
    if (type) p.set('type', type)
    if (environment) p.set('environment', environment)
    if (mine) p.set('mine', 'true')
    return `/api/changes${p.size ? `?${p}` : ''}`
  }, [status, type, environment, mine])

  const changes = useList<ChangeSummary>(url, reloadKey)

  return (
    <div className="helpdesk-card">
      <h3>{t('Endringer')}</h3>
      <div className="asset-filters">
        <select aria-label={t('Status')} value={status} onChange={(e) => setStatus(e.target.value)}>
          <option value="">{t('Alle statuser')}</option>
          {CHANGE_STATUS_CODES.map((c) => <option key={c} value={c}>{changeStatusLabel(c)}</option>)}
        </select>
        <select aria-label={t('Type')} value={type} onChange={(e) => setType(e.target.value)}>
          <option value="">{t('Alle typer')}</option>
          {CHANGE_TYPE_CODES.map((c) => <option key={c} value={c}>{changeTypeLabel(c)}</option>)}
        </select>
        <select aria-label={t('Miljø')} value={environment} onChange={(e) => setEnvironment(e.target.value)}>
          <option value="">{t('Alle miljøer')}</option>
          {ENVIRONMENT_CODES.map((c) => <option key={c} value={c}>{environmentLabel(c)}</option>)}
        </select>
        <label className="settings-check">
          <input type="checkbox" checked={mine} onChange={(e) => setMine(e.target.checked)} />
          {t('Bare mine')}
        </label>
      </div>

      {changes.error && <p role="alert" className="settings-error">{changes.error}</p>}
      {changes.loading ? <p>{t('Henter endringer...')}</p> : changes.items.length === 0 ? (
        <p>{t('Ingen endringer.')}</p>
      ) : (
        <div className="audit-table-wrapper">
          <table className="audit-table">
            <thead>
              <tr>
                <th>{t('Nummer')}</th>
                <th>{t('Tittel')}</th>
                <th>{t('Type')}</th>
                <th>{t('Miljø')}</th>
                <th>{t('Status')}</th>
                <th>{t('Risiko')}</th>
                <th>{t('Planlagt')}</th>
              </tr>
            </thead>
            <tbody>
              {changes.items.map((c) => (
                <tr key={c.id} className="gov-row" onClick={() => onOpen(c.id)}>
                  <td className="audit-nowrap">
                    <button type="button" className="gov-link" onClick={() => onOpen(c.id)}>{c.number}</button>
                  </td>
                  <td>{c.title}</td>
                  <td><Badge kind={c.type}>{changeTypeLabel(c.type)}</Badge></td>
                  <td><Badge kind={c.environment}>{environmentLabel(c.environment)}</Badge></td>
                  <td>{changeStatusLabel(c.status)}</td>
                  <td>{c.riskScore ?? '–'}</td>
                  <td className="audit-nowrap">{c.plannedStart ? formatDateTime(c.plannedStart) : '–'}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  )
}

// Ukekalender over planlagte endringer (planlagt start i uken)
function ChangeCalendar({ onOpen }: { onOpen: (id: number) => void }) {
  const [weekStart, setWeekStart] = useState(() => startOfWeek(new Date()))
  const changes = useList<ChangeSummary>('/api/changes')

  const days = Array.from({ length: 7 }, (_, i) => {
    const d = new Date(weekStart)
    d.setDate(d.getDate() + i)
    return d
  })

  const shift = (weeks: number) => {
    const d = new Date(weekStart)
    d.setDate(d.getDate() + 7 * weeks)
    setWeekStart(d)
  }

  const sameDay = (a: Date, b: Date) => a.toDateString() === b.toDateString()
  const active = changes.items.filter((c) => c.plannedStart && !['cancelled', 'rejected'].includes(c.status))

  return (
    <div className="helpdesk-card">
      <h3>{t('Endringskalender')}</h3>
      <div className="asset-filters">
        <button type="button" className="settings-row-action" onClick={() => shift(-1)}>{t('Forrige uke')}</button>
        <button type="button" className="settings-row-action" onClick={() => setWeekStart(startOfWeek(new Date()))}>{t('Denne uken')}</button>
        <button type="button" className="settings-row-action" onClick={() => shift(1)}>{t('Neste uke')}</button>
      </div>
      <div className="gov-calendar">
        {days.map((day) => (
          <div key={day.toISOString()} className="gov-calendar-day">
            <strong>{day.toLocaleDateString(locale(), { weekday: 'short', day: 'numeric', month: 'short' })}</strong>
            {active.filter((c) => sameDay(new Date(c.plannedStart!), day)).map((c) => (
              <button key={c.id} type="button"
                className={`gov-calendar-item${c.environment === 'production' ? ' gov-production-item' : ''}`}
                onClick={() => onOpen(c.id)}>
                {new Date(c.plannedStart!).toLocaleTimeString(locale(), { hour: '2-digit', minute: '2-digit' })} {c.number}
                <span>{c.title}</span>
              </button>
            ))}
          </div>
        ))}
      </div>
    </div>
  )
}

type FormState = {
  title: string; description: string; justification: string; expectedBenefit: string
  type: string; priority: string; environment: string
  likelihood: string; impact: string
  riskAnalysis: string; impactAnalysis: string; securityImpact: string; gdprImpact: string
  costChangePerMonth: string; costChangeNote: string; backupStatus: string; backupNote: string
  implementationPlan: string; testPlan: string; testResults: string; rollbackPlan: string
  plannedStart: string; plannedEnd: string; implementerEmployeeId: string
  serviceIds: number[]; azureScopes: { azureSubscriptionId: number; resourceGroup: string }[]
  templateId: string
}

function toForm(d?: ChangeDetails): FormState {
  const c = d?.change
  const s = (v: string | number | null | undefined) => (v == null ? '' : String(v))
  return {
    title: s(c?.title), description: s(c?.description), justification: s(c?.justification),
    expectedBenefit: s(c?.expectedBenefit), type: c?.type ?? 'normal', priority: c?.priority ?? 'normal',
    environment: c?.environment ?? 'development', likelihood: s(c?.likelihood), impact: s(c?.impact),
    riskAnalysis: s(c?.riskAnalysis), impactAnalysis: s(c?.impactAnalysis), securityImpact: s(c?.securityImpact),
    gdprImpact: s(c?.gdprImpact), costChangePerMonth: s(c?.costChangePerMonth), costChangeNote: s(c?.costChangeNote),
    backupStatus: c?.backupStatus ?? 'unverified', backupNote: s(c?.backupNote),
    implementationPlan: s(c?.implementationPlan), testPlan: s(c?.testPlan), testResults: s(c?.testResults),
    rollbackPlan: s(c?.rollbackPlan), plannedStart: toLocalInput(c?.plannedStart ?? null),
    plannedEnd: toLocalInput(c?.plannedEnd ?? null), implementerEmployeeId: s(c?.implementerEmployeeId),
    serviceIds: d?.serviceIds ?? [],
    azureScopes: (d?.azureScopes ?? []).map((a) => ({ azureSubscriptionId: a.azureSubscriptionId, resourceGroup: a.resourceGroup ?? '' })),
    templateId: s(c?.templateId),
  }
}

function toBody(f: FormState) {
  const num = (v: string) => (v === '' ? null : Number(v))
  const str = (v: string) => (v.trim() === '' ? null : v)
  return {
    title: f.title, description: f.description, justification: str(f.justification),
    expectedBenefit: str(f.expectedBenefit), type: f.type, priority: f.priority, environment: f.environment,
    likelihood: num(f.likelihood), impact: num(f.impact), riskAnalysis: str(f.riskAnalysis),
    impactAnalysis: str(f.impactAnalysis), securityImpact: str(f.securityImpact), gdprImpact: str(f.gdprImpact),
    costChangePerMonth: num(f.costChangePerMonth), costChangeNote: str(f.costChangeNote),
    backupStatus: f.backupStatus, backupNote: str(f.backupNote),
    implementationPlan: f.type === 'standard' ? null : str(f.implementationPlan),
    testPlan: f.type === 'standard' ? null : str(f.testPlan),
    testResults: str(f.testResults),
    rollbackPlan: f.type === 'standard' ? null : str(f.rollbackPlan),
    plannedStart: fromLocalInput(f.plannedStart), plannedEnd: fromLocalInput(f.plannedEnd),
    implementerEmployeeId: num(f.implementerEmployeeId),
    serviceIds: f.serviceIds,
    azureScopes: f.azureScopes.map((a) => ({ azureSubscriptionId: a.azureSubscriptionId, resourceGroup: str(a.resourceGroup) })),
    templateId: f.type === 'standard' ? num(f.templateId) : null,
  }
}

// Felt som er med i godkjent innhold (samme som ChangeScopeHash i backend)
const SCOPE_FIELDS: (keyof FormState)[] = [
  'environment', 'description', 'likelihood', 'impact', 'riskAnalysis', 'impactAnalysis',
  'securityImpact', 'gdprImpact', 'implementationPlan', 'rollbackPlan', 'serviceIds', 'azureScopes',
]

function TextField({ id, label, value, onChange, rows = 3, disabled }: {
  id: string; label: string; value: string; onChange: (v: string) => void; rows?: number; disabled?: boolean
}) {
  return (
    <div>
      <label htmlFor={id}>{label}</label>
      <textarea id={id} rows={rows} value={value} disabled={disabled} onChange={(e) => onChange(e.target.value)} />
    </div>
  )
}

function ChangeForm({ options, existing, onSaved, onCancel }: {
  options: ChangeOptions | null
  existing?: ChangeDetails
  onSaved: (d: ChangeDetails) => void
  onCancel?: () => void
}) {
  const send = useSend()
  const [f, setF] = useState<FormState>(() => toForm(existing))
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const set = <K extends keyof FormState>(key: K, value: FormState[K]) => setF((p) => ({ ...p, [key]: value }))

  const isStandard = f.type === 'standard'
  const template = options?.templates.find((tpl) => String(tpl.id) === f.templateId)

  const approvedStatuses = ['pending_approval', 'approved', 'scheduled']
  const initial = useMemo(() => toForm(existing), [existing])
  const scopeChanged = existing != null && approvedStatuses.includes(existing.change.status) &&
    SCOPE_FIELDS.some((k) => JSON.stringify(f[k]) !== JSON.stringify(initial[k]))

  function chooseTemplate(id: string) {
    const tpl = options?.templates.find((x) => String(x.id) === id)
    setF((p) => ({
      ...p,
      templateId: id,
      implementationPlan: tpl?.implementationPlan ?? '',
      testPlan: tpl?.testPlan ?? '',
      rollbackPlan: tpl?.rollbackPlan ?? '',
      environment: tpl && !tpl.allowedEnvironments.split(',').includes(p.environment)
        ? tpl.allowedEnvironments.split(',')[0] : p.environment,
    }))
  }

  async function save() {
    if (scopeChanged && !window.confirm(t('Du endrer godkjent innhold. Lagring sender endringen tilbake til godkjenning. Fortsette?')))
      return
    setBusy(true)
    setError('')
    try {
      const response = existing
        ? await send('PUT', `/api/changes/${existing.change.id}`, toBody(f))
        : await send('POST', '/api/changes', toBody(f))
      onSaved(await response.json())
    } catch (e) {
      setError(errorText(e))
    } finally {
      setBusy(false)
    }
  }

  const environments = isStandard && template ? template.allowedEnvironments.split(',') : ENVIRONMENT_CODES

  return (
    <div className="helpdesk-card">
      <h3>{existing ? t('Endre {number}', { number: existing.change.number }) : t('Ny endring')}</h3>
      <form className="helpdesk-form settings-form" onSubmit={(e) => { e.preventDefault(); void save() }}>
        <h4>{t('Beskrivelse')}</h4>
        <div className="settings-form-grid">
          <div>
            <label htmlFor="chg-type">{t('Type')}</label>
            <select id="chg-type" value={f.type} disabled={existing != null} onChange={(e) => set('type', e.target.value)}>
              {CHANGE_TYPE_CODES.map((c) => <option key={c} value={c}>{changeTypeLabel(c)}</option>)}
            </select>
          </div>
          {isStandard && (
            <div>
              <label htmlFor="chg-template">{t('Mal')}</label>
              <select id="chg-template" value={f.templateId} disabled={existing != null}
                onChange={(e) => chooseTemplate(e.target.value)} required>
                <option value="">{t('Velg forhåndsgodkjent mal')}</option>
                {options?.templates.map((tpl) => <option key={tpl.id} value={tpl.id}>{tpl.name}</option>)}
              </select>
            </div>
          )}
          <div>
            <label htmlFor="chg-title">{t('Tittel')}</label>
            <input id="chg-title" value={f.title} maxLength={200} required onChange={(e) => set('title', e.target.value)} />
          </div>
          <div>
            <label htmlFor="chg-priority">{t('Prioritet')}</label>
            <select id="chg-priority" value={f.priority} onChange={(e) => set('priority', e.target.value)}>
              {PRIORITIES.map((p) => <option key={p} value={p}>{priorityLabel(p)}</option>)}
            </select>
          </div>
          <div>
            <label htmlFor="chg-env">{t('Miljø')}</label>
            <select id="chg-env" value={f.environment} onChange={(e) => set('environment', e.target.value)}>
              {environments.map((c) => <option key={c} value={c}>{environmentLabel(c)}</option>)}
            </select>
          </div>
        </div>
        <TextField id="chg-desc" label={t('Beskrivelse')} value={f.description} onChange={(v) => set('description', v)} />
        <TextField id="chg-just" label={t('Begrunnelse')} value={f.justification} onChange={(v) => set('justification', v)} rows={2} />
        <TextField id="chg-benefit" label={t('Forventet gevinst')} value={f.expectedBenefit} onChange={(v) => set('expectedBenefit', v)} rows={2} />

        <h4>{t('Omfang')}</h4>
        <fieldset className="gov-checklist">
          <legend>{t('Berørte systemer og tjenester')}</legend>
          {options?.services.map((s) => (
            <label key={s.id} className="settings-check">
              <input type="checkbox" checked={f.serviceIds.includes(s.id)}
                onChange={(e) => set('serviceIds', e.target.checked ? [...f.serviceIds, s.id] : f.serviceIds.filter((x) => x !== s.id))} />
              {s.name}
            </label>
          ))}
        </fieldset>
        <fieldset className="gov-checklist">
          <legend>{t('Berørte Azure-abonnementer')}</legend>
          {options?.subscriptions.length === 0 && <p className="settings-muted">{t('Ingen abonnementer er synket.')}</p>}
          {options?.subscriptions.map((s) => {
            const scope = f.azureScopes.find((a) => a.azureSubscriptionId === s.id)
            return (
              <div key={s.id} className="gov-scope-row">
                <label className="settings-check">
                  <input type="checkbox" checked={scope != null}
                    onChange={(e) => set('azureScopes', e.target.checked
                      ? [...f.azureScopes, { azureSubscriptionId: s.id, resourceGroup: '' }]
                      : f.azureScopes.filter((a) => a.azureSubscriptionId !== s.id))} />
                  {s.name}
                </label>
                {scope && (
                  <input aria-label={t('Ressursgruppe (valgfritt)')} placeholder={t('Ressursgruppe (valgfritt)')}
                    value={scope.resourceGroup} maxLength={90}
                    onChange={(e) => set('azureScopes', f.azureScopes.map((a) =>
                      a.azureSubscriptionId === s.id ? { ...a, resourceGroup: e.target.value } : a))} />
                )}
              </div>
            )
          })}
        </fieldset>

        <h4>{t('Risiko og konsekvens')}</h4>
        <div className="settings-form-grid">
          <div>
            <label htmlFor="chg-l">{t('Sannsynlighet (1–5)')}</label>
            <select id="chg-l" value={f.likelihood} onChange={(e) => set('likelihood', e.target.value)}>
              <option value="">–</option>{[1, 2, 3, 4, 5].map((n) => <option key={n} value={n}>{n}</option>)}
            </select>
          </div>
          <div>
            <label htmlFor="chg-i">{t('Konsekvens (1–5)')}</label>
            <select id="chg-i" value={f.impact} onChange={(e) => set('impact', e.target.value)}>
              <option value="">–</option>{[1, 2, 3, 4, 5].map((n) => <option key={n} value={n}>{n}</option>)}
            </select>
          </div>
          {isStandard && template && <p className="settings-muted">{t('Malen tillater risikoscore opptil {max}.', { max: template.maxRiskScore })}</p>}
        </div>
        <TextField id="chg-risk" label={t('Risikoanalyse')} value={f.riskAnalysis} onChange={(v) => set('riskAnalysis', v)} />
        <TextField id="chg-impact" label={t('Konsekvensanalyse')} value={f.impactAnalysis} onChange={(v) => set('impactAnalysis', v)} />
        <TextField id="chg-sec" label={t('Konsekvenser for informasjonssikkerhet')} value={f.securityImpact} onChange={(v) => set('securityImpact', v)} rows={2} />
        <TextField id="chg-gdpr" label={t('Konsekvenser for GDPR')} value={f.gdprImpact} onChange={(v) => set('gdprImpact', v)} rows={2} />

        <h4>{t('Kostnad og backup')}</h4>
        <div className="settings-form-grid">
          <div>
            <label htmlFor="chg-cost">{t('Kostnadsendring per måned (NOK)')}</label>
            <input id="chg-cost" type="number" step="0.01" value={f.costChangePerMonth} onChange={(e) => set('costChangePerMonth', e.target.value)} />
          </div>
          <div>
            <label htmlFor="chg-backup">{t('Backup og gjenoppretting')}</label>
            <select id="chg-backup" value={f.backupStatus} onChange={(e) => set('backupStatus', e.target.value)}>
              {BACKUP_STATUS_CODES.map((c) => <option key={c} value={c}>{backupStatusLabel(c)}</option>)}
            </select>
          </div>
        </div>
        <TextField id="chg-costnote" label={t('Kostnad, forklaring')} value={f.costChangeNote} onChange={(v) => set('costChangeNote', v)} rows={1} />
        <TextField id="chg-backupnote" label={t('Backup, merknad')} value={f.backupNote} onChange={(v) => set('backupNote', v)} rows={1} />

        <h4>{t('Planer')}</h4>
        {isStandard && <p className="settings-muted">{t('Planene kommer fra malen og kan ikke endres.')}</p>}
        <TextField id="chg-impl" label={t('Implementeringsplan')} value={f.implementationPlan} disabled={isStandard} onChange={(v) => set('implementationPlan', v)} />
        <TextField id="chg-test" label={t('Testplan')} value={f.testPlan} disabled={isStandard} onChange={(v) => set('testPlan', v)} />
        <TextField id="chg-results" label={t('Testresultater')} value={f.testResults} onChange={(v) => set('testResults', v)} rows={2} />
        <TextField id="chg-rollback" label={t('Rollback-plan')} value={f.rollbackPlan} disabled={isStandard} onChange={(v) => set('rollbackPlan', v)} />

        <h4>{t('Tidspunkt og ansvar')}</h4>
        <div className="settings-form-grid">
          <div>
            <label htmlFor="chg-start">{t('Planlagt start')}</label>
            <input id="chg-start" type="datetime-local" value={f.plannedStart} onChange={(e) => set('plannedStart', e.target.value)} />
          </div>
          <div>
            <label htmlFor="chg-end">{t('Planlagt slutt')}</label>
            <input id="chg-end" type="datetime-local" value={f.plannedEnd} onChange={(e) => set('plannedEnd', e.target.value)} />
          </div>
          <div>
            <label htmlFor="chg-implementer">{t('Ansvarlig for gjennomføring')}</label>
            <select id="chg-implementer" value={f.implementerEmployeeId} onChange={(e) => set('implementerEmployeeId', e.target.value)}>
              <option value="">{t('Settes ved implementering')}</option>
              {options?.employees.map((e) => <option key={e.id} value={e.id}>{e.name}</option>)}
            </select>
          </div>
        </div>

        {scopeChanged && (
          <p className="settings-error">{t('Du endrer godkjent innhold. Lagring sender endringen tilbake til godkjenning.')}</p>
        )}
        {error && <p role="alert" className="settings-error">{error}</p>}
        <div className="settings-form-actions">
          <button type="submit" disabled={busy}>{existing ? t('Lagre') : t('Lagre utkast')}</button>
          {onCancel && <button type="button" className="secondary" onClick={onCancel}>{t('Avbryt')}</button>}
        </div>
      </form>
    </div>
  )
}

const TRANSITION_LABELS: Record<string, string> = {
  submitted: 'Send inn',
  risk_assessment: 'Start risikovurdering',
  technical_review: 'Til teknisk gjennomgang',
  testing: 'Til testing',
  pending_approval: 'Send til godkjenning',
  scheduled: 'Planlegg',
  implementation: 'Start implementering',
  verification: 'Til verifisering',
  failed: 'Marker som feilet',
  rolled_back: 'Marker som rullet tilbake',
  completed: 'Marker som gjennomført',
  closed: 'Lukk',
  cancelled: 'Avbryt endringen',
}

// Overganger som krever et notat (resultat eller begrunnelse)
const NOTE_REQUIRED = ['verification', 'failed', 'rolled_back']

function ChangeDetailsView({ id, options, onChanged }: { id: number; options: ChangeOptions | null; onChanged: () => void }) {
  const send = useSend()
  const [reloadKey, setReloadKey] = useState(0)
  const details = useResource<ChangeDetails>(`/api/changes/${id}`, reloadKey)
  const [editing, setEditing] = useState(false)
  const [showHistory, setShowHistory] = useState(() => new URLSearchParams(window.location.search).has('historikk'))
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [comment, setComment] = useState('')

  async function run(action: () => Promise<unknown>) {
    setBusy(true)
    setError('')
    try {
      await action()
      setComment('')
      setReloadKey((k) => k + 1)
      onChanged()
    } catch (e) {
      setError(errorText(e))
    } finally {
      setBusy(false)
    }
  }

  if (details.loading && !details.data) return <div className="helpdesk-card"><p>{t('Henter endringen...')}</p></div>
  if (!details.data) return <div className="helpdesk-card"><p className="settings-error">{details.error || t('Fant ikke endringen.')}</p></div>

  const d = details.data
  const c = d.change
  const name = (ids: number[], list?: { id: number; name: string }[]) =>
    ids.map((x) => list?.find((o) => o.id === x)?.name ?? `#${x}`).join(', ') || '–'

  if (editing) {
    return (
      <ChangeForm options={options} existing={d} onCancel={() => setEditing(false)}
        onSaved={() => { setEditing(false); setReloadKey((k) => k + 1); onChanged() }} />
    )
  }

  function transition(to: string) {
    let note: string | null = null
    if (NOTE_REQUIRED.includes(to)) {
      note = window.prompt(t('Beskriv resultatet av implementeringen:'))
      if (!note) return
    } else if (to === 'cancelled') {
      if (!window.confirm(t('Avbryte {number}?', { number: c.number }))) return
    }
    void run(() => send('POST', `/api/changes/${c.id}/transitions`, { to, note }))
  }

  function decide(role: string, decision: string) {
    void run(() => send('POST', `/api/changes/${c.id}/decisions`, {
      role, decision, comment: comment.trim() || null, expectedScopeHash: c.scopeHash,
    }))
  }

  return (
    <div className="helpdesk-card">
      <h3>{c.number} · {c.title}</h3>
      <p>
        <Badge kind={c.type}>{changeTypeLabel(c.type)}</Badge>{' '}
        <Badge kind={c.environment}>{environmentLabel(c.environment)}</Badge>{' '}
        <strong>{changeStatusLabel(c.status)}</strong>
        {d.waitingForInfo && <span className="settings-error"> · {t('Venter på mer informasjon')}</span>}
      </p>

      {error && <p role="alert" className="settings-error">{error}</p>}

      <div className="gov-actions">
        {d.allowedTransitions.map((to) => (
          <button key={to} type="button" className={`settings-row-action${to === 'cancelled' ? ' m365-danger' : ''}`}
            disabled={busy} onClick={() => transition(to)}>
            {t(TRANSITION_LABELS[to] ?? to)}
          </button>
        ))}
        {['draft', 'submitted', 'risk_assessment', 'technical_review', 'testing', 'pending_approval', 'approved', 'scheduled'].includes(c.status) && (
          <button type="button" className="settings-row-action" onClick={() => setEditing(true)}>{t('Endre')}</button>
        )}
        <button type="button" className="settings-row-action" onClick={() => setShowHistory((s) => !s)}>
          {showHistory ? t('Skjul historikk') : t('Vis historikk')}
        </button>
      </div>

      {d.canDecideAs.length > 0 && (
        <div className="gov-decision">
          <h4>{t('Godkjenning')}</h4>
          <label htmlFor="chg-comment">{t('Kommentar (påkrevd ved avvisning og spørsmål)')}</label>
          <textarea id="chg-comment" rows={2} value={comment} onChange={(e) => setComment(e.target.value)} />
          {d.canDecideAs.map((role) => (
            <div key={role} className="gov-actions">
              <span>{t('Som {role}:', { role: roleLabel(role) })}</span>
              <button type="button" className="settings-row-action" disabled={busy} onClick={() => decide(role, 'approved')}>{t('Godkjenn')}</button>
              <button type="button" className="settings-row-action" disabled={busy} onClick={() => decide(role, 'info_requested')}>{t('Be om mer informasjon')}</button>
              <button type="button" className="settings-row-action m365-danger" disabled={busy} onClick={() => decide(role, 'rejected')}>{t('Avvis')}</button>
            </div>
          ))}
        </div>
      )}

      {d.canPostReview && (
        <div className="gov-decision">
          <h4>{t('Etterkontroll')}</h4>
          <textarea aria-label={t('Funn fra etterkontrollen')} rows={3} value={comment} onChange={(e) => setComment(e.target.value)} />
          <button type="button" className="settings-row-action" disabled={busy || !comment.trim()}
            onClick={() => void run(() => send('POST', `/api/changes/${c.id}/post-review`, { notes: comment }))}>
            {t('Registrer etterkontroll')}
          </button>
        </div>
      )}

      {d.requirements.length > 0 && (
        <>
          <h4>{t('Påkrevde godkjenninger')}</h4>
          <ul>
            {d.requirements.map((r) => (
              <li key={r.role} className={r.satisfied ? 'settings-message' : undefined}>
                {r.satisfied ? '✓' : '○'} {roleLabel(r.role)} ({r.approvedBy.length}/{r.minApprovals})
                {r.approvedBy.length > 0 && ` · ${r.approvedBy.join(', ')}`}
              </li>
            ))}
          </ul>
        </>
      )}

      {d.approvals.length > 0 && (
        <>
          <h4>{t('Beslutninger')}</h4>
          <ul>
            {d.approvals.map((a) => (
              <li key={a.id} className={a.valid ? undefined : 'settings-muted'}>
                {formatDateTime(a.decidedAt)} · {a.approverName} ({roleLabel(a.role)}): <strong>{t(a.decision === 'approved' ? 'Godkjent' : a.decision === 'rejected' ? 'Avvist' : 'Ba om informasjon')}</strong>
                {a.comment && ` – ${a.comment}`}
                {!a.valid && ` (${t('gjelder ikke lenger: innholdet er endret')})`}
              </li>
            ))}
          </ul>
        </>
      )}

      <dl className="gov-fields">
        <dt>{t('Innmeldt av')}</dt><dd>{c.requestedByName} · {formatDateTime(c.createdAt)}</dd>
        <dt>{t('Prioritet')}</dt><dd>{priorityLabel(c.priority)}</dd>
        <dt>{t('Systemer og tjenester')}</dt><dd>{name(d.serviceIds, options?.services)}</dd>
        <dt>{t('Azure')}</dt><dd>{d.azureScopes.map((a) =>
          `${options?.subscriptions.find((s) => s.id === a.azureSubscriptionId)?.name ?? `#${a.azureSubscriptionId}`}${a.resourceGroup ? ` / ${a.resourceGroup}` : ''}`).join(', ') || '–'}</dd>
        <dt>{t('Planlagt')}</dt><dd>{c.plannedStart ? `${formatDateTime(c.plannedStart)} – ${c.plannedEnd ? formatDateTime(c.plannedEnd) : '?'}` : '–'}</dd>
        <dt>{t('Ansvarlig for gjennomføring')}</dt><dd>{name(c.implementerEmployeeId ? [c.implementerEmployeeId] : [], options?.employees)}</dd>
        <dt>{t('Risiko')}</dt><dd>{c.likelihood && c.impact ? `${c.likelihood} × ${c.impact} = ${c.likelihood * c.impact}` : '–'}</dd>
        <dt>{t('Backup')}</dt><dd>{backupStatusLabel(c.backupStatus)}{c.backupNote && ` · ${c.backupNote}`}</dd>
        <dt>{t('Kostnadsendring')}</dt><dd>{c.costChangePerMonth != null ? `${c.costChangePerMonth} NOK/mnd` : '–'}{c.costChangeNote && ` · ${c.costChangeNote}`}</dd>
        <dt>{t('Koblede risikoer')}</dt><dd>{d.linkedRiskIds?.length ? d.linkedRiskIds.map((x) => `#${x}`).join(', ') : '–'}</dd>
      </dl>

      {([
        ['Beskrivelse', c.description], ['Begrunnelse', c.justification], ['Forventet gevinst', c.expectedBenefit],
        ['Risikoanalyse', c.riskAnalysis], ['Konsekvensanalyse', c.impactAnalysis],
        ['Konsekvenser for informasjonssikkerhet', c.securityImpact], ['Konsekvenser for GDPR', c.gdprImpact],
        ['Implementeringsplan', c.implementationPlan], ['Testplan', c.testPlan], ['Testresultater', c.testResults],
        ['Rollback-plan', c.rollbackPlan], ['Implementeringsresultat', c.implementationResult],
        ['Etterkontroll', c.postReviewNotes ? `${c.postReviewNotes} (${c.postReviewedByName})` : null],
      ] as [string, string | null][]).filter(([, v]) => v).map(([label, value]) => (
        <div key={label} className="gov-text">
          <h4>{t(label)}</h4>
          <p>{value}</p>
        </div>
      ))}

      <DeliveryPanel details={d} onChanged={() => { setReloadKey((k) => k + 1); onChanged() }} />

      {showHistory && <History url={`/api/changes/${c.id}/history`} />}
    </div>
  )
}

export function History({ url }: { url: string }) {
  const events = useList<AuditEntry>(url)
  return (
    <div>
      <h4>{t('Historikk')}</h4>
      {events.loading ? <p>{t('Henter historikk...')}</p> : (
        <ul className="ticket-history">
          {events.items.map((e) => (
            <li key={e.id}>
              <span className="ticket-history-meta">{formatDateTime(e.occurredAt)} · {e.actorName} · {e.entityType} · {e.action}</span>
              {e.changes && <div className="settings-muted gov-history-changes">{e.changes}</div>}
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}

type GateResult = { changeNumber: string; environment: string; allowed: boolean; reason: string; simulated: boolean }
type PlanResult = { plan: { text: string; hash: string; add: number; change: number; destroy: number } }

// Leveranse (C6): SIMULERT GitHub og Terraform. Ingen kall ut av appen, ingen
// infrastruktur endres, og produksjon avvises alltid i prototypen.
function DeliveryPanel({ details, onChanged }: { details: ChangeDetails; onChanged: () => void }) {
  const send = useSend()
  const getHeaders = useAuthHeaders()
  const c = details.change
  const [prUrl, setPrUrl] = useState(c.pullRequestUrl ?? '')
  const [sha, setSha] = useState(c.commitSha ?? '')
  const [plan, setPlan] = useState<PlanResult['plan'] | null>(null)
  const [gate, setGate] = useState<GateResult | null>(null)
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)

  async function run(action: () => Promise<void>) {
    setBusy(true)
    setError('')
    try {
      await action()
    } catch (e) {
      setError(errorText(e))
    } finally {
      setBusy(false)
    }
  }

  const link = () => run(async () => {
    await send('POST', `/api/changes/${c.id}/delivery/link`, { pullRequestUrl: prUrl.trim() || null, commitSha: sha.trim() || null })
    onChanged()
  })

  const createPlan = () => run(async () => {
    const response = await send('POST', `/api/changes/${c.id}/delivery/plan`)
    setPlan(((await response.json()) as PlanResult).plan)
    onChanged()
  })

  const checkGate = (environment: string) => run(async () => {
    const p = new URLSearchParams({ environment, commitSha: c.commitSha ?? '', planHash: c.terraformPlanHash ?? '' })
    const response = await fetch(apiUrl(`/api/changes/${c.number}/deployment-gate?${p}`), { headers: await getHeaders() })
    if (!response.ok) throw new Error(t('Sjekken feilet.'))
    setGate(await response.json())
  })

  const simulateApply = () => run(async () => {
    const response = await send('POST', `/api/changes/${c.id}/delivery/simulate-apply`)
    setGate(await response.json())
    onChanged()
  })

  return (
    <div className="gov-decision">
      <h4>{t('Leveranse')} <span className="gov-badge">{t('Simulert')}</span></h4>
      <p className="settings-muted">
        {t('Simulert GitHub og Terraform. Ingen kall ut av appen og ingen infrastruktur endres. Produksjon avvises alltid i prototypen. Ny commit eller ny plan gjør godkjenningen ugyldig.')}
      </p>
      <label htmlFor="dl-pr">{t('Pull request (https://github.com/…)')}</label>
      <input id="dl-pr" value={prUrl} onChange={(e) => setPrUrl(e.target.value)} />
      <label htmlFor="dl-sha">{t('Commit-SHA')}</label>
      <input id="dl-sha" value={sha} onChange={(e) => setSha(e.target.value)} />
      <div className="gov-actions">
        <button type="button" className="settings-row-action" disabled={busy} onClick={() => void link()}>{t('Koble PR og commit')}</button>
        <button type="button" className="settings-row-action" disabled={busy} onClick={() => void createPlan()}>{t('Lag simulert Terraform-plan')}</button>
      </div>
      <p className="settings-muted">
        {t('Koblet commit')}: {c.commitSha ?? '–'} · {t('Plan-hash')}: {c.terraformPlanHash ? `${c.terraformPlanHash.slice(0, 12)}…` : '–'}
      </p>
      {plan && (
        <>
          <p className={plan.destroy > 0 ? 'settings-error' : undefined}>
            {t('Plan: {add} legges til, {change} endres, {destroy} slettes.', { add: plan.add, change: plan.change, destroy: plan.destroy })}
          </p>
          <pre className="gov-history-changes">{plan.text}</pre>
        </>
      )}
      <div className="gov-actions">
        {ENVIRONMENT_CODES.map((env) => (
          <button key={env} type="button" className="settings-row-action" disabled={busy} onClick={() => void checkGate(env)}>
            {t('Sjekk {env}', { env: environmentLabel(env) })}
          </button>
        ))}
        {c.status === 'implementation' && c.environment !== 'production' && (
          <button type="button" className="settings-row-action" disabled={busy} onClick={() => void simulateApply()}>
            {t('Simulert Apply')}
          </button>
        )}
      </div>
      {gate && (
        <p className={gate.allowed ? 'settings-message' : 'settings-error'}>
          {environmentLabel(gate.environment)}: {gate.allowed ? t('Tillatt') : t('Ikke tillatt')} – {gate.reason}
        </p>
      )}
      {error && <p role="alert" className="settings-error">{error}</p>}
    </div>
  )
}

import { useState } from 'react'
import {
  CHANGE_TYPE_CODES,
  ENVIRONMENT_CODES,
  GOVERNANCE_ACTIVITIES,
  GOVERNANCE_ROLES,
  activityLabel,
  changeTypeLabel,
  environmentLabel,
  roleLabel,
  type ChangeOptions,
  type ChangeTemplate,
} from '../../governance'
import { formatDateTime } from '../../audit'
import { errorText, useList, useResource, useSend } from './settingsApi'
import { t } from '../../i18n'

type Assignment = { id: number; role: string; serviceCatalogItemId: number | null; employeeId: number | null; entraGroupId: string | null }
type RaciEntry = { id: number; activity: string; role: string; raci: string }
type ApprovalRule = { id: number; changeType: string; environment: string; requiredRole: string; minApprovals: number; sortOrder: number }

function useAction(onDone: () => void) {
  const send = useSend()
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)
  const run = async (method: string, path: string, body?: unknown) => {
    setBusy(true)
    setError('')
    try {
      await send(method, path, body)
      onDone()
    } catch (e) {
      setError(errorText(e))
    } finally {
      setBusy(false)
    }
  }
  return { run, error, busy }
}

// Endringsroller: ansatt eller Entra-gruppe, globalt eller per tjeneste. Ingen kan tildele seg selv.
export function GovernanceRolesSettings() {
  const [reloadKey, setReloadKey] = useState(0)
  const assignments = useList<Assignment>('/api/governance/roles', reloadKey)
  const options = useResource<ChangeOptions>('/api/changes/options')
  const { run, error, busy } = useAction(() => setReloadKey((k) => k + 1))
  const [role, setRole] = useState('change_manager')
  const [holder, setHolder] = useState<'employee' | 'group'>('employee')
  const [employeeId, setEmployeeId] = useState('')
  const [groupId, setGroupId] = useState('')
  const [serviceId, setServiceId] = useState('')

  const employee = (id: number | null) => options.data?.employees.find((e) => e.id === id)?.name ?? `#${id}`
  const service = (id: number | null) => (id == null ? t('Alle') : options.data?.services.find((s) => s.id === id)?.name ?? `#${id}`)

  return (
    <div className="helpdesk-card">
      <h3>{t('Endringsroller')}</h3>
      <p className="audit-intro">{t('Hvem som har rollene i endrings- og risikoprosessen. Du kan ikke tildele roller til deg selv eller til en gruppe du er medlem av.')}</p>
      <form className="helpdesk-form settings-form" onSubmit={(e) => {
        e.preventDefault()
        void run('POST', '/api/governance/roles', {
          role,
          serviceCatalogItemId: serviceId ? Number(serviceId) : null,
          employeeId: holder === 'employee' && employeeId ? Number(employeeId) : null,
          entraGroupId: holder === 'group' ? groupId.trim() : null,
        })
      }}>
        <div className="settings-form-grid">
          <div>
            <label htmlFor="gr-role">{t('Rolle')}</label>
            <select id="gr-role" value={role} onChange={(e) => setRole(e.target.value)}>
              {Object.keys(GOVERNANCE_ROLES).map((r) => <option key={r} value={r}>{roleLabel(r)}</option>)}
            </select>
          </div>
          <div>
            <label htmlFor="gr-holder">{t('Tildel til')}</label>
            <select id="gr-holder" value={holder} onChange={(e) => setHolder(e.target.value as 'employee' | 'group')}>
              <option value="employee">{t('Ansatt')}</option>
              <option value="group">{t('Entra-gruppe')}</option>
            </select>
          </div>
          {holder === 'employee' ? (
            <div>
              <label htmlFor="gr-employee">{t('Ansatt')}</label>
              <select id="gr-employee" value={employeeId} required onChange={(e) => setEmployeeId(e.target.value)}>
                <option value="">{t('Velg ansatt')}</option>
                {options.data?.employees.map((e) => <option key={e.id} value={e.id}>{e.name}</option>)}
              </select>
            </div>
          ) : (
            <div>
              <label htmlFor="gr-group">{t('Gruppe-ID (objekt-ID i Entra)')}</label>
              <input id="gr-group" value={groupId} required onChange={(e) => setGroupId(e.target.value)} />
            </div>
          )}
          <div>
            <label htmlFor="gr-service">{t('Gjelder')}</label>
            <select id="gr-service" value={serviceId} onChange={(e) => setServiceId(e.target.value)}>
              <option value="">{t('Alle systemer og tjenester')}</option>
              {options.data?.services.map((s) => <option key={s.id} value={s.id}>{s.name}</option>)}
            </select>
          </div>
        </div>
        <div className="settings-form-actions"><button type="submit" disabled={busy}>{t('Tildel rolle')}</button></div>
      </form>
      {error && <p role="alert" className="settings-error">{error}</p>}

      <div className="audit-table-wrapper">
        <table className="audit-table">
          <thead><tr><th>{t('Rolle')}</th><th>{t('Tildelt')}</th><th>{t('Gjelder')}</th><th /></tr></thead>
          <tbody>
            {assignments.items.map((a) => (
              <tr key={a.id}>
                <td>{roleLabel(a.role)}</td>
                <td>{a.employeeId != null ? employee(a.employeeId) : `${t('Gruppe')} ${a.entraGroupId}`}</td>
                <td>{service(a.serviceCatalogItemId)}</td>
                <td>
                  <button type="button" className="settings-row-action" disabled={busy}
                    onClick={() => window.confirm(t('Fjerne rollen?')) && void run('DELETE', `/api/governance/roles/${a.id}`)}>
                    {t('Fjern')}
                  </button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  )
}

// RACI-matrise: aktivitet × rolle
export function RaciSettings() {
  const [reloadKey, setReloadKey] = useState(0)
  const entries = useList<RaciEntry>('/api/governance/raci', reloadKey)
  const { run, error, busy } = useAction(() => setReloadKey((k) => k + 1))
  const roles = Object.keys(GOVERNANCE_ROLES)

  return (
    <div className="helpdesk-card">
      <h3>{t('RACI-matrise')}</h3>
      <p className="audit-intro">{t('R = Responsible, A = Accountable (én per aktivitet), C = Consulted, I = Informed.')}</p>
      {error && <p role="alert" className="settings-error">{error}</p>}
      <div className="audit-table-wrapper">
        <table className="audit-table gov-raci">
          <thead>
            <tr><th>{t('Aktivitet')}</th>{roles.map((r) => <th key={r}>{roleLabel(r)}</th>)}</tr>
          </thead>
          <tbody>
            {Object.keys(GOVERNANCE_ACTIVITIES).map((activity) => (
              <tr key={activity}>
                <td>{activityLabel(activity)}</td>
                {roles.map((role) => {
                  const value = entries.items.find((e) => e.activity === activity && e.role === role)?.raci ?? ''
                  return (
                    <td key={role}>
                      <select aria-label={`${activityLabel(activity)} – ${roleLabel(role)}`} value={value} disabled={busy}
                        onChange={(e) => void run('PUT', '/api/governance/raci', { activity, role, raci: e.target.value || null })}>
                        <option value="">–</option>
                        {['R', 'A', 'C', 'I'].map((l) => <option key={l} value={l}>{l}</option>)}
                      </select>
                    </td>
                  )
                })}
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  )
}

// Godkjenningsregler per type og miljø
export function ApprovalRulesSettings() {
  const [reloadKey, setReloadKey] = useState(0)
  const rules = useList<ApprovalRule>('/api/governance/approval-rules', reloadKey)
  const { run, error, busy } = useAction(() => setReloadKey((k) => k + 1))
  const [draft, setDraft] = useState({ changeType: 'normal', environment: 'production', requiredRole: 'change_manager', minApprovals: 1, sortOrder: 10 })

  return (
    <div className="helpdesk-card">
      <h3>{t('Godkjenningsregler')}</h3>
      <p className="audit-intro">{t('Hvilke roller som må godkjenne normal- og hasteendringer. Standardendringer er godkjent gjennom malen. Produksjon krever alltid minst Change Manager.')}</p>
      <form className="helpdesk-form settings-form" onSubmit={(e) => { e.preventDefault(); void run('POST', '/api/governance/approval-rules', draft) }}>
        <div className="settings-form-grid">
          <div>
            <label htmlFor="ar-type">{t('Type')}</label>
            <select id="ar-type" value={draft.changeType} onChange={(e) => setDraft({ ...draft, changeType: e.target.value })}>
              {CHANGE_TYPE_CODES.filter((c) => c !== 'standard').map((c) => <option key={c} value={c}>{changeTypeLabel(c)}</option>)}
            </select>
          </div>
          <div>
            <label htmlFor="ar-env">{t('Miljø')}</label>
            <select id="ar-env" value={draft.environment} onChange={(e) => setDraft({ ...draft, environment: e.target.value })}>
              <option value="any">{t('Alle miljøer')}</option>
              {ENVIRONMENT_CODES.map((c) => <option key={c} value={c}>{environmentLabel(c)}</option>)}
            </select>
          </div>
          <div>
            <label htmlFor="ar-role">{t('Rolle')}</label>
            <select id="ar-role" value={draft.requiredRole} onChange={(e) => setDraft({ ...draft, requiredRole: e.target.value })}>
              {Object.keys(GOVERNANCE_ROLES).map((r) => <option key={r} value={r}>{roleLabel(r)}</option>)}
            </select>
          </div>
          <div>
            <label htmlFor="ar-min">{t('Antall godkjenninger')}</label>
            <input id="ar-min" type="number" min={1} max={10} value={draft.minApprovals}
              onChange={(e) => setDraft({ ...draft, minApprovals: Number(e.target.value) })} />
          </div>
        </div>
        <div className="settings-form-actions"><button type="submit" disabled={busy}>{t('Legg til regel')}</button></div>
      </form>
      {error && <p role="alert" className="settings-error">{error}</p>}
      <div className="audit-table-wrapper">
        <table className="audit-table">
          <thead><tr><th>{t('Type')}</th><th>{t('Miljø')}</th><th>{t('Rolle')}</th><th>{t('Antall')}</th><th /></tr></thead>
          <tbody>
            {rules.items.map((r) => (
              <tr key={r.id}>
                <td>{changeTypeLabel(r.changeType)}</td>
                <td>{r.environment === 'any' ? t('Alle miljøer') : environmentLabel(r.environment)}</td>
                <td>{roleLabel(r.requiredRole)}</td>
                <td>{r.minApprovals}</td>
                <td>
                  <button type="button" className="settings-row-action" disabled={busy}
                    onClick={() => window.confirm(t('Slette regelen?')) && void run('DELETE', `/api/governance/approval-rules/${r.id}`)}>
                    {t('Slett')}
                  </button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  )
}

const EMPTY_TEMPLATE = {
  name: '', description: '', implementationPlan: '', testPlan: '', rollbackPlan: '',
  allowedEnvironments: ['development'], maxRiskScore: 6, nextReviewAt: '', isActive: true,
}

// Maler for standardendringer. Endret innhold fjerner forhåndsgodkjenningen.
export function ChangeTemplatesSettings() {
  const [reloadKey, setReloadKey] = useState(0)
  const templates = useList<ChangeTemplate>('/api/changetemplates', reloadKey)
  const [draft, setDraft] = useState(EMPTY_TEMPLATE)
  const [editing, setEditing] = useState<number | null>(null)
  const { run, error, busy } = useAction(() => { setReloadKey((k) => k + 1); setEditing(null); setDraft(EMPTY_TEMPLATE) })

  const body = () => ({ ...draft, nextReviewAt: draft.nextReviewAt ? new Date(`${draft.nextReviewAt}T12:00:00`).toISOString() : null })

  return (
    <div className="helpdesk-card">
      <h3>{t('Endringsmaler')}</h3>
      <p className="audit-intro">{t('Forhåndsgodkjente maler for standardendringer. En Change Manager godkjenner malen; endret innhold krever ny godkjenning.')}</p>
      <form className="helpdesk-form settings-form" onSubmit={(e) => {
        e.preventDefault()
        void run(editing ? 'PUT' : 'POST', editing ? `/api/changetemplates/${editing}` : '/api/changetemplates', body())
      }}>
        <div className="settings-form-grid">
          <div>
            <label htmlFor="ct-name">{t('Navn')}</label>
            <input id="ct-name" required maxLength={200} value={draft.name} onChange={(e) => setDraft({ ...draft, name: e.target.value })} />
          </div>
          <div>
            <label htmlFor="ct-max">{t('Maks risikoscore (1–25)')}</label>
            <input id="ct-max" type="number" min={1} max={25} value={draft.maxRiskScore} onChange={(e) => setDraft({ ...draft, maxRiskScore: Number(e.target.value) })} />
          </div>
          <div>
            <label htmlFor="ct-review">{t('Neste gjennomgang')}</label>
            <input id="ct-review" type="date" value={draft.nextReviewAt} onChange={(e) => setDraft({ ...draft, nextReviewAt: e.target.value })} />
          </div>
        </div>
        <fieldset className="gov-checklist">
          <legend>{t('Tillatte miljøer')}</legend>
          {ENVIRONMENT_CODES.map((env) => (
            <label key={env} className="settings-check">
              <input type="checkbox" checked={draft.allowedEnvironments.includes(env)}
                onChange={(e) => setDraft({ ...draft, allowedEnvironments: e.target.checked
                  ? [...draft.allowedEnvironments, env] : draft.allowedEnvironments.filter((x) => x !== env) })} />
              {environmentLabel(env)}
            </label>
          ))}
        </fieldset>
        {(['implementationPlan', 'testPlan', 'rollbackPlan'] as const).map((k) => (
          <div key={k}>
            <label htmlFor={`ct-${k}`}>{t(k === 'implementationPlan' ? 'Implementeringsplan' : k === 'testPlan' ? 'Testplan' : 'Rollback-plan')}</label>
            <textarea id={`ct-${k}`} rows={2} required={k !== 'testPlan'} value={draft[k]} onChange={(e) => setDraft({ ...draft, [k]: e.target.value })} />
          </div>
        ))}
        <div className="settings-form-actions">
          <button type="submit" disabled={busy}>{editing ? t('Lagre') : t('Legg til mal')}</button>
          {editing && <button type="button" className="secondary" onClick={() => { setEditing(null); setDraft(EMPTY_TEMPLATE) }}>{t('Avbryt')}</button>}
        </div>
      </form>
      {error && <p role="alert" className="settings-error">{error}</p>}
      <div className="audit-table-wrapper">
        <table className="audit-table">
          <thead><tr><th>{t('Mal')}</th><th>{t('Miljøer')}</th><th>{t('Maks score')}</th><th>{t('Forhåndsgodkjent')}</th><th /></tr></thead>
          <tbody>
            {templates.items.map((tpl) => (
              <tr key={tpl.id} className={tpl.isActive ? undefined : 'settings-muted'}>
                <td>{tpl.name}</td>
                <td>{tpl.allowedEnvironments.split(',').map(environmentLabel).join(', ')}</td>
                <td>{tpl.maxRiskScore}</td>
                <td>{tpl.approvedAt ? `${tpl.approvedByName} · ${formatDateTime(tpl.approvedAt)}` : <span className="settings-error">{t('Nei')}</span>}</td>
                <td className="audit-nowrap">
                  <button type="button" className="settings-row-action" onClick={() => {
                    setEditing(tpl.id)
                    setDraft({
                      name: tpl.name, description: tpl.description ?? '', implementationPlan: tpl.implementationPlan ?? '',
                      testPlan: tpl.testPlan ?? '', rollbackPlan: tpl.rollbackPlan ?? '',
                      allowedEnvironments: tpl.allowedEnvironments.split(','), maxRiskScore: tpl.maxRiskScore,
                      nextReviewAt: tpl.nextReviewAt?.slice(0, 10) ?? '', isActive: tpl.isActive,
                    })
                  }}>{t('Endre')}</button>
                  {!tpl.approvedAt && (
                    <button type="button" className="settings-row-action" disabled={busy}
                      onClick={() => void run('POST', `/api/changetemplates/${tpl.id}/approve`)}>
                      {t('Forhåndsgodkjenn (Change Manager)')}
                    </button>
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  )
}

type GovernanceSettingsDto = { teamsNotificationsEnabled: boolean; reminderDaysBefore: number; retentionMonths: number | null }

// Teams-kort for endringer og risiko (C5), under fanen Teams
export function GovernanceNotificationSettings() {
  const [reloadKey, setReloadKey] = useState(0)
  const settings = useResource<GovernanceSettingsDto>('/api/governance/settings', reloadKey)
  const { run, error, busy } = useAction(() => setReloadKey((k) => k + 1))
  const data = settings.data

  if (!data) return null

  return (
    <div className="helpdesk-card">
      <h3>{t('Endringer og risiko i Teams')}</h3>
      <p className="audit-intro">
        {t('Kort om endringer som venter på godkjenning, statusendringer, kommende produksjonsendringer, risikoer som ikke er behandlet og risikoaksept som utløper. Kortene viser bare nummer, tittel, status og tidspunkt, og lenker til appen.')}
      </p>
      <label className="settings-check">
        <input type="checkbox" checked={data.teamsNotificationsEnabled} disabled={busy}
          onChange={(e) => void run('PUT', '/api/governance/settings', { ...data, teamsNotificationsEnabled: e.target.checked })} />
        {t('Send Teams-kort for endringer og risiko')}
      </label>
      <div className="helpdesk-form settings-form">
        <label htmlFor="gov-reminder">{t('Påminnelse om produksjonsendringer (dager før)')}</label>
        <input id="gov-reminder" type="number" min={0} max={60} defaultValue={data.reminderDaysBefore} disabled={busy}
          onBlur={(e) => Number(e.target.value) !== data.reminderDaysBefore &&
            void run('PUT', '/api/governance/settings', { ...data, reminderDaysBefore: Number(e.target.value) })} />
      </div>
      {error && <p role="alert" className="settings-error">{error}</p>}
    </div>
  )
}

// Syntetiske demodata (C9). Vises bare når Demo:Enabled er på i utviklingstenanten.
export function DemoDataSettings() {
  const status = useResource<{ allowed: boolean }>('/api/governance/demo-data')
  const send = useSend()
  const [message, setMessage] = useState('')
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)

  if (!status.data?.allowed) return null

  async function run(method: 'POST' | 'DELETE') {
    if (method === 'DELETE' && !window.confirm(t('Fjerne demodata? Rader med godkjenninger eller aksept avsluttes i stedet for å slettes.'))) return
    setBusy(true)
    setError('')
    setMessage('')
    try {
      const result = await (await send(method, '/api/governance/demo-data')).json()
      setMessage(method === 'POST'
        ? t('Laget {c} endringer og {r} risikoer ({s} fantes fra før).', { c: result.changesCreated, r: result.risksCreated, s: result.skipped.length })
        : t('Slettet {d}, avsluttet {c}.', { d: result.deleted, c: result.closed }))
    } catch (e) {
      setError(errorText(e))
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="helpdesk-card">
      <h3>{t('Demodata')}</h3>
      <p className="audit-intro">
        {t('Syntetiske endringer og risikoer merket [DEMO], laget gjennom den vanlige arbeidsflyten med testbrukerne som aktører. Bare i utviklingstenanten.')}
      </p>
      <div className="settings-form-actions">
        <button type="button" disabled={busy} onClick={() => void run('POST')}>{t('Lag demodata')}</button>
        <button type="button" className="secondary" disabled={busy} onClick={() => void run('DELETE')}>{t('Fjern demodata')}</button>
      </div>
      {message && <p className="settings-message">{message}</p>}
      {error && <p role="alert" className="settings-error">{error}</p>}
    </div>
  )
}

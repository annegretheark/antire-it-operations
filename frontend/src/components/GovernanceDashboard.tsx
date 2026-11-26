import './Helpdesk.css'
import { useResource } from './settings/settingsApi'
import { formatDateTime } from '../audit'
import { changeStatusLabel, riskLevel, roleLabel } from '../governance'
import { t } from '../i18n'

type Dashboard = {
  openChanges: number
  pendingApproval: number
  upcomingProduction: { id: number; number: string; title: string; plannedStart: string | null; status: string }[]
  successful90Days: number
  failed90Days: number
  rolledBack90Days: number
  missingPostReview: number
  openRisks: number
  criticalRisks: number
  unownedRisks: number
  awaitingAcceptance: number
  acceptedRisks: number
  overdueActions: number
  acceptanceExpiringSoon: number
  matrix: number[][]
  roles: { role: string; people: number; groups: number; requiredByRules: boolean }[]
  riskOwners: { employeeId: number; name: string; openRisks: number; criticalRisks: number }[]
}

// Endringer og risiko på dashboardet (ENDRING_OG_RISIKO.md, C7). Tallene beregnes i
// backend. Vises ikke uten tilgang (Helpdesk.Agent eller en endringsrolle).
export default function GovernanceDashboard({ onOpen }: { onOpen: (page: string, query?: string) => void }) {
  const data = useResource<Dashboard>('/api/governance/dashboard')
  const d = data.data

  if (!d) return null

  const card = (label: string, value: number, page: string, query?: string, tone?: 'alert' | 'warning') => (
    <button type="button" className={`dashboard-stat gov-dashboard-card${value > 0 && tone ? ` dashboard-${tone}` : ''}`}
      onClick={() => onOpen(page, query)}>
      <h4>{label}</h4>
      <strong>{value}</strong>
    </button>
  )

  const missingRoles = d.roles.filter((r) => r.requiredByRules && r.people + r.groups === 0)

  return (
    <section className="card">
      <h3>{t('Endringer og risiko')}</h3>

      <h4>{t('Endringer')}</h4>
      <div className="dashboard-grid">
        {card(t('Åpne endringer'), d.openChanges, 'Endringer')}
        {card(t('Venter på godkjenning'), d.pendingApproval, 'Endringer', 'status=pending_approval', 'warning')}
        {card(t('Vellykkede (90 dager)'), d.successful90Days, 'Endringer')}
        {card(t('Mislykkede (90 dager)'), d.failed90Days, 'Endringer', 'status=failed', 'alert')}
        {card(t('Rollbacks (90 dager)'), d.rolledBack90Days, 'Endringer', 'status=rolled_back', 'warning')}
        {card(t('Mangler etterkontroll'), d.missingPostReview, 'Endringer', 'status=completed', 'alert')}
      </div>

      <h4>{t('Planlagte produksjonsendringer (14 dager)')}</h4>
      {d.upcomingProduction.length === 0 ? <p className="settings-muted">{t('Ingen.')}</p> : (
        <ul>
          {d.upcomingProduction.map((u) => (
            <li key={u.id}>
              <button type="button" className="gov-link" onClick={() => onOpen('Endringer', `endring=${u.id}`)}>{u.number}</button>
              {' '}{u.title} · {u.plannedStart ? formatDateTime(u.plannedStart) : '–'} · {changeStatusLabel(u.status)}
            </li>
          ))}
        </ul>
      )}

      <h4>{t('Risiko')}</h4>
      <div className="dashboard-grid">
        {card(t('Åpne risikoer'), d.openRisks, 'Risiko')}
        {card(t('Kritiske risikoer'), d.criticalRisks, 'Risiko', 'level=critical', 'alert')}
        {card(t('Uten eier'), d.unownedRisks, 'Risiko', 'flag=unowned', 'alert')}
        {card(t('Venter på aksept'), d.awaitingAcceptance, 'Risiko', 'flag=awaitingAcceptance', 'warning')}
        {card(t('Aksepterte'), d.acceptedRisks, 'Risiko', 'status=accepted')}
        {card(t('Forfalte tiltak'), d.overdueActions, 'Risiko', 'flag=overdue', 'alert')}
        {card(t('Aksept utløper snart'), d.acceptanceExpiringSoon, 'Risiko', 'status=accepted', 'warning')}
      </div>

      <div className="gov-dashboard-row">
        <div>
          <h4>{t('Risikomatrise')}</h4>
          <div className="gov-matrix">
            {[5, 4, 3, 2, 1].map((l) => (
              <div key={l} className="gov-matrix-row">
                <span className="gov-matrix-label">{l}</span>
                {[1, 2, 3, 4, 5].map((c) => (
                  <span key={c} className={`gov-matrix-cell gov-level-${riskLevel(l * c)}`}>{d.matrix[l - 1][c - 1] || ''}</span>
                ))}
              </div>
            ))}
          </div>
        </div>

        <div>
          <h4>{t('Ansvar og eierskap')}</h4>
          {missingRoles.length > 0 && (
            <p className="settings-error">
              {t('Påkrevd i godkjenningsreglene, men ikke tildelt: {roles}', { roles: missingRoles.map((r) => roleLabel(r.role)).join(', ') })}
            </p>
          )}
          <table className="audit-table">
            <thead><tr><th>{t('Rolle')}</th><th>{t('Personer')}</th><th>{t('Grupper')}</th></tr></thead>
            <tbody>
              {d.roles.map((r) => (
                <tr key={r.role} className={r.requiredByRules && r.people + r.groups === 0 ? 'settings-error' : undefined}>
                  <td>{roleLabel(r.role)}</td><td>{r.people}</td><td>{r.groups}</td>
                </tr>
              ))}
            </tbody>
          </table>
          {d.riskOwners.length > 0 && (
            <>
              <h4>{t('Risikoeiere')}</h4>
              <ul>
                {d.riskOwners.map((o) => (
                  <li key={o.employeeId}>
                    {o.name}: {t('{n} åpne', { n: o.openRisks })}
                    {o.criticalRisks > 0 && <span className="settings-error"> · {t('{n} kritiske', { n: o.criticalRisks })}</span>}
                  </li>
                ))}
              </ul>
            </>
          )}
        </div>
      </div>
    </section>
  )
}

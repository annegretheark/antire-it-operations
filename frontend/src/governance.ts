import { t } from './i18n'

// Change Management og Risk Management (ENDRING_OG_RISIKO.md): typer som speiler
// API-et, og norske etiketter. Koder er engelske, som i backend.

export type Option = { id: number; name: string }

export type ChangeTemplate = {
  id: number
  name: string
  description: string | null
  category: string | null
  implementationPlan: string | null
  testPlan: string | null
  rollbackPlan: string | null
  allowedEnvironments: string
  maxRiskScore: number
  approvedByName: string | null
  approvedAt: string | null
  nextReviewAt: string | null
  isActive: boolean
}

export type ChangeOptions = {
  services: Option[]
  subscriptions: Option[]
  templates: ChangeTemplate[]
  employees: Option[]
}

export type ChangeSummary = {
  id: number
  number: string
  title: string
  type: string
  environment: string
  priority: string
  status: string
  riskScore: number | null
  plannedStart: string | null
  plannedEnd: string | null
  requestedByName: string
  updatedAt: string
}

export type ChangeRequest = {
  id: number
  number: string
  title: string
  description: string
  justification: string | null
  expectedBenefit: string | null
  type: string
  priority: string
  environment: string
  status: string
  likelihood: number | null
  impact: number | null
  riskScore: number | null
  riskAnalysis: string | null
  impactAnalysis: string | null
  securityImpact: string | null
  gdprImpact: string | null
  processingActivityId: number | null
  costChangePerMonth: number | null
  costChangeNote: string | null
  backupStatus: string
  backupNote: string | null
  implementationPlan: string | null
  testPlan: string | null
  testResults: string | null
  rollbackPlan: string | null
  plannedStart: string | null
  plannedEnd: string | null
  requestedByObjectId: string
  requestedByName: string
  implementerEmployeeId: number | null
  implementationResult: string | null
  postReviewRequired: boolean
  postReviewNotes: string | null
  postReviewedAt: string | null
  postReviewedByName: string | null
  templateId: number | null
  pullRequestUrl: string | null
  commitSha: string | null
  pipelineRunId: string | null
  terraformPlanHash: string | null
  scopeHash: string | null
  createdAt: string
  updatedAt: string
  closedAt: string | null
}

export type ChangeDetails = {
  change: ChangeRequest
  serviceIds: number[]
  azureScopes: { azureSubscriptionId: number; resourceGroup: string | null }[]
  approvals: { id: number; role: string; decision: string; approverName: string; decidedAt: string; comment: string | null; valid: boolean }[]
  requirements: { role: string; minApprovals: number; approvedBy: string[]; satisfied: boolean }[]
  waitingForInfo: boolean
  allowedTransitions: string[]
  canDecideAs: string[]
  canPostReview: boolean
  linkedRiskIds: number[] | null
}

export type Risk = {
  id: number
  number: string
  title: string
  description: string
  category: string
  likelihood: number
  consequence: number
  score: number
  existingControls: string | null
  recommendedActions: string | null
  residualLikelihood: number | null
  residualConsequence: number | null
  residualScore: number | null
  strategy: string | null
  status: string
  ownerEmployeeId: number | null
  actionOwnerEmployeeId: number | null
  dueDate: string | null
  nextReviewAt: string | null
  createdAt: string
  updatedAt: string
  closedAt: string | null
  acceptances: {
    id: number
    kind: string
    actorName: string
    decidedAt: string
    justification: string | null
    scoreAtDecision: number
    validUntil: string | null
  }[]
}

export type RiskSummary = Pick<Risk,
  'id' | 'number' | 'title' | 'category' | 'likelihood' | 'consequence' | 'score' | 'residualScore' |
  'strategy' | 'status' | 'ownerEmployeeId' | 'actionOwnerEmployeeId' | 'dueDate' | 'nextReviewAt' | 'updatedAt'> & { level: string }

export type RiskLinks = { changeIds: number[]; serviceIds: number[]; ticketIds: number[]; alertIds: number[] }

export type RiskDetails = {
  risk: Risk
  level: string
  residualLevel: string | null
  acceptanceActive: boolean
  acceptanceInactiveReason: string | null
  links: RiskLinks
  allowedTransitions: string[]
  canEdit: boolean
  canAccept: boolean
}

export type RiskMatrix = { inherent: number[][]; residual: number[][]; openRisks: number }

export type AuditEntry = {
  id: number
  occurredAt: string
  actorName: string
  entityType: string
  entityId: string
  action: string
  changes: string | null
}

const CHANGE_TYPES: Record<string, string> = {
  standard: 'Standard',
  normal: 'Normal',
  emergency: 'Hasteendring',
}

const ENVIRONMENTS: Record<string, string> = {
  development: 'Utvikling',
  test: 'Test',
  production: 'Produksjon',
}

const CHANGE_STATUSES: Record<string, string> = {
  draft: 'Utkast',
  submitted: 'Sendt inn',
  risk_assessment: 'Risikovurdering',
  technical_review: 'Teknisk gjennomgang',
  testing: 'Testing',
  pending_approval: 'Venter på godkjenning',
  approved: 'Godkjent',
  scheduled: 'Planlagt',
  implementation: 'Implementeres',
  verification: 'Verifiseres',
  completed: 'Gjennomført',
  closed: 'Lukket',
  rejected: 'Avvist',
  cancelled: 'Avbrutt',
  failed: 'Feilet',
  rolled_back: 'Rullet tilbake',
}

const BACKUP_STATUSES: Record<string, string> = {
  verified: 'Verifisert',
  unverified: 'Ikke verifisert',
  missing: 'Mangler',
  not_applicable: 'Ikke relevant',
}

export const GOVERNANCE_ROLES: Record<string, string> = {
  change_requester: 'Change Requester',
  change_manager: 'Change Manager',
  technical_owner: 'Technical Owner',
  system_owner: 'System Owner',
  risk_owner: 'Risk Owner',
  security_officer: 'Security Officer',
  gdpr_responsible: 'GDPR Responsible',
  approver: 'Approver',
  implementer: 'Implementer',
  reviewer: 'Reviewer',
}

export const GOVERNANCE_ACTIVITIES: Record<string, string> = {
  submit_change: 'Melde inn endring',
  assess_change_risk: 'Vurdere risiko for endring',
  technical_review: 'Teknisk gjennomgang',
  approve_production_change: 'Godkjenne produksjonsendring',
  implement_change: 'Implementere endring',
  verify_change: 'Verifisere endring',
  emergency_post_review: 'Etterkontroll av hasteendring',
  accept_risk: 'Akseptere risiko',
  treat_risk: 'Behandle risiko',
}

export const RISK_CATEGORIES: Record<string, string> = {
  information_security: 'Informasjonssikkerhet',
  backup_and_disaster_recovery: 'Backup og katastrofegjenoppretting',
  gdpr_and_compliance: 'GDPR og etterlevelse',
  identity_and_access_management: 'Identitet og tilgang',
  financial_and_finops: 'Økonomi og FinOps',
  operational_risk: 'Operasjonell risiko',
  technical_debt: 'Teknisk gjeld',
  availability: 'Tilgjengelighet',
  governance: 'Styring',
  supplier_risk: 'Leverandørrisiko',
}

const RISK_STATUSES: Record<string, string> = {
  identified: 'Identifisert',
  assessed: 'Vurdert',
  in_treatment: 'Under behandling',
  monitoring: 'Overvåkes',
  accepted: 'Akseptert',
  closed: 'Lukket',
}

export const RISK_STRATEGIES: Record<string, string> = {
  mitigate: 'Redusere (Mitigate)',
  accept: 'Akseptere (Accept)',
  transfer: 'Overføre (Transfer)',
  avoid: 'Unngå (Avoid)',
}

const RISK_LEVELS: Record<string, string> = {
  low: 'Lav',
  medium: 'Middels',
  high: 'Høy',
  critical: 'Kritisk',
}

const label = (labels: Record<string, string>, code: string | null | undefined) =>
  code ? t(labels[code] ?? code) : '–'

export const changeTypeLabel = (code: string) => label(CHANGE_TYPES, code)
export const environmentLabel = (code: string) => label(ENVIRONMENTS, code)
export const changeStatusLabel = (code: string) => label(CHANGE_STATUSES, code)
export const backupStatusLabel = (code: string) => label(BACKUP_STATUSES, code)
export const roleLabel = (code: string) => label(GOVERNANCE_ROLES, code)
export const activityLabel = (code: string) => label(GOVERNANCE_ACTIVITIES, code)
export const riskCategoryLabel = (code: string) => label(RISK_CATEGORIES, code)
export const riskStatusLabel = (code: string) => label(RISK_STATUSES, code)
export const riskStrategyLabel = (code: string | null) => label(RISK_STRATEGIES, code)
export const riskLevelLabel = (code: string | null) => label(RISK_LEVELS, code)

export const CHANGE_TYPE_CODES = Object.keys(CHANGE_TYPES)
export const ENVIRONMENT_CODES = Object.keys(ENVIRONMENTS)
export const CHANGE_STATUS_CODES = Object.keys(CHANGE_STATUSES)
export const BACKUP_STATUS_CODES = Object.keys(BACKUP_STATUSES)
export const RISK_STATUS_CODES = Object.keys(RISK_STATUSES)
export const RISK_LEVEL_CODES = Object.keys(RISK_LEVELS)

// Nivå etter score (samme grenser som RiskLevels i backend)
export function riskLevel(score: number): string {
  if (score >= 15) return 'critical'
  if (score >= 10) return 'high'
  if (score >= 5) return 'medium'
  return 'low'
}

// Dato og tid i lokal tid, til <input type="datetime-local"> og tilbake til UTC
export function toLocalInput(value: string | null): string {
  if (!value) return ''
  const d = new Date(value)
  const pad = (n: number) => String(n).padStart(2, '0')
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`
}

export function fromLocalInput(value: string): string | null {
  return value ? new Date(value).toISOString() : null
}

// Mandag 00:00 lokal tid for uken datoen ligger i
export function startOfWeek(date: Date): Date {
  const d = new Date(date)
  d.setHours(0, 0, 0, 0)
  d.setDate(d.getDate() - ((d.getDay() + 6) % 7))
  return d
}

// Detaljvisning fra lenke (?endring=12 eller ?risiko=3), ellers null
export function linkedId(param: 'endring' | 'risiko'): { kind: 'details'; id: number } | null {
  const id = Number(new URLSearchParams(window.location.search).get(param))
  return Number.isInteger(id) && id > 0 ? { kind: 'details', id } : null
}

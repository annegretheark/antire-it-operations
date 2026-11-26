namespace backend.Models;

// Change Management og Risk Management (ENDRING_OG_RISIKO.md). Prototype: bare
// syntetiske data i utviklingstenanten. Statuser og typer lagres som engelske koder
// og vises på norsk og engelsk i frontend.

// Rader som bare kan legges til, aldri endres eller slettes (stoppes i AuditInterceptor).
// Beslutninger (godkjenninger, risikoaksept) endres bare ved å legge til en ny hendelse.
public interface IAppendOnly
{
}

public static class ChangeTypes
{
    public const string Standard = "standard";
    public const string Normal = "normal";
    public const string Emergency = "emergency";

    public static readonly string[] All = [Standard, Normal, Emergency];
}

public static class ChangeEnvironments
{
    public const string Development = "development";
    public const string Test = "test";
    public const string Production = "production";

    public static readonly string[] All = [Development, Test, Production];
}

public static class ChangeStatuses
{
    public const string Draft = "draft";
    public const string Submitted = "submitted";
    public const string RiskAssessment = "risk_assessment";
    public const string TechnicalReview = "technical_review";
    public const string Testing = "testing";
    public const string PendingApproval = "pending_approval";
    public const string Approved = "approved";
    public const string Scheduled = "scheduled";
    public const string Implementation = "implementation";
    public const string Verification = "verification";
    public const string Completed = "completed";
    public const string Closed = "closed";
    public const string Rejected = "rejected";
    public const string Cancelled = "cancelled";
    public const string Failed = "failed";
    public const string RolledBack = "rolled_back";

    public static readonly string[] All =
    [
        Draft, Submitted, RiskAssessment, TechnicalReview, Testing, PendingApproval, Approved,
        Scheduled, Implementation, Verification, Completed, Closed, Rejected, Cancelled, Failed, RolledBack,
    ];
}

public static class BackupStatuses
{
    public const string Verified = "verified";
    public const string Unverified = "unverified";
    public const string Missing = "missing";
    public const string NotApplicable = "not_applicable";

    public static readonly string[] All = [Verified, Unverified, Missing, NotApplicable];
}

public static class ApprovalDecisions
{
    public const string Approved = "approved";
    public const string Rejected = "rejected";
    public const string InfoRequested = "info_requested";

    public static readonly string[] All = [Approved, Rejected, InfoRequested];
}

// De ti rollene i endrings- og risikoprosessen (konfigureres i GovernanceRoleAssignments)
public static class GovernanceRoles
{
    public const string ChangeRequester = "change_requester";
    public const string ChangeManager = "change_manager";
    public const string TechnicalOwner = "technical_owner";
    public const string SystemOwner = "system_owner";
    public const string RiskOwner = "risk_owner";
    public const string SecurityOfficer = "security_officer";
    public const string GdprResponsible = "gdpr_responsible";
    public const string Approver = "approver";
    public const string Implementer = "implementer";
    public const string Reviewer = "reviewer";

    public static readonly string[] All =
    [
        ChangeRequester, ChangeManager, TechnicalOwner, SystemOwner, RiskOwner,
        SecurityOfficer, GdprResponsible, Approver, Implementer, Reviewer,
    ];
}

public static class RaciLetters
{
    public const string Responsible = "R";
    public const string Accountable = "A";
    public const string Consulted = "C";
    public const string Informed = "I";

    public static readonly string[] All = [Responsible, Accountable, Consulted, Informed];
}

// Aktiviteter i RACI-matrisen
public static class GovernanceActivities
{
    public const string SubmitChange = "submit_change";
    public const string AssessChangeRisk = "assess_change_risk";
    public const string TechnicalReview = "technical_review";
    public const string ApproveProductionChange = "approve_production_change";
    public const string ImplementChange = "implement_change";
    public const string VerifyChange = "verify_change";
    public const string EmergencyPostReview = "emergency_post_review";
    public const string AcceptRisk = "accept_risk";
    public const string TreatRisk = "treat_risk";

    public static readonly string[] All =
    [
        SubmitChange, AssessChangeRisk, TechnicalReview, ApproveProductionChange,
        ImplementChange, VerifyChange, EmergencyPostReview, AcceptRisk, TreatRisk,
    ];
}

public static class RiskCategories
{
    public const string InformationSecurity = "information_security";
    public const string BackupAndDisasterRecovery = "backup_and_disaster_recovery";
    public const string GdprAndCompliance = "gdpr_and_compliance";
    public const string IdentityAndAccessManagement = "identity_and_access_management";
    public const string FinancialAndFinOps = "financial_and_finops";
    public const string OperationalRisk = "operational_risk";
    public const string TechnicalDebt = "technical_debt";
    public const string Availability = "availability";
    public const string Governance = "governance";
    public const string SupplierRisk = "supplier_risk";

    public static readonly string[] All =
    [
        InformationSecurity, BackupAndDisasterRecovery, GdprAndCompliance, IdentityAndAccessManagement,
        FinancialAndFinOps, OperationalRisk, TechnicalDebt, Availability, Governance, SupplierRisk,
    ];
}

public static class RiskStrategies
{
    public const string Mitigate = "mitigate";
    public const string Accept = "accept";
    public const string Transfer = "transfer";
    public const string Avoid = "avoid";

    public static readonly string[] All = [Mitigate, Accept, Transfer, Avoid];
}

public static class RiskStatuses
{
    public const string Identified = "identified";
    public const string Assessed = "assessed";
    public const string InTreatment = "in_treatment";
    public const string Monitoring = "monitoring";
    public const string Accepted = "accepted";
    public const string Closed = "closed";

    public static readonly string[] All = [Identified, Assessed, InTreatment, Monitoring, Accepted, Closed];
}

public static class RiskAcceptanceKinds
{
    public const string Accepted = "accepted";
    public const string Revoked = "revoked";

    public static readonly string[] All = [Accepted, Revoked];
}

// Sannsynlighet × konsekvens (1–5 hver). Beregnes også i databasen (beregnede kolonner).
public static class RiskScore
{
    public const int Min = 1;
    public const int Max = 5;

    public static bool IsValid(int? value) => value is >= Min and <= Max;

    public static int? Of(int? likelihood, int? consequence) =>
        likelihood is { } l && consequence is { } c ? l * c : null;
}

// En endringsforespørsel (CHG-000001)
public class ChangeRequest : ITenantOwned, IAudited
{
    public int Id { get; set; }

    public string TenantId { get; set; } = string.Empty;

    // CHG-000001, unikt per tenant (GovernanceNumberService)
    public string Number { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? Justification { get; set; }
    public string? ExpectedBenefit { get; set; }

    // standard | normal | emergency (ChangeTypes)
    public string Type { get; set; } = ChangeTypes.Normal;

    // low | normal | high | critical (TicketPriorities)
    public string Priority { get; set; } = TicketPriorities.Normal;

    // development | test | production (ChangeEnvironments)
    public string Environment { get; set; } = ChangeEnvironments.Development;

    // ChangeStatuses
    public string Status { get; set; } = ChangeStatuses.Draft;

    // Risiko for selve endringen, 1–5; RiskScore beregnes i databasen
    public int? Likelihood { get; set; }
    public int? Impact { get; set; }
    [System.Text.Json.Serialization.JsonInclude]
    public int? RiskScore { get; private set; }

    public string? RiskAnalysis { get; set; }
    public string? ImpactAnalysis { get; set; }
    public string? SecurityImpact { get; set; }
    public string? GdprImpact { get; set; }

    // Behandlingen i protokollen endringen berører (GDPR)
    public int? ProcessingActivityId { get; set; }

    // Forventet endring i kostnad per måned (NOK), og forklaring
    public decimal? CostChangePerMonth { get; set; }
    public string? CostChangeNote { get; set; }

    // verified | unverified | missing | not_applicable (BackupStatuses)
    public string BackupStatus { get; set; } = BackupStatuses.Unverified;
    public string? BackupNote { get; set; }

    public string? ImplementationPlan { get; set; }
    public string? TestPlan { get; set; }
    public string? TestResults { get; set; }
    public string? RollbackPlan { get; set; }

    public DateTime? PlannedStart { get; set; }
    public DateTime? PlannedEnd { get; set; }

    // Innmelder (innlogget bruker ved opprettelse)
    public string RequestedByObjectId { get; set; } = string.Empty;
    public string RequestedByName { get; set; } = string.Empty;
    public int? RequestedByEmployeeId { get; set; }

    // Ansvarlig for gjennomføring
    public int? ImplementerEmployeeId { get; set; }

    public string? ImplementationResult { get; set; }

    // Etterkontroll (påkrevd for emergency)
    public bool PostReviewRequired { get; set; }
    public string? PostReviewNotes { get; set; }
    public DateTime? PostReviewedAt { get; set; }
    public string? PostReviewedByName { get; set; }

    // Standardmalen endringen bygger på
    public int? TemplateId { get; set; }

    // Leveranse (simulert GitHub og Terraform i prototypen)
    public string? PullRequestUrl { get; set; }
    public string? CommitSha { get; set; }
    public string? PipelineRunId { get; set; }
    public string? TerraformPlanHash { get; set; }

    // Hash av godkjent innhold slik det er nå; godkjenninger med en annen hash gjelder ikke
    public string? ScopeHash { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? ClosedAt { get; set; }

    public List<ChangeAffectedService> Services { get; set; } = [];
    public List<ChangeAzureScope> AzureScopes { get; set; } = [];
    public List<ChangeApproval> Approvals { get; set; } = [];
}

// Berørt system eller tjeneste
public class ChangeAffectedService : ITenantOwned, IAudited
{
    public int Id { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public int ChangeRequestId { get; set; }
    public int ServiceCatalogItemId { get; set; }
}

// Berørt Azure-abonnement, eventuelt én ressursgruppe
public class ChangeAzureScope : ITenantOwned, IAudited
{
    public int Id { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public int ChangeRequestId { get; set; }
    public int AzureSubscriptionId { get; set; }
    public string? ResourceGroup { get; set; }
}

// Én beslutning fra én godkjenner. Kan aldri endres; gjelder bare så lenge
// endringens ScopeHash er lik hashen her.
public class ChangeApproval : ITenantOwned, IAudited, IAppendOnly
{
    public int Id { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public int ChangeRequestId { get; set; }

    // Rollen beslutningen ble tatt i (GovernanceRoles)
    public string Role { get; set; } = string.Empty;

    // approved | rejected | info_requested (ApprovalDecisions)
    public string Decision { get; set; } = string.Empty;

    public string ApproverObjectId { get; set; } = string.Empty;
    public string ApproverName { get; set; } = string.Empty;
    public int? ApproverEmployeeId { get; set; }

    public DateTime DecidedAt { get; set; }
    public string? Comment { get; set; }

    // Endringens ScopeHash da beslutningen ble tatt
    public string ScopeHash { get; set; } = string.Empty;
}

// Forhåndsgodkjent mal for standardendringer
public class ChangeTemplate : ITenantOwned, IAudited
{
    public int Id { get; set; }
    public string TenantId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Category { get; set; }

    public string? ImplementationPlan { get; set; }
    public string? TestPlan { get; set; }
    public string? RollbackPlan { get; set; }

    // Miljøer malen kan brukes i, kommaseparert (ChangeEnvironments)
    public string AllowedEnvironments { get; set; } = ChangeEnvironments.Development;

    // Høyeste risikoscore (1–25) en endring fra malen kan ha
    public int MaxRiskScore { get; set; } = 6;

    // Forhåndsgodkjenning (Change Manager)
    public string? ApprovedByObjectId { get; set; }
    public string? ApprovedByName { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public DateTime? NextReviewAt { get; set; }

    public bool IsActive { get; set; } = true;
}

// En rolle tildelt en ansatt eller en Entra-gruppe, globalt eller for én tjeneste
public class GovernanceRoleAssignment : ITenantOwned, IAudited
{
    public int Id { get; set; }
    public string TenantId { get; set; } = string.Empty;

    // GovernanceRoles
    public string Role { get; set; } = string.Empty;

    // Null = gjelder alle systemer og tjenester
    public int? ServiceCatalogItemId { get; set; }

    // Nøyaktig én av disse er satt
    public int? EmployeeId { get; set; }
    public string? EntraGroupId { get; set; }
}

// Én celle i RACI-matrisen: aktivitet × rolle → R, A, C eller I
public class RaciEntry : ITenantOwned, IAudited
{
    public int Id { get; set; }
    public string TenantId { get; set; } = string.Empty;

    // GovernanceActivities
    public string Activity { get; set; } = string.Empty;

    // GovernanceRoles
    public string Role { get; set; } = string.Empty;

    // R | A | C | I (RaciLetters)
    public string Raci { get; set; } = RaciLetters.Informed;
}

// Hvilke roller som må godkjenne, per type og miljø
public class ApprovalRule : ITenantOwned, IAudited
{
    public const string AnyEnvironment = "any";

    public int Id { get; set; }
    public string TenantId { get; set; } = string.Empty;

    // ChangeTypes
    public string ChangeType { get; set; } = ChangeTypes.Normal;

    // ChangeEnvironments, eller «any»
    public string Environment { get; set; } = AnyEnvironment;

    // GovernanceRoles
    public string RequiredRole { get; set; } = string.Empty;

    public int MinApprovals { get; set; } = 1;

    public int SortOrder { get; set; }
}

// En risiko i risikoregisteret (RISK-000001)
public class Risk : ITenantOwned, IAudited
{
    public int Id { get; set; }
    public string TenantId { get; set; } = string.Empty;

    // RISK-000001, unikt per tenant
    public string Number { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    // RiskCategories
    public string Category { get; set; } = RiskCategories.OperationalRisk;

    // 1–5; Score beregnes i databasen
    public int Likelihood { get; set; } = 1;
    public int Consequence { get; set; } = 1;
    [System.Text.Json.Serialization.JsonInclude]
    public int Score { get; private set; }

    public string? ExistingControls { get; set; }
    public string? RecommendedActions { get; set; }

    // Restrisiko etter tiltak, 1–5; ResidualScore beregnes i databasen
    public int? ResidualLikelihood { get; set; }
    public int? ResidualConsequence { get; set; }
    [System.Text.Json.Serialization.JsonInclude]
    public int? ResidualScore { get; private set; }

    // mitigate | accept | transfer | avoid (RiskStrategies)
    public string? Strategy { get; set; }

    // RiskStatuses
    public string Status { get; set; } = RiskStatuses.Identified;

    public int? OwnerEmployeeId { get; set; }
    public int? ActionOwnerEmployeeId { get; set; }

    // Frist for tiltak og dato for neste vurdering
    public DateTime? DueDate { get; set; }
    public DateTime? NextReviewAt { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? ClosedAt { get; set; }

    public List<RiskAcceptance> Acceptances { get; set; } = [];
}

// Aksept eller tilbaketrekking av aksept. Kan aldri endres; siste rad gjelder.
public class RiskAcceptance : ITenantOwned, IAudited, IAppendOnly
{
    public int Id { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public int RiskId { get; set; }

    // accepted | revoked (RiskAcceptanceKinds)
    public string Kind { get; set; } = RiskAcceptanceKinds.Accepted;

    public string ActorObjectId { get; set; } = string.Empty;
    public string ActorName { get; set; } = string.Empty;
    public int? ActorEmployeeId { get; set; }

    public DateTime DecidedAt { get; set; }
    public string? Justification { get; set; }

    // Risikoscoren da beslutningen ble tatt
    public int ScoreAtDecision { get; set; }

    // Når aksepten må vurderes på nytt
    public DateTime? ValidUntil { get; set; }
}

public class RiskChange : ITenantOwned, IAudited
{
    public int Id { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public int RiskId { get; set; }
    public int ChangeRequestId { get; set; }
}

public class RiskService : ITenantOwned, IAudited
{
    public int Id { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public int RiskId { get; set; }
    public int ServiceCatalogItemId { get; set; }
}

// Risiko ↔ hendelse: en sak eller et varsel (nøyaktig én er satt)
public class RiskIncident : ITenantOwned, IAudited
{
    public int Id { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public int RiskId { get; set; }
    public int? TicketId { get; set; }
    public int? AlertId { get; set; }
}

// Innstillinger per tenant for endringer og risiko
public class GovernanceSettings : ITenantOwned, IAudited
{
    public int Id { get; set; }
    public string TenantId { get; set; } = string.Empty;

    // Måneder etter lukking før endringer og risikoer arkiveres; null = beholdes
    public int? RetentionMonths { get; set; }

    // Påminnelser om kommende endringer og frister, dager før
    public int ReminderDaysBefore { get; set; } = 3;

    // Simulert GitHub og Terraform (prototype). Ekte integrasjon finnes ikke ennå.
    public bool SimulatedDeliveryEnabled { get; set; } = true;

    // Teams-kort for endringer og risiko (C5). Krever at Teams er satt opp under Innstillinger.
    public bool TeamsNotificationsEnabled { get; set; } = true;
}

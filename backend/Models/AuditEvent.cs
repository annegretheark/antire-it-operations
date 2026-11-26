namespace backend.Models;

// Entiteter som revisjonsloggføres automatisk ved opprettelse, endring og sletting
// (se AppDbContext). Felter som Id og TenantId logges ikke.
public interface IAudited
{
}

// Én hendelse i revisjonsloggen. Rader legges bare til; de kan aldri endres
// eller slettes (AppDbContext stopper det, og det finnes ingen endepunkter for det).
public class AuditEvent : ITenantOwned
{
    public long Id { get; set; }

    // Entra-tenanten hendelsen hører til (settes automatisk)
    public string TenantId { get; set; } = string.Empty;

    public DateTime OccurredAt { get; set; }

    // Entra objekt-ID og navn til den som gjorde endringen ("system" uten innlogget bruker)
    public string ActorObjectId { get; set; } = string.Empty;
    public string ActorName { get; set; } = string.Empty;

    // F.eks. Ticket, Employee, AccessRequest
    public string EntityType { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;

    // created | updated | deleted | approved | rejected | completed | synced | sla_breached
    // | access_granted | access_grant_failed | alert_recurred | alert_resolved | reopened_by_alert
    public string Action { get; set; } = string.Empty;

    // JSON: { "Felt": [før, etter] } for endringer, eller fritt innhold for
    // forretningshendelser (f.eks. antall ved synk)
    public string? Changes { get; set; }
}

public static class AuditActions
{
    public const string Created = "created";
    public const string Updated = "updated";
    public const string Deleted = "deleted";
    public const string Approved = "approved";
    public const string Rejected = "rejected";
    public const string Completed = "completed";
    public const string Synced = "synced";
    public const string SlaBreached = "sla_breached";
    // Brukeren ble lagt i Entra-gruppen for en godkjent tilgangsbestilling (2.4)
    public const string AccessGranted = "access_granted";
    public const string AccessGrantFailed = "access_grant_failed";
    // Varsler (fase 4)
    public const string AlertRecurred = "alert_recurred";
    public const string AlertResolved = "alert_resolved";
    public const string ReopenedByAlert = "reopened_by_alert";
    // Onboarding og offboarding
    public const string LifecycleStarted = "lifecycle_started";
    public const string AccessRevoked = "access_revoked";
    // GDPR
    public const string GdprExported = "gdpr_exported";
    public const string GdprAnonymized = "gdpr_anonymized";
    // Brukere i Entra ID, endret fra Microsoft 365-siden med personens egne rettigheter
    public const string UserDisabled = "user_disabled";
    public const string UserEnabled = "user_enabled";
    public const string UserDeleted = "user_deleted";
    // Change Management (ENDRING_OG_RISIKO.md)
    public const string ChangeStatusChanged = "change_status_changed";
    public const string ChangeApproved = "change_approved";
    public const string ChangeRejected = "change_rejected";
    public const string ChangeInfoRequested = "change_info_requested";
    public const string ApprovalInvalidated = "approval_invalidated";
    public const string ChangePostReviewed = "change_post_reviewed";
    public const string RoleAssigned = "role_assigned";
    public const string RoleRemoved = "role_removed";
    public const string TemplateApproved = "template_approved";
    // Risk Management
    public const string RiskStatusChanged = "risk_status_changed";
    public const string RiskAccepted = "risk_accepted";
    public const string RiskAcceptanceRevoked = "risk_acceptance_revoked";
    public const string RiskAcceptanceVoided = "risk_acceptance_voided";
    public const string RiskAcceptanceExpired = "risk_acceptance_expired";
    public const string RiskActionOverdue = "risk_action_overdue";
    public const string RiskReviewDue = "risk_review_due";
    public const string RiskLinksChanged = "risk_links_changed";
    // Påminnelser sendt til Teams (C5), logget én gang per hendelse
    public const string ChangeUpcomingNotified = "change_upcoming_notified";
    public const string ChangePostReviewReminder = "change_post_review_reminder";
    public const string RiskUntreatedNotified = "risk_untreated_notified";
    public const string RiskAcceptanceExpiring = "risk_acceptance_expiring";
    // Leveranse (C6, simulert GitHub og Terraform)
    public const string ChangePlanCreated = "change_plan_created";
    public const string ChangeGateChecked = "change_gate_checked";
    public const string ChangeSimulatedApply = "change_simulated_apply";
    // AI (C8): hvilken oppgave som ble sendt, ikke innholdet
    public const string AiSuggestionRequested = "ai_suggestion_requested";
}

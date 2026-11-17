namespace backend.Models;

public static class LifecycleTypes
{
    public const string Onboarding = "onboarding";
    public const string Offboarding = "offboarding";

    public static readonly string[] All = [Onboarding, Offboarding];
}

public static class LifecycleStatuses
{
    public const string Active = "active";
    public const string Completed = "completed";
    public const string Cancelled = "cancelled";
}

// Én oppgave i malen for onboarding eller offboarding (Innstillinger → Onboarding-maler).
// Fristen regnes fra startdato (onboarding) eller sluttdato (offboarding).
public class LifecycleTemplateTask : ITenantOwned, IAudited
{
    public int Id { get; set; }

    public string TenantId { get; set; } = string.Empty;

    // onboarding | offboarding
    public string Type { get; set; } = LifecycleTypes.Onboarding;

    public string Title { get; set; } = string.Empty;

    public string? Description { get; set; }

    // Ansvarlig område, f.eks. «Tilgangsbestilling» (som kategoriene i katalogen)
    public string? Category { get; set; }

    // Dager før (negativt) eller etter start- eller sluttdato
    public int OffsetDays { get; set; }

    public int SortOrder { get; set; }

    // Onboarding: tjeneste (type access) som skal bestilles for den ansatte
    public int? ServiceCatalogItemId { get; set; }

    public bool IsActive { get; set; } = true;
}

// Onboarding eller offboarding for én ansatt. Bare én aktiv av hver type per ansatt.
public class LifecycleProcess : ITenantOwned, IAudited
{
    public int Id { get; set; }

    public string TenantId { get; set; } = string.Empty;

    public int EmployeeId { get; set; }
    public Employee Employee { get; set; } = null!;

    public string Type { get; set; } = LifecycleTypes.Onboarding;

    // Startdato (onboarding) eller sluttdato (offboarding)
    public DateTime ReferenceDate { get; set; }

    // active | completed | cancelled
    public string Status { get; set; } = LifecycleStatuses.Active;

    public DateTime CreatedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public List<LifecycleTask> Tasks { get; set; } = [];
}

public class LifecycleTask : ITenantOwned, IAudited
{
    public int Id { get; set; }

    public string TenantId { get; set; } = string.Empty;

    public int ProcessId { get; set; }

    public string Title { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string? Category { get; set; }

    public DateTime DueDate { get; set; }

    public int SortOrder { get; set; }

    public int? ServiceCatalogItemId { get; set; }

    public DateTime? CompletedAt { get; set; }
    public string? CompletedByName { get; set; }

    public string? Note { get; set; }
}

// Behandlingsprotokoll (GDPR art. 30): én behandling av personopplysninger
public class ProcessingActivity : ITenantOwned, IAudited
{
    public int Id { get; set; }

    public string TenantId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    // System eller tjeneste behandlingen skjer i
    public string? System { get; set; }

    public string Purpose { get; set; } = string.Empty;

    // Hvem opplysningene gjelder, f.eks. «Ansatte»
    public string DataSubjects { get; set; } = string.Empty;

    // Hvilke opplysninger, f.eks. «Navn, e-post, stilling»
    public string DataCategories { get; set; } = string.Empty;

    // LegalBases
    public string LegalBasis { get; set; } = LegalBases.Contract;

    public string Retention { get; set; } = string.Empty;

    // Databehandlere og andre mottakere
    public string? Recipients { get; set; }

    // Overføring til land utenfor EØS (og grunnlaget for den)
    public string? ThirdCountryTransfers { get; set; }

    public string? SecurityMeasures { get; set; }

    // Ansvarlig person eller rolle
    public string? Owner { get; set; }

    public DateTime? LastReviewedAt { get; set; }
}

// Behandlingsgrunnlag etter GDPR art. 6 nr. 1
public static class LegalBases
{
    public const string Consent = "consent";
    public const string Contract = "contract";
    public const string LegalObligation = "legal_obligation";
    public const string VitalInterests = "vital_interests";
    public const string PublicTask = "public_task";
    public const string LegitimateInterests = "legitimate_interests";

    public static readonly string[] All =
        [Consent, Contract, LegalObligation, VitalInterests, PublicTask, LegitimateInterests];
}

// GDPR-innstillinger for tenanten
public class GdprSettings : ITenantOwned, IAudited
{
    public int Id { get; set; }

    public string TenantId { get; set; } = string.Empty;

    // Ansatte som har sluttet, anonymiseres automatisk så mange måneder etter
    // sluttdato. Null = av.
    public int? AnonymizeAfterMonths { get; set; }
}

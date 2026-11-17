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

    // created | updated | deleted | approved | rejected | completed | synced
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
}

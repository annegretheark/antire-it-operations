using System.ComponentModel.DataAnnotations.Schema;

namespace backend.Models;

public class Ticket : ITenantOwned, IAudited
{
    public int Id { get; set; }

    // Entra-tenanten raden hører til (settes automatisk)
    public string TenantId { get; set; } = string.Empty;

    public int? ReportedByEmployeeId { get; set; }
    public Employee? ReportedByEmployee { get; set; }

    public string Category { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    // new | in_progress | resolved (TicketStatuses)
    public string Status { get; set; } = TicketStatuses.New;

    // low | normal | high | critical (TicketPriorities)
    public string Priority { get; set; } = TicketPriorities.Normal;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Ansvarlig ansatt (eneste ansvarlig-felt, se migrering MergeTicketAssignee)
    public int? AssignedToEmployeeId { get; set; }
    public Employee? AssignedToEmployee { get; set; }

    // Tjenesten saken ble meldt inn på (valgfri). Category settes fra tjenesten.
    public int? ServiceCatalogItemId { get; set; }

    // SLA-frister, beregnet fra CreatedAt ved opprettelse og ved endring av
    // prioritet (SlaService). Null for saker fra før SLA.
    public DateTime? ResponseDueAt { get; set; }
    public DateTime? ResolutionDueAt { get; set; }

    // Første gang en agent endret status eller ansvarlig
    public DateTime? FirstRespondedAt { get; set; }

    // Når saken ble løst; nullstilles hvis saken gjenåpnes
    public DateTime? ResolvedAt { get; set; }

    // Satt av SlaMonitorService når fristen er passert (lik fristen), slik at
    // bruddet logges én gang i historikken
    public DateTime? ResponseBreachedAt { get; set; }
    public DateTime? ResolutionBreachedAt { get; set; }

    // ok | at_risk | breached | met | none (SlaStatuses), beregnet nå
    [NotMapped]
    public string SlaStatus => Services.SlaEvaluator.Evaluate(this, DateTime.UtcNow);
}

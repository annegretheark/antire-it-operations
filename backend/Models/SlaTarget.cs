namespace backend.Models;

// SLA-mål per prioritet i en tenant. Minuttene er arbeidstid (08–16 på
// virkedager, se SlaCalculator), eller vanlig tid når TwentyFourSeven er satt.
public class SlaTarget : ITenantOwned, IAudited
{
    public int Id { get; set; }

    // Entra-tenanten raden hører til (settes automatisk)
    public string TenantId { get; set; } = string.Empty;

    // low | normal | high | critical (TicketPriorities), unik i tenanten
    public string Priority { get; set; } = string.Empty;

    // Tid til første respons (agent endrer status eller ansvarlig)
    public int ResponseMinutes { get; set; }

    // Tid til saken er løst
    public int ResolutionMinutes { get; set; }

    // Fristen løper døgnet rundt, også i helger og på helligdager
    public bool TwentyFourSeven { get; set; }
}

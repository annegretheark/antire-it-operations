using Microsoft.EntityFrameworkCore;
using backend.Data;
using backend.Models;

namespace backend.Services;

// Startinnhold i tjenestekatalogen. En tenant som aldri har hatt en katalog får
// dagens fire sakskategorier og systemene fra det gamle tilgangsskjemaet første
// gang katalogen hentes. Tjenester slettes aldri (bare deaktiveres), så en tom
// katalog betyr alltid en ny tenant.
public class ServiceCatalogService
{
    private readonly AppDbContext _db;
    private readonly ILogger<ServiceCatalogService> _logger;

    public ServiceCatalogService(AppDbContext db, ILogger<ServiceCatalogService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public static IReadOnlyList<ServiceCatalogItem> DefaultItems() =>
    [
        Item("Feil", "Feil", ServiceCatalogTypes.Incident, "Noe virker ikke som det skal."),
        Item("Tilgangsbestilling", "Tilgangsbestilling", ServiceCatalogTypes.Request, "Spørsmål om eller behov for tilgang."),
        Item("Bestilling av utstyr", "Bestilling av utstyr", ServiceCatalogTypes.Request, "PC, skjerm, mobil og annet utstyr."),
        Item("Annet", "Annet", ServiceCatalogTypes.Incident, "Alt som ikke passer i de andre."),

        // Systemene i tilgangsskjemaet før katalogen fantes
        Access("Microsoft 365"),
        Access("Teams"),
        Access("SharePoint"),
        Access("Azure"),
        Access("Annet system"),
    ];

    public async Task EnsureSeededAsync(CancellationToken cancellationToken = default)
    {
        if (await _db.ServiceCatalogItems.AnyAsync(cancellationToken))
            return;

        _db.ServiceCatalogItems.AddRange(DefaultItems());

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            // En annen forespørsel rakk å opprette startinnholdet først (unikt navn)
            _logger.LogInformation(ex, "Startinnhold i tjenestekatalogen fantes allerede.");
            _db.ChangeTracker.Clear();
        }
    }

    private static ServiceCatalogItem Item(
        string name, string category, string type, string description) => new()
    {
        Name = name,
        Category = category,
        Type = type,
        Description = description,
    };

    private static ServiceCatalogItem Access(string system) => new()
    {
        Name = system,
        Category = "Tilgangsbestilling",
        Type = ServiceCatalogTypes.Access,
        RequiresApproval = true,
    };
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using backend.Data;
using backend.Models;

namespace backend.Services;

// Tenant satt i koden, for arbeid uten innlogget bruker (bakgrunnsjobber)
public sealed class FixedTenantContext(string tenantId) : ITenantContext
{
    public string? TenantId { get; } = tenantId.ToLowerInvariant();
}

// Markerer SLA-brudd på åpne saker i alle tillatte tenanter, slik at bruddet
// logges én gang i sakens historikk (aktør «system»). «I fare» beregnes når
// sakene hentes og lagres ikke.
//
// Konfigurasjon: Sla:MonitorEnabled (standard true) og Sla:CheckIntervalSeconds (300).
public class SlaMonitorService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IOptionsMonitor<TenantOptions> _tenants;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SlaMonitorService> _logger;

    public SlaMonitorService(
        IServiceScopeFactory scopes,
        IOptionsMonitor<TenantOptions> tenants,
        IConfiguration configuration,
        ILogger<SlaMonitorService> logger)
    {
        _scopes = scopes;
        _tenants = tenants;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_configuration.GetValue("Sla:MonitorEnabled", true))
            return;

        var interval = TimeSpan.FromSeconds(
            Math.Max(30, _configuration.GetValue("Sla:CheckIntervalSeconds", 300)));

        using var timer = new PeriodicTimer(interval);

        do
        {
            foreach (var tenant in _tenants.CurrentValue.Allowed)
            {
                try
                {
                    await CheckTenantAsync(tenant.TenantId, DateTime.UtcNow, stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    // Én tenant som feiler skal ikke stoppe de andre eller appen
                    _logger.LogError(ex, "SLA-sjekk feilet for tenant {TenantId}", tenant.TenantId);
                }
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    // Markerer nye brudd i én tenant. Gir antall saker som fikk et nytt brudd.
    public async Task<int> CheckTenantAsync(
        string tenantId, DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        using var scope = _scopes.CreateTenantScope(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var tickets = await db.Tickets
            .Where(t => t.Status != TicketStatuses.Resolved)
            .Where(t =>
                (t.ResponseBreachedAt == null && t.FirstRespondedAt == null &&
                 t.ResponseDueAt != null && t.ResponseDueAt < nowUtc) ||
                (t.ResolutionBreachedAt == null &&
                 t.ResolutionDueAt != null && t.ResolutionDueAt < nowUtc))
            .ToListAsync(cancellationToken);

        foreach (var ticket in tickets)
        {
            if (ticket.ResponseBreachedAt == null && ticket.FirstRespondedAt == null &&
                ticket.ResponseDueAt < nowUtc)
            {
                ticket.ResponseBreachedAt = ticket.ResponseDueAt;
            }

            if (ticket.ResolutionBreachedAt == null && ticket.ResolutionDueAt < nowUtc)
                ticket.ResolutionBreachedAt = ticket.ResolutionDueAt;

            db.SetAuditAction(ticket, AuditActions.SlaBreached);
        }

        if (tickets.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
            _logger.LogInformation(
                "SLA-brudd markert på {Count} saker i tenant {TenantId}", tickets.Count, tenantId);
        }

        return tickets.Count;
    }
}

using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using backend.Data;
using backend.Models;
using backend.Services;

namespace backend.Controllers;

// Hendelse slik API-et viser den. Changes er JSON-objektet fra databasen,
// f.eks. { "Status": ["new", "resolved"] }.
public record AuditEventDto(
    long Id,
    DateTime OccurredAt,
    string ActorObjectId,
    string ActorName,
    string EntityType,
    string EntityId,
    string Action,
    JsonElement? Changes)
{
    public static AuditEventDto From(AuditEvent e) => new(
        e.Id,
        e.OccurredAt,
        e.ActorObjectId,
        e.ActorName,
        e.EntityType,
        e.EntityId,
        e.Action,
        e.Changes == null ? null : JsonDocument.Parse(e.Changes).RootElement.Clone());
}

// Revisjonslogg for hele tenanten. Bare lesing: det finnes ingen endepunkter
// som endrer eller sletter hendelser.
[ApiController]
[Route("api/audit")]
[Authorize(Policy = AuthPolicies.HelpdeskAdmin)]
public class AuditController : ControllerBase
{
    private const int DefaultTake = 100;
    private const int MaxTake = 500;

    private readonly AppDbContext _db;

    public AuditController(AppDbContext db)
    {
        _db = db;
    }

    // Nyeste først. Bla videre med before = Id til siste hendelse på forrige side.
    [HttpGet]
    public async Task<ActionResult<IEnumerable<AuditEventDto>>> Get(
        [FromQuery] string? entityType,
        [FromQuery] string? entityId,
        [FromQuery] string? action,
        [FromQuery] long? before,
        [FromQuery] int take = DefaultTake)
    {
        take = Math.Clamp(take, 1, MaxTake);

        var query = _db.AuditEvents.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(entityType))
            query = query.Where(e => e.EntityType == entityType);

        if (!string.IsNullOrWhiteSpace(entityId))
            query = query.Where(e => e.EntityId == entityId);

        if (!string.IsNullOrWhiteSpace(action))
            query = query.Where(e => e.Action == action);

        if (before is long beforeId)
            query = query.Where(e => e.Id < beforeId);

        var events = await query
            .OrderByDescending(e => e.Id)
            .Take(take)
            .ToListAsync();

        return Ok(events.Select(AuditEventDto.From));
    }
}

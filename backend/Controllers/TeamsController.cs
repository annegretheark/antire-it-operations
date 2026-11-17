using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using backend.Data;
using backend.Models;
using backend.Services;

namespace backend.Controllers;

public record TeamsSettingsDto(
    bool Configured,
    // Bare starten og slutten av adressen vises; hele adressen sendes aldri tilbake
    string? MaskedWebhookUrl,
    bool NotifyCriticalManualTickets,
    IReadOnlyList<TeamsNotificationDto> RecentNotifications
);

public record TeamsNotificationDto(
    int Id,
    int? TicketId,
    string Kind,
    string Status,
    int Attempts,
    string? LastError,
    DateTime CreatedAt,
    DateTime? SentAt
);

public record UpdateTeamsSettingsRequest(
    // Ny adresse; null eller tom beholder den som er lagret
    string? WebhookUrl,
    bool NotifyCriticalManualTickets,
    // Fjern lagret adresse (slår av Teams-varsling)
    bool RemoveWebhook = false
);

// Teams-varsling for tenanten (Helpdesk.Admin). Webhook-adressen er en hemmelighet:
// den lagres kryptert og vises bare maskert.
[ApiController]
[Route("api/teams")]
[Authorize(Policy = AuthPolicies.HelpdeskAdmin)]
public class TeamsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly TeamsNotificationService _notifications;

    public TeamsController(AppDbContext db, TeamsNotificationService notifications)
    {
        _db = db;
        _notifications = notifications;
    }

    [HttpGet("settings")]
    public async Task<TeamsSettingsDto> Get()
    {
        var settings = await _notifications.GetSettingsAsync(HttpContext.RequestAborted);
        var url = _notifications.Decrypt(settings);

        var recent = await _db.TeamsNotifications
            .AsNoTracking()
            .OrderByDescending(n => n.Id)
            .Take(10)
            .Select(n => new TeamsNotificationDto(
                n.Id, n.TicketId, n.Kind, n.Status, n.Attempts, n.LastError, n.CreatedAt, n.SentAt))
            .ToListAsync();

        return new TeamsSettingsDto(
            url != null,
            url == null ? null : TeamsNotificationService.Mask(url),
            settings?.NotifyCriticalManualTickets ?? false,
            recent);
    }

    [HttpPut("settings")]
    public async Task<IActionResult> Update([FromBody] UpdateTeamsSettingsRequest request)
    {
        var newUrl = request.WebhookUrl?.Trim();

        if (!string.IsNullOrEmpty(newUrl) && !TeamsService.IsAllowedWebhookUrl(newUrl))
        {
            return BadRequest(
                "Adressen må være en https-adresse fra Teams Workflows (…logic.azure.com eller …powerplatform.com).");
        }

        var settings = await _notifications.GetSettingsAsync(HttpContext.RequestAborted);

        if (settings == null)
        {
            settings = new TeamsSettings();
            _db.TeamsSettings.Add(settings);
        }

        if (request.RemoveWebhook)
            settings.EncryptedWebhookUrl = null;
        else if (!string.IsNullOrEmpty(newUrl))
            settings.EncryptedWebhookUrl = _notifications.Encrypt(newUrl);

        settings.NotifyCriticalManualTickets = request.NotifyCriticalManualTickets;
        settings.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();

        return NoContent();
    }

    [HttpPost("test")]
    public async Task<IActionResult> SendTest()
    {
        try
        {
            await _notifications.SendTestAsync(HttpContext.RequestAborted);
            return Ok(new { message = "Testmeldingen er sendt. Sjekk kanalen i Teams." });
        }
        catch (TeamsDeliveryException e)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { message = e.Message });
        }
    }
}

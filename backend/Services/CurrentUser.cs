using Microsoft.Identity.Web;

namespace backend.Services;

// Innlogget person, slik revisjonsloggen og andre tjenester trenger den.
// Roller sjekkes fortsatt med User.IsHelpdeskAgent() osv. (AuthPolicies.cs).
public interface ICurrentUser
{
    // Entra objekt-ID (oid), eller null uten innlogget bruker
    string? ObjectId { get; }

    // Visningsnavn fra tokenet (name, deretter preferred_username)
    string? Name { get; }
}

public class HttpCurrentUser : ICurrentUser
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public HttpCurrentUser(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    // Satt bare av kode i backend (demodata i utviklingstenanten, C9), i et eget scope.
    // Kan aldri settes fra en forespørsel.
    private (string ObjectId, string Name)? _override;

    public void UseIdentity(string objectId, string name) => _override = (objectId.ToLowerInvariant(), name);

    public string? ObjectId =>
        _override?.ObjectId ?? _httpContextAccessor.HttpContext?.User.GetObjectId();

    public string? Name
    {
        get
        {
            if (_override is { } identity)
                return identity.Name;

            var user = _httpContextAccessor.HttpContext?.User;

            if (user == null)
                return null;

            return user.FindFirst("name")?.Value
                ?? user.FindFirst("preferred_username")?.Value
                ?? user.Identity?.Name;
        }
    }
}

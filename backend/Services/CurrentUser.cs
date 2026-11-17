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

    public string? ObjectId =>
        _httpContextAccessor.HttpContext?.User.GetObjectId();

    public string? Name
    {
        get
        {
            var user = _httpContextAccessor.HttpContext?.User;

            if (user == null)
                return null;

            return user.FindFirst("name")?.Value
                ?? user.FindFirst("preferred_username")?.Value
                ?? user.Identity?.Name;
        }
    }
}

using System.Collections.Concurrent;
using Azure.Identity;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Users.Item.CheckMemberGroups;
using Microsoft.Graph.Models.ODataErrors;

namespace backend.Services;

// Registrert som singleton: én Graph-klient (med token-cache) per tenant.
// Appen må ha admin-samtykke i hver tenant den brukes i.
public class GraphService
{
    // checkMemberGroups godtar maks 20 gruppe-ID-er per kall
    private const int CheckMemberGroupsBatchSize = 20;

    private readonly AppCredentials _credentials;
    private readonly ConcurrentDictionary<string, GraphServiceClient> _clients = new();

    // Innlogging med sertifikat, managed identity eller hemmelighet (se AppCredentials)
    public GraphService(IConfiguration configuration)
    {
        _credentials = new AppCredentials(configuration);
    }

    private GraphServiceClient ClientFor(string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new InvalidOperationException("Tenant mangler for Graph-kall.");

        return _clients.GetOrAdd(tenantId.ToLowerInvariant(), id =>
            new GraphServiceClient(
                _credentials.For(id),
                new[] { "https://graph.microsoft.com/.default" }));
    }

    // Henter alle brukere med avdeling og stilling (følger alle sider)
    public virtual async Task<List<User>> GetEmployeesAsync(
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        var client = ClientFor(tenantId);
        var users = new List<User>();

        var firstPage = await client.Users.GetAsync(request =>
        {
            request.QueryParameters.Select = new[]
            {
                "id",
                "displayName",
                "givenName",
                "surname",
                "userPrincipalName",
                "mail",
                "department",
                "jobTitle",
                "employeeId"
            };

            request.QueryParameters.Top = 999;
        }, cancellationToken);

        if (firstPage == null)
            return users;

        var pageIterator = PageIterator<User, UserCollectionResponse>
            .CreatePageIterator(
                client,
                firstPage,
                user =>
                {
                    if (!string.IsNullOrWhiteSpace(user.Department) &&
                        !string.IsNullOrWhiteSpace(user.JobTitle))
                    {
                        users.Add(user);
                    }

                    return true;
                });

        await pageIterator.IterateAsync(cancellationToken);

        return users;
    }

    // Alle grupper brukeren er medlem av, direkte eller indirekte
    public async Task<List<string>> GetUserGroupIdsAsync(
        string tenantId,
        string entraObjectId,
        CancellationToken cancellationToken = default)
    {
        var client = ClientFor(tenantId);
        var groupIds = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase
        );

        var firstPage = await client.Users[entraObjectId]
            .TransitiveMemberOf
            .GetAsync(cancellationToken: cancellationToken);

        if (firstPage == null)
            return groupIds.ToList();

        var pageIterator = PageIterator<DirectoryObject, DirectoryObjectCollectionResponse>
            .CreatePageIterator(
                client,
                firstPage,
                directoryObject =>
                {
                    if (directoryObject is Group group &&
                        !string.IsNullOrWhiteSpace(group.Id))
                    {
                        groupIds.Add(group.Id);
                    }

                    return true;
                });

        await pageIterator.IterateAsync(cancellationToken);

        return groupIds.ToList();
    }

    // Sjekker om brukeren er medlem (også indirekte) av minst én av gruppene.
    // Ett kall per 20 grupper i stedet for å liste alle brukerens grupper.
    // Virtual slik at testene kan bytte ut Graph.
    public virtual async Task<bool> IsMemberOfAnyGroupAsync(
        string tenantId,
        string entraObjectId,
        IReadOnlyCollection<string> groupIds,
        CancellationToken cancellationToken = default)
    {
        var client = ClientFor(tenantId);

        foreach (var batch in groupIds.Chunk(CheckMemberGroupsBatchSize))
        {
            var response = await client.Users[entraObjectId]
                .CheckMemberGroups
                .PostAsCheckMemberGroupsPostResponseAsync(
                    new CheckMemberGroupsPostRequestBody
                    {
                        GroupIds = batch.ToList()
                    },
                    cancellationToken: cancellationToken);

            if (response?.Value is { Count: > 0 })
                return true;
        }

        return false;
    }

    // ---------- Microsoft 365-oversikt (fase 2) ----------

    // Brukere med kontostatus og lisenser. Siste pålogging, MFA og lisensnavn
    // hentes for seg: mangler rettighet eller lisens, står årsaken i svaret.
    public virtual async Task<M365UserOverview> GetUserOverviewAsync(
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        var client = ClientFor(tenantId);
        var users = new List<User>();

        try
        {
            var page = await client.Users.GetAsync(request =>
            {
                request.QueryParameters.Select =
                [
                    "id", "displayName", "userPrincipalName", "mail", "accountEnabled",
                    "userType", "department", "jobTitle", "createdDateTime", "assignedLicenses",
                ];
                request.QueryParameters.Top = 999;
            }, cancellationToken);

            await CollectAsync<User, UserCollectionResponse>(client, page, users, cancellationToken);
        }
        catch (ODataError e)
        {
            throw Unavailable(e, "User.Read.All");
        }

        // Lisensnavn (skuPartNumber) for lisens-ID-ene på brukerne
        Dictionary<Guid, string> skuNames = [];
        string? licensesReason = null;

        try
        {
            var skus = await client.SubscribedSkus.GetAsync(cancellationToken: cancellationToken);
            skuNames = (skus?.Value ?? [])
                .Where(s => s.SkuId != null)
                .ToDictionary(s => s.SkuId!.Value, s => s.SkuPartNumber ?? s.SkuId!.Value.ToString());
        }
        catch (ODataError e)
        {
            licensesReason = Reason(e, "Organization.Read.All");
        }

        // Siste pålogging: krever AuditLog.Read.All og Entra ID P1
        Dictionary<string, SignInActivity?>? signIns = null;
        string? signInReason = null;

        try
        {
            var withSignIn = new List<User>();
            var page = await client.Users.GetAsync(request =>
            {
                request.QueryParameters.Select = ["id", "signInActivity"];
                // Med signInActivity er største sidestørrelse 120
                request.QueryParameters.Top = 120;
            }, cancellationToken);

            await CollectAsync<User, UserCollectionResponse>(client, page, withSignIn, cancellationToken);
            signIns = withSignIn
                .Where(u => u.Id != null)
                .ToDictionary(u => u.Id!, u => u.SignInActivity);
        }
        catch (ODataError e)
        {
            signInReason = Reason(e, "AuditLog.Read.All", "Entra ID P1");
        }

        // MFA-registrering: krever AuditLog.Read.All og Entra ID P1
        Dictionary<string, bool?>? mfa = null;
        string? mfaReason = null;

        try
        {
            var details = new List<UserRegistrationDetails>();
            var page = await client.Reports.AuthenticationMethods.UserRegistrationDetails
                .GetAsync(cancellationToken: cancellationToken);

            await CollectAsync<UserRegistrationDetails, UserRegistrationDetailsCollectionResponse>(
                client, page, details, cancellationToken);
            mfa = details
                .Where(d => d.Id != null)
                .ToDictionary(d => d.Id!, d => d.IsMfaRegistered);
        }
        catch (ODataError e)
        {
            mfaReason = Reason(e, "AuditLog.Read.All", "Entra ID P1");
        }

        var result = users
            .Where(u => u.Id != null)
            .Select(u => new M365User(
                u.Id!,
                u.DisplayName,
                u.UserPrincipalName,
                u.Mail,
                u.AccountEnabled,
                u.UserType,
                u.Department,
                u.JobTitle,
                u.CreatedDateTime,
                (u.AssignedLicenses ?? [])
                    .Where(l => l.SkuId != null)
                    .Select(l => skuNames.GetValueOrDefault(l.SkuId!.Value, l.SkuId!.Value.ToString()))
                    .ToList(),
                signIns?.GetValueOrDefault(u.Id!)?.LastSignInDateTime,
                mfa?.GetValueOrDefault(u.Id!),
                signIns?.GetValueOrDefault(u.Id!)?.LastSuccessfulSignInDateTime))
            .OrderBy(u => u.DisplayName)
            .ToList();

        return new M365UserOverview(result, signInReason, mfaReason, licensesReason, DateTimeOffset.UtcNow);
    }

    public virtual async Task<List<M365Group>> GetGroupsAsync(
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        var client = ClientFor(tenantId);
        var groups = new List<Group>();

        try
        {
            var page = await client.Groups.GetAsync(request =>
            {
                request.QueryParameters.Select =
                [
                    "id", "displayName", "description", "mail", "groupTypes",
                    "securityEnabled", "mailEnabled",
                ];
                request.QueryParameters.Top = 999;
            }, cancellationToken);

            await CollectAsync<Group, GroupCollectionResponse>(client, page, groups, cancellationToken);
        }
        catch (ODataError e)
        {
            throw Unavailable(e, "GroupMember.Read.All");
        }

        return groups
            .Where(g => g.Id != null)
            .Select(g =>
            {
                var types = g.GroupTypes ?? [];
                var kind = types.Contains("Unified") ? "microsoft365"
                    : g.SecurityEnabled == true && g.MailEnabled == true ? "mail_security"
                    : g.SecurityEnabled == true ? "security"
                    : "distribution";

                return new M365Group(
                    g.Id!, g.DisplayName, g.Description, g.Mail, kind,
                    types.Contains("DynamicMembership"));
            })
            .OrderBy(g => g.DisplayName)
            .ToList();
    }

    public virtual async Task<List<M365GroupMember>> GetGroupMembersAsync(
        string tenantId,
        string groupId,
        CancellationToken cancellationToken = default)
    {
        var client = ClientFor(tenantId);
        var members = new List<DirectoryObject>();

        try
        {
            var page = await client.Groups[groupId].Members.GetAsync(request =>
            {
                request.QueryParameters.Select = ["id", "displayName", "userPrincipalName"];
                request.QueryParameters.Top = 999;
            }, cancellationToken);

            await CollectAsync<DirectoryObject, DirectoryObjectCollectionResponse>(
                client, page, members, cancellationToken);
        }
        catch (ODataError e)
        {
            throw Unavailable(e, "GroupMember.Read.All");
        }

        return members
            .Where(m => m.Id != null)
            .Select(m => m switch
            {
                User u => new M365GroupMember(u.Id!, u.DisplayName, u.UserPrincipalName, "user"),
                Group g => new M365GroupMember(g.Id!, g.DisplayName, null, "group"),
                Device d => new M365GroupMember(d.Id!, d.DisplayName, null, "device"),
                _ => new M365GroupMember(m.Id!, null, null, "other"),
            })
            .OrderBy(m => m.DisplayName)
            .ToList();
    }

    // Legger brukeren i gruppen (krever GroupMember.ReadWrite.All).
    // Er brukeren allerede medlem, regnes det som vellykket.
    public virtual async Task<AddMemberResult> AddGroupMemberAsync(
        string tenantId,
        string groupId,
        string userObjectId,
        CancellationToken cancellationToken = default)
    {
        var client = ClientFor(tenantId);

        try
        {
            await client.Groups[groupId].Members.Ref.PostAsync(
                new ReferenceCreate
                {
                    OdataId = $"https://graph.microsoft.com/v1.0/directoryObjects/{userObjectId}",
                },
                cancellationToken: cancellationToken);

            return AddMemberResult.Added;
        }
        catch (ODataError e) when (
            e.ResponseStatusCode == 400 &&
            e.Error?.Message?.Contains("already exist", StringComparison.OrdinalIgnoreCase) == true)
        {
            return AddMemberResult.AlreadyMember;
        }
        catch (ODataError e)
        {
            throw Unavailable(e, "GroupMember.ReadWrite.All");
        }
    }

    // Fjerner brukeren fra gruppen (krever GroupMember.ReadWrite.All).
    // Er brukeren ikke medlem, regnes det som vellykket. Gir true hvis den ble fjernet.
    public virtual async Task<bool> RemoveGroupMemberAsync(
        string tenantId,
        string groupId,
        string userObjectId,
        CancellationToken cancellationToken = default)
    {
        var client = ClientFor(tenantId);

        try
        {
            await client.Groups[groupId].Members[userObjectId].Ref.DeleteAsync(cancellationToken: cancellationToken);
            return true;
        }
        catch (ODataError e) when (e.ResponseStatusCode == 404)
        {
            return false;
        }
        catch (ODataError e)
        {
            throw Unavailable(e, "GroupMember.ReadWrite.All");
        }
    }

    // ---------- Handlinger med innlogget persons rettigheter (on-behalf-of) ----------
    //
    // Appen bruker personens token, så Entra sjekker personens egne roller (f.eks.
    // User Administrator). Appen trenger bare de delegerte rettighetene med admin-samtykke.

    // Graph-klient for én forespørsel på vegne av personen
    private GraphServiceClient DelegatedClient(string tenantId, string userToken) =>
        new(_credentials.OnBehalfOf(tenantId, userToken), ["https://graph.microsoft.com/.default"]);

    // Deaktiverer eller aktiverer kontoen (delegert User.EnableDisableAccount.All)
    public virtual async Task SetAccountEnabledAsync(
        string tenantId,
        string userToken,
        string userId,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await DelegatedClient(tenantId, userToken).Users[userId]
                .PatchAsync(new User { AccountEnabled = enabled }, cancellationToken: cancellationToken);
        }
        catch (ODataError e)
        {
            throw Denied(e);
        }
        catch (AuthenticationFailedException e)
        {
            throw NoConsent(e, "User.EnableDisableAccount.All");
        }
    }

    // Sletter brukeren (delegert User.DeleteRestore.All). Kan gjenopprettes i Entra i 30 dager.
    public virtual async Task DeleteUserAsync(
        string tenantId,
        string userToken,
        string userId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await DelegatedClient(tenantId, userToken).Users[userId]
                .DeleteAsync(cancellationToken: cancellationToken);
        }
        catch (ODataError e)
        {
            throw Denied(e);
        }
        catch (AuthenticationFailedException e)
        {
            throw NoConsent(e, "User.DeleteRestore.All");
        }
    }

    private static GraphUnavailableException Denied(ODataError e) =>
        new(e.ResponseStatusCode switch
        {
            401 or 403 => "Du har ikke lov til dette i Entra ID. Det krever en rolle som User Administrator " +
                          "(Privileged Authentication Administrator for administratorer).",
            404 => "Fant ikke brukeren i Entra ID.",
            _ => $"Microsoft Graph svarte med feil: {e.Error?.Message ?? e.Message}",
        }, e.ResponseStatusCode, e);

    private static GraphUnavailableException NoConsent(AuthenticationFailedException e, string permission) =>
        new($"Kunne ikke logge inn på dine vegne. Appen trenger den delegerte rettigheten {permission} " +
            "med admin-samtykke (scripts/Grant-GraphPermissions.ps1 -IncludeUserAdmin).", null, e);

    // Følger alle sider i et Graph-svar
    private static async Task CollectAsync<TItem, TPage>(
        GraphServiceClient client,
        TPage? firstPage,
        List<TItem> items,
        CancellationToken cancellationToken)
        where TPage : Microsoft.Kiota.Abstractions.Serialization.IParsable,
            Microsoft.Kiota.Abstractions.Serialization.IAdditionalDataHolder, new()
    {
        if (firstPage == null)
            return;

        var iterator = PageIterator<TItem, TPage>.CreatePageIterator(client, firstPage, item =>
        {
            items.Add(item);
            return true;
        });

        await iterator.IterateAsync(cancellationToken);
    }

    // Kort forklaring når en valgfri del av oversikten mangler
    private static string Reason(ODataError e, string permission, string? license = null) =>
        e.ResponseStatusCode is 401 or 403
            ? license == null
                ? $"Krever rettigheten {permission}."
                : $"Krever rettigheten {permission} og lisensen {license}."
            : $"Microsoft Graph svarte med feil: {e.Error?.Message ?? e.Message}";

    private static GraphUnavailableException Unavailable(ODataError e, string permission) =>
        new(e.ResponseStatusCode switch
        {
            401 or 403 => $"Appen mangler rettigheten {permission} i Entra ID (admin-samtykke).",
            404 => "Fant ikke gruppen eller brukeren i Entra ID.",
            _ => $"Microsoft Graph svarte med feil: {e.Error?.Message ?? e.Message}",
        }, e.ResponseStatusCode, e);

    // ---------- Microsoft Defender (fase 4.3) ----------

    // Varsler fra Graph Security API (alerts_v2) som er endret siden «since».
    // Krever SecurityAlert.Read.All og Defender-lisens i tenanten.
    public virtual async Task<List<AlertSignal>> GetDefenderAlertsAsync(
        string tenantId,
        DateTimeOffset since,
        CancellationToken cancellationToken = default)
    {
        var client = ClientFor(tenantId);
        var alerts = new List<Microsoft.Graph.Models.Security.Alert>();

        try
        {
            var page = await client.Security.Alerts_v2.GetAsync(request =>
            {
                request.QueryParameters.Filter =
                    $"lastUpdateDateTime ge {since.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}";
                request.QueryParameters.Top = 500;
            }, cancellationToken);

            await CollectAsync<Microsoft.Graph.Models.Security.Alert,
                Microsoft.Graph.Models.Security.AlertCollectionResponse>(client, page, alerts, cancellationToken);
        }
        catch (ODataError e)
        {
            throw Unavailable(e, "SecurityAlert.Read.All");
        }

        return alerts
            .Where(a => !string.IsNullOrEmpty(a.Id))
            .Select(a =>
            {
                var severity = a.Severity switch
                {
                    Microsoft.Graph.Models.Security.AlertSeverity.High => "high",
                    Microsoft.Graph.Models.Security.AlertSeverity.Medium => "medium",
                    Microsoft.Graph.Models.Security.AlertSeverity.Low => "low",
                    _ => "informational",
                };

                var firedAt = (a.LastActivityDateTime ?? a.CreatedDateTime ?? DateTimeOffset.UtcNow).UtcDateTime;

                var raw = System.Text.Json.JsonSerializer.Serialize(new
                {
                    a.Id,
                    a.Title,
                    severity,
                    status = a.Status?.ToString(),
                    category = a.Category,
                    serviceSource = a.ServiceSource?.ToString(),
                    a.CreatedDateTime,
                    a.LastActivityDateTime,
                    a.AlertWebUrl,
                });

                return new AlertSignal(
                    backend.Models.AlertSources.Defender,
                    a.Id!,
                    a.Title ?? "Defender-varsel",
                    severity,
                    null,
                    a.AlertWebUrl,
                    a.Status == Microsoft.Graph.Models.Security.AlertStatus.Resolved
                        ? backend.Models.AlertStates.Resolved
                        : backend.Models.AlertStates.Fired,
                    firedAt,
                    raw);
            })
            .ToList();
    }
}

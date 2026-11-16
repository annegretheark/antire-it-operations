using System.Collections.Concurrent;
using Azure.Identity;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Users.Item.CheckMemberGroups;

namespace backend.Services;

// Registrert som singleton: én Graph-klient (med token-cache) per tenant.
// Appen må ha admin-samtykke i hver tenant den brukes i.
public class GraphService
{
    // checkMemberGroups godtar maks 20 gruppe-ID-er per kall
    private const int CheckMemberGroupsBatchSize = 20;

    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly ConcurrentDictionary<string, GraphServiceClient> _clients = new();

    public GraphService(IConfiguration configuration)
    {
        _clientId = configuration["MicrosoftGraph:ClientId"]
            ?? throw new InvalidOperationException("ClientId mangler");

        _clientSecret = configuration["MicrosoftGraph:ClientSecret"]
            ?? throw new InvalidOperationException("ClientSecret mangler");
    }

    private GraphServiceClient ClientFor(string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new InvalidOperationException("Tenant mangler for Graph-kall.");

        return _clients.GetOrAdd(tenantId.ToLowerInvariant(), id =>
            new GraphServiceClient(
                new ClientSecretCredential(id, _clientId, _clientSecret),
                new[] { "https://graph.microsoft.com/.default" }));
    }

    // Henter alle brukere med avdeling og stilling (følger alle sider)
    public async Task<List<User>> GetEmployeesAsync(
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
    public async Task<bool> IsMemberOfAnyGroupAsync(
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
}

using Azure.Identity;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Users.Item.CheckMemberGroups;

namespace backend.Services;

// Registrert som singleton: én klient og token-cache for hele applikasjonen
public class GraphService
{
    // checkMemberGroups godtar maks 20 gruppe-ID-er per kall
    private const int CheckMemberGroupsBatchSize = 20;

    private readonly GraphServiceClient _graphClient;

    public GraphService(IConfiguration configuration)
    {
        var tenantId = configuration["MicrosoftGraph:TenantId"]
            ?? throw new InvalidOperationException("TenantId mangler");

        var clientId = configuration["MicrosoftGraph:ClientId"]
            ?? throw new InvalidOperationException("ClientId mangler");

        var clientSecret = configuration["MicrosoftGraph:ClientSecret"]
            ?? throw new InvalidOperationException("ClientSecret mangler");

        var credential = new ClientSecretCredential(
            tenantId,
            clientId,
            clientSecret
        );

        _graphClient = new GraphServiceClient(
            credential,
            new[] { "https://graph.microsoft.com/.default" }
        );
    }

    // Henter alle brukere med avdeling og stilling (følger alle sider)
    public async Task<List<User>> GetEmployeesAsync(
        CancellationToken cancellationToken = default)
    {
        var users = new List<User>();

        var firstPage = await _graphClient.Users.GetAsync(request =>
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
                _graphClient,
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
        string entraObjectId,
        CancellationToken cancellationToken = default)
    {
        var groupIds = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase
        );

        var firstPage = await _graphClient.Users[entraObjectId]
            .TransitiveMemberOf
            .GetAsync(cancellationToken: cancellationToken);

        if (firstPage == null)
            return groupIds.ToList();

        var pageIterator = PageIterator<DirectoryObject, DirectoryObjectCollectionResponse>
            .CreatePageIterator(
                _graphClient,
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
        string entraObjectId,
        IReadOnlyCollection<string> groupIds,
        CancellationToken cancellationToken = default)
    {
        foreach (var batch in groupIds.Chunk(CheckMemberGroupsBatchSize))
        {
            var response = await _graphClient.Users[entraObjectId]
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


using Azure.Identity;
using Microsoft.Graph;
using Microsoft.Graph.Models;

namespace backend.Services;

public class GraphService
{
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

    public async Task<List<User>> GetEmployeesAsync()
    {
        var users = new List<User>();

        var response = await _graphClient.Users.GetAsync(request =>
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

            request.QueryParameters.Top = 100;
        });

        if (response?.Value != null)
{
    users.AddRange(
        response.Value.Where(user =>
            !string.IsNullOrWhiteSpace(user.Department) &&
            !string.IsNullOrWhiteSpace(user.JobTitle)
        )
    );
}
        return users;
    }

  public async Task<List<string>> GetUserGroupIdsAsync(string entraObjectId)
{
    var groupIds = new HashSet<string>(
        StringComparer.OrdinalIgnoreCase
    );

    var response = await _graphClient.Users[entraObjectId]
        .TransitiveMemberOf
        .GetAsync();

    while (response != null)
    {
        if (response.Value != null)
        {
            foreach (var group in response.Value.OfType<Group>())
            {
                if (!string.IsNullOrWhiteSpace(group.Id))
                {
                    groupIds.Add(group.Id);
                }
            }
        }

        if (string.IsNullOrEmpty(response.OdataNextLink))
            break;

        response = await _graphClient.Users[entraObjectId]
            .TransitiveMemberOf
            .WithUrl(response.OdataNextLink)
            .GetAsync();
    }

    return groupIds.ToList();
}

}

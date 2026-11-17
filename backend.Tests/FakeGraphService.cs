using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using backend.Services;

namespace backend.Tests;

// GraphService uten Microsoft Graph, for tester. Hver testklasse har sin egen
// fabrikk og dermed sin egen instans.
public class FakeGraphService(IConfiguration configuration) : GraphService(configuration)
{
    // Entra objekt-ID-er som regnes som medlem av alle grupper (automatisk tildeling)
    public HashSet<string> Members { get; } = [];

    public M365UserOverview UserOverview { get; set; } =
        new([], null, null, null, DateTimeOffset.UtcNow);

    public List<M365Group> Groups { get; } = [];

    public Dictionary<string, List<M365GroupMember>> GroupMembers { get; } = [];

    // Settes for å la neste kall til Graph feile
    public Exception? Failure { get; set; }

    // (gruppe, bruker) som allerede er medlem
    public HashSet<(string GroupId, string UserId)> ExistingMemberships { get; } = [];

    // Alle vellykkede kall til AddGroupMemberAsync: (tenant, gruppe, bruker)
    public ConcurrentQueue<(string TenantId, string GroupId, string UserId)> AddedMembers { get; } = new();

    public int UserOverviewCalls;

    public override Task<bool> IsMemberOfAnyGroupAsync(
        string tenantId, string entraObjectId, IReadOnlyCollection<string> groupIds,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Members.Contains(entraObjectId));

    public override Task<M365UserOverview> GetUserOverviewAsync(
        string tenantId, CancellationToken cancellationToken = default)
    {
        ThrowIfFailing();
        Interlocked.Increment(ref UserOverviewCalls);
        return Task.FromResult(UserOverview);
    }

    public override Task<List<M365Group>> GetGroupsAsync(
        string tenantId, CancellationToken cancellationToken = default)
    {
        ThrowIfFailing();
        return Task.FromResult(Groups.ToList());
    }

    public override Task<List<M365GroupMember>> GetGroupMembersAsync(
        string tenantId, string groupId, CancellationToken cancellationToken = default)
    {
        ThrowIfFailing();
        return Task.FromResult(GroupMembers.GetValueOrDefault(groupId) ?? []);
    }

    public override Task<AddMemberResult> AddGroupMemberAsync(
        string tenantId, string groupId, string userObjectId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfFailing();

        if (ExistingMemberships.Contains((groupId, userObjectId)))
            return Task.FromResult(AddMemberResult.AlreadyMember);

        AddedMembers.Enqueue((tenantId, groupId, userObjectId));
        return Task.FromResult(AddMemberResult.Added);
    }

    private void ThrowIfFailing()
    {
        if (Failure is { } failure)
            throw failure;
    }
}

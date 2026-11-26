using Microsoft.EntityFrameworkCore;
using backend.Data;

namespace backend.Services;

// Løpenumre for endringer (CHG-000001) og risikoer (RISK-000001) fra
// PostgreSQL-sekvensene change_number_seq og risk_number_seq.
public class GovernanceNumberService
{
    public const string ChangePrefix = "CHG-";
    public const string RiskPrefix = "RISK-";

    private readonly AppDbContext _db;

    public GovernanceNumberService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<string> NextChangeNumberAsync(CancellationToken cancellationToken = default) =>
        Format(ChangePrefix, await NextAsync("change_number_seq", cancellationToken));

    public async Task<string> NextRiskNumberAsync(CancellationToken cancellationToken = default) =>
        Format(RiskPrefix, await NextAsync("risk_number_seq", cancellationToken));

    // «CHG-» + 6 siffer (flere siffer etter 999999)
    public static string Format(string prefix, long value) => $"{prefix}{value:D6}";

    // Virtual slik at SQLite-testene (uten sekvenser) kan bruke en teller
    protected virtual async Task<long> NextAsync(string sequence, CancellationToken cancellationToken)
    {
        // Sekvensnavnet kommer bare fra konstantene over, aldri fra brukeren
        return sequence switch
        {
            "change_number_seq" => await _db.Database
                .SqlQuery<long>($"SELECT nextval('change_number_seq') AS \"Value\"")
                .SingleAsync(cancellationToken),
            "risk_number_seq" => await _db.Database
                .SqlQuery<long>($"SELECT nextval('risk_number_seq') AS \"Value\"")
                .SingleAsync(cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(sequence)),
        };
    }
}

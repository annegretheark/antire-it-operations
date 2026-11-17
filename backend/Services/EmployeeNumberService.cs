using backend.Data;
using Microsoft.EntityFrameworkCore;

namespace backend.Services;

// Tildeler unike ansattnummer fra PostgreSQL-sekvensen employee_number_seq
public class EmployeeNumberService
{
    private readonly AppDbContext _db;

    public EmployeeNumberService(AppDbContext db)
    {
        _db = db;
    }

    // Virtual slik at SQLite-testene (uten sekvenser) kan bruke en teller
    public virtual async Task<string> NextAsync(CancellationToken cancellationToken = default)
    {
        var next = await _db.Database
            .SqlQuery<long>($"SELECT nextval('employee_number_seq') AS \"Value\"")
            .SingleAsync(cancellationToken);

        return next.ToString();
    }
}

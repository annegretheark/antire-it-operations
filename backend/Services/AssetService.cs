using System.Globalization;
using Microsoft.EntityFrameworkCore;
using backend.Data;
using backend.Models;

namespace backend.Services;

// Felter for å opprette eller endre utstyr (skjema og CSV-import)
public record AssetFields(
    string? AssetTag,
    string? Type,
    string? Manufacturer,
    string? Model,
    string? SerialNumber,
    string? Status,
    int? AssignedToEmployeeId,
    DateTime? PurchaseDate,
    DateTime? WarrantyEndsAt,
    string? Notes
);

public record AssetImportResult(int Created, int Updated, IReadOnlyList<string> Errors);

// Regler for IT-utstyr, felles for skjema og CSV-import
public class AssetService
{
    public const int MaxImportRows = 5000;

    private readonly AppDbContext _db;

    public AssetService(AppDbContext db)
    {
        _db = db;
    }

    // Validerer og kopierer feltene. Gir en feilmelding, eller null når alt er i orden.
    // Tildelt utstyr er i bruk; utstyr som ikke er tildelt kan ikke være i bruk.
    public async Task<string?> ApplyAsync(Asset asset, AssetFields fields)
    {
        var tag = fields.AssetTag?.Trim();
        var type = fields.Type?.Trim() ?? AssetTypes.Other;
        var status = fields.Status?.Trim();

        if (string.IsNullOrWhiteSpace(tag) || tag.Length > 50)
            return "Utstyrsnummer er påkrevd (maks 50 tegn).";

        if (!AssetTypes.All.Contains(type))
            return $"Ugyldig type «{type}».";

        if (status != null && !AssetStatuses.All.Contains(status))
            return $"Ugyldig status «{status}».";

        if (Longer(fields.Manufacturer, 100) || Longer(fields.Model, 100) ||
            Longer(fields.SerialNumber, 100) || Longer(fields.Notes, 2000))
        {
            return "Produsent, modell og serienummer kan ha maks 100 tegn, notater maks 2000.";
        }

        if (fields.PurchaseDate is { } bought && fields.WarrantyEndsAt is { } warranty && warranty < bought)
            return "Garantien kan ikke slutte før kjøpsdatoen.";

        var assigneeChanged = fields.AssignedToEmployeeId != asset.AssignedToEmployeeId;

        if (fields.AssignedToEmployeeId is int employeeId && assigneeChanged)
        {
            var isActive = await _db.Employees.AnyAsync(e => e.Id == employeeId && e.Status == "Aktiv");

            if (!isActive)
                return "Utstyr kan bare tildeles aktive ansatte.";
        }

        status ??= asset.Id == 0 ? AssetStatuses.InStock : asset.Status;

        if (fields.AssignedToEmployeeId != null)
        {
            if (status == AssetStatuses.Retired)
                return "Utstyr som er tildelt en ansatt, kan ikke kasseres. Registrer innlevering først.";

            if (status == AssetStatuses.InStock)
                status = AssetStatuses.InUse;
        }
        else if (status == AssetStatuses.InUse)
        {
            status = AssetStatuses.InStock;
        }

        asset.AssetTag = tag;
        asset.Type = type;
        asset.Manufacturer = Clean(fields.Manufacturer);
        asset.Model = Clean(fields.Model);
        asset.SerialNumber = Clean(fields.SerialNumber);
        asset.Status = status;
        asset.AssignedToEmployeeId = fields.AssignedToEmployeeId;
        asset.PurchaseDate = AsUtcDate(fields.PurchaseDate);
        asset.WarrantyEndsAt = AsUtcDate(fields.WarrantyEndsAt);
        asset.Notes = Clean(fields.Notes);

        return null;
    }

    // Utstyrsnummer sammenlignes uten hensyn til store og små bokstaver
    public Task<bool> TagIsTakenAsync(string tag, int exceptId) =>
        _db.Assets.AnyAsync(a => a.AssetTag.ToLower() == tag.Trim().ToLower() && a.Id != exceptId);

    // Importerer CSV. Finnes utstyrsnummeret fra før, oppdateres utstyret med
    // kolonnene som er med i filen. Én feil stopper hele importen.
    public async Task<AssetImportResult> ImportAsync(string csv)
    {
        var rows = CsvReader.Read(csv);
        var errors = new List<string>();

        if (rows.Count == 0)
            return new(0, 0, ["Filen har ingen rader under overskriftene."]);

        if (rows.Count > MaxImportRows)
            return new(0, 0, [$"Maks {MaxImportRows} rader per import."]);

        var columns = rows[0].Values.Keys
            .Select(k => (Header: k, Field: ColumnFor(k)))
            .ToList();

        var unknown = columns.Where(c => c.Field == null).Select(c => c.Header).ToList();

        if (unknown.Count > 0)
            return new(0, 0, [$"Ukjente kolonner: {string.Join(", ", unknown)}."]);

        if (!columns.Any(c => c.Field == Column.AssetTag))
            return new(0, 0, ["Kolonnen Utstyrsnummer (AssetTag) mangler."]);

        var existing = await _db.Assets.ToDictionaryAsync(a => a.AssetTag, StringComparer.OrdinalIgnoreCase);
        var employees = await _db.Employees
            .Where(e => e.Status == "Aktiv")
            .Select(e => new { e.Id, e.Email })
            .ToListAsync();
        var employeeByEmail = employees
            .GroupBy(e => e.Email.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);

        var seenTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var created = 0;
        var updated = 0;

        foreach (var (rowNumber, values) in rows)
        {
            string? Get(Column column) =>
                columns.FirstOrDefault(c => c.Field == column) is { Header: { } header } &&
                values.TryGetValue(header, out var value)
                    ? value
                    : null;

            bool Has(Column column) => columns.Any(c => c.Field == column);

            var tag = Get(Column.AssetTag)?.Trim() ?? "";

            if (tag.Length > 0 && !seenTags.Add(tag))
            {
                errors.Add($"Rad {rowNumber}: utstyrsnummer {tag} står flere ganger i filen.");
                continue;
            }

            var isNew = !existing.TryGetValue(tag, out var asset);
            asset ??= new Asset();

            var rowErrors = new List<string>();

            string? Code(Column column, Func<string, string?> map, string current, string label)
            {
                if (!Has(column))
                    return current;

                var raw = Get(column);

                if (string.IsNullOrWhiteSpace(raw))
                    return isNew ? null : current;

                var code = map(raw);

                if (code == null)
                    rowErrors.Add($"ugyldig {label} «{raw}»");

                return code;
            }

            DateTime? Date(Column column, DateTime? current, string label)
            {
                if (!Has(column))
                    return current;

                var raw = Get(column);

                if (string.IsNullOrWhiteSpace(raw))
                    return null;

                if (ParseDate(raw) is { } date)
                    return date;

                rowErrors.Add($"ugyldig {label} «{raw}» (bruk åååå-mm-dd eller dd.mm.åååå)");
                return current;
            }

            string? Text(Column column, string? current) => Has(column) ? Get(column) : current;

            var assignee = asset.AssignedToEmployeeId;

            if (Has(Column.AssignedToEmail))
            {
                var email = Get(Column.AssignedToEmail)?.Trim();

                if (string.IsNullOrEmpty(email))
                    assignee = null;
                else if (employeeByEmail.TryGetValue(email, out var employeeId))
                    assignee = employeeId;
                else
                    rowErrors.Add($"fant ingen aktiv ansatt med e-post {email}");
            }

            var fields = new AssetFields(
                tag,
                Code(Column.Type, MapType, asset.Type, "type") ?? AssetTypes.Other,
                Text(Column.Manufacturer, asset.Manufacturer),
                Text(Column.Model, asset.Model),
                Text(Column.SerialNumber, asset.SerialNumber),
                Code(Column.Status, MapStatus, asset.Status, "status"),
                assignee,
                Date(Column.PurchaseDate, asset.PurchaseDate, "kjøpsdato"),
                Date(Column.WarrantyEndsAt, asset.WarrantyEndsAt, "garantidato"),
                Text(Column.Notes, asset.Notes));

            if (rowErrors.Count == 0 && await ApplyAsync(asset, fields) is { } error)
                rowErrors.Add(error.TrimEnd('.'));

            if (rowErrors.Count > 0)
            {
                errors.Add($"Rad {rowNumber}: {string.Join("; ", rowErrors)}.");
                continue;
            }

            if (isNew)
            {
                _db.Assets.Add(asset);
                created++;
            }
            else if (_db.Entry(asset).State == EntityState.Modified)
            {
                updated++;
            }
        }

        if (errors.Count > 0)
        {
            // Ingenting lagres når én rad er feil
            _db.ChangeTracker.Clear();
            return new(0, 0, errors.Take(50).ToList());
        }

        await _db.SaveChangesAsync();
        return new(created, updated, []);
    }

    private enum Column
    {
        AssetTag, Type, Manufacturer, Model, SerialNumber, Status,
        AssignedToEmail, PurchaseDate, WarrantyEndsAt, Notes,
    }

    // Overskrifter på engelsk (feltnavn) eller norsk, uten hensyn til store
    // bokstaver, mellomrom og bindestrek
    private static readonly Dictionary<string, Column> ColumnNames = new()
    {
        ["assettag"] = Column.AssetTag, ["utstyrsnummer"] = Column.AssetTag,
        ["type"] = Column.Type,
        ["manufacturer"] = Column.Manufacturer, ["produsent"] = Column.Manufacturer,
        ["model"] = Column.Model, ["modell"] = Column.Model,
        ["serialnumber"] = Column.SerialNumber, ["serienummer"] = Column.SerialNumber,
        ["status"] = Column.Status,
        ["assignedtoemail"] = Column.AssignedToEmail, ["ansatt"] = Column.AssignedToEmail,
        ["ansattepost"] = Column.AssignedToEmail, ["epost"] = Column.AssignedToEmail,
        ["purchasedate"] = Column.PurchaseDate, ["kjøpsdato"] = Column.PurchaseDate,
        ["warrantyendsat"] = Column.WarrantyEndsAt, ["garantitil"] = Column.WarrantyEndsAt,
        ["notes"] = Column.Notes, ["notater"] = Column.Notes,
    };

    private static Column? ColumnFor(string header)
    {
        var key = new string(header.ToLowerInvariant().Where(char.IsLetter).ToArray());
        return ColumnNames.TryGetValue(key, out var column) ? column : null;
    }

    private static readonly Dictionary<string, string> TypeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["bærbar pc"] = AssetTypes.Laptop, ["bærbar"] = AssetTypes.Laptop,
        ["stasjonær pc"] = AssetTypes.Desktop, ["stasjonær"] = AssetTypes.Desktop,
        ["mobil"] = AssetTypes.Mobile, ["mobiltelefon"] = AssetTypes.Mobile,
        ["skjerm"] = AssetTypes.Monitor, ["annet"] = AssetTypes.Other,
    };

    private static readonly Dictionary<string, string> StatusNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["på lager"] = AssetStatuses.InStock, ["i bruk"] = AssetStatuses.InUse,
        ["til reparasjon"] = AssetStatuses.Repair, ["reparasjon"] = AssetStatuses.Repair,
        ["kassert"] = AssetStatuses.Retired, ["utrangert"] = AssetStatuses.Retired,
    };

    private static string? MapType(string raw) =>
        AssetTypes.All.FirstOrDefault(t => t.Equals(raw.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? TypeNames.GetValueOrDefault(raw.Trim());

    private static string? MapStatus(string raw) =>
        AssetStatuses.All.FirstOrDefault(s => s.Equals(raw.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? StatusNames.GetValueOrDefault(raw.Trim());

    private static readonly string[] DateFormats = ["yyyy-MM-dd", "dd.MM.yyyy", "d.M.yyyy"];

    private static DateTime? ParseDate(string raw) =>
        DateTime.TryParseExact(raw.Trim(), DateFormats, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var date)
            ? DateTime.SpecifyKind(date, DateTimeKind.Utc)
            : null;

    private static DateTime? AsUtcDate(DateTime? value) =>
        value is { } v ? DateTime.SpecifyKind(v.Date, DateTimeKind.Utc) : null;

    private static bool Longer(string? value, int max) => value?.Trim().Length > max;

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

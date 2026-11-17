using System.Text;

namespace backend.Services;

// Leser CSV slik Excel lager den: semikolon (norsk Excel) eller komma som
// skilletegn, felt i anførselstegn kan inneholde skilletegn, linjeskift og "".
public static class CsvReader
{
    // Første rad er overskrifter. Gir radene som ordbøker (overskrift → verdi),
    // med radnummer slik brukeren ser det i regnearket (overskriften er rad 1).
    public static List<(int Row, Dictionary<string, string> Values)> Read(string csv)
    {
        var rows = Parse(csv.TrimStart('﻿'));

        if (rows.Count == 0)
            return [];

        var headers = rows[0].Select(h => h.Trim()).ToList();
        var result = new List<(int, Dictionary<string, string>)>();

        for (var i = 1; i < rows.Count; i++)
        {
            var row = rows[i];

            // Hopp over tomme linjer
            if (row.All(string.IsNullOrWhiteSpace))
                continue;

            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            for (var c = 0; c < headers.Count; c++)
            {
                if (headers[c].Length > 0)
                    values[headers[c]] = c < row.Count ? row[c].Trim() : string.Empty;
            }

            result.Add((i + 1, values));
        }

        return result;
    }

    private static char DetectDelimiter(string csv)
    {
        var firstLine = csv.Split('\n', 2)[0];
        return firstLine.Count(c => c == ';') >= firstLine.Count(c => c == ',') ? ';' : ',';
    }

    private static List<List<string>> Parse(string csv)
    {
        var delimiter = DetectDelimiter(csv);
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < csv.Length; i++)
        {
            var ch = csv[i];

            if (inQuotes)
            {
                if (ch == '"' && i + 1 < csv.Length && csv[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (ch == '"')
                {
                    inQuotes = false;
                }
                else
                {
                    field.Append(ch);
                }
            }
            else if (ch == '"')
            {
                inQuotes = true;
            }
            else if (ch == delimiter)
            {
                row.Add(field.ToString());
                field.Clear();
            }
            else if (ch == '\n' || ch == '\r')
            {
                if (ch == '\r' && i + 1 < csv.Length && csv[i + 1] == '\n')
                    i++;

                row.Add(field.ToString());
                field.Clear();
                rows.Add(row);
                row = [];
            }
            else
            {
                field.Append(ch);
            }
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }

        return rows;
    }
}

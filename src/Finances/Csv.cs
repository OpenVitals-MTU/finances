using System.Text;

namespace Finances;

/// <summary>Just enough CSV: quoted fields, doubled quotes, one record per line.</summary>
public static class Csv
{
    public static List<string[]> Read(string path, string[] expectedHeader)
    {
        var lines = File.ReadAllLines(path, Encoding.UTF8).Where(l => l.Length > 0).ToList();
        if (lines.Count == 0 || !ParseLine(lines[0]).SequenceEqual(expectedHeader))
            throw new DataException($"{path}: header must be {string.Join(",", expectedHeader)}");
        var rows = new List<string[]>();
        for (var i = 1; i < lines.Count; i++)
        {
            var fields = ParseLine(lines[i]);
            if (fields.Length != expectedHeader.Length)
                throw new DataException($"{path}:{i + 1}: expected {expectedHeader.Length} fields, found {fields.Length}");
            rows.Add(fields);
        }
        return rows;
    }

    public static void Write(string path, string[] header, IEnumerable<string[]> rows)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Format(header, rows), new UTF8Encoding(false));
    }

    public static string Format(string[] header, IEnumerable<string[]> rows)
    {
        var text = new StringBuilder();
        text.Append(string.Join(",", header.Select(Quote))).Append('\n');
        foreach (var row in rows)
            text.Append(string.Join(",", row.Select(Quote))).Append('\n');
        return text.ToString();
    }

    private static string Quote(string field) =>
        field.AsSpan().IndexOfAny(",\"\n\r") >= 0 ? $"\"{field.Replace("\"", "\"\"")}\"" : field;

    public static string[] ParseLine(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else current.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { fields.Add(current.ToString()); current.Clear(); }
            else current.Append(c);
        }
        fields.Add(current.ToString());
        return fields.ToArray();
    }
}

using System.Text.Json;

namespace Finances;

/// <summary>
/// One flat key/value namespace, the same names everywhere. Precedence, highest first:
/// environment variables (the pipeline), appsettings.local.json (gitignored, a laptop),
/// appsettings.json (committed defaults). Both files sit at the repository root.
/// </summary>
public sealed class Settings
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public static Settings Load(string root)
    {
        var settings = new Settings();
        foreach (var file in new[] { "appsettings.json", "appsettings.local.json" })
        {
            var path = Path.Combine(root, file);
            if (!File.Exists(path)) continue;
            using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                var value = property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString()!,
                    JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => property.Value.GetRawText(),
                    JsonValueKind.Null => "",
                    _ => throw new DataException($"{path}: '{property.Name}' must be a string, number or boolean"),
                };
                settings._values[property.Name] = value;
            }
        }
        return settings;
    }

    /// <summary>The value, or null when unset or blank.</summary>
    public string? Get(string name)
    {
        var fromEnvironment = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrWhiteSpace(fromEnvironment)) return fromEnvironment.Trim();
        return _values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
    }

    public string Get(string name, string fallback) => Get(name) ?? fallback;

    public string Require(string name) =>
        Get(name) ?? throw new DataException($"{name} is not set; put it in appsettings.local.json or export it as an environment variable");
}

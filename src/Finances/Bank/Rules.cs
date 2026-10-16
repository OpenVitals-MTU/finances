using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Finances.Bank;

public sealed record Rule(string Name, Regex Match, string Kind, string Source, string Category, string Payee, string Description, string PaidFrom);

public sealed record Classified(BankTransaction Transaction, Rule? Rule, string Kind);

/// <summary>Turns bank transactions into income, expense or transfer rows with config/rules.json.</summary>
public static class Rules
{
    public const int PayoutWindowDays = 6;
    private static readonly string[] Kinds = ["income", "expense", "transfer"];

    private sealed class RuleDto
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("match")] public string? Match { get; set; }
        [JsonPropertyName("kind")] public string? Kind { get; set; }
        [JsonPropertyName("source")] public string? Source { get; set; }
        [JsonPropertyName("category")] public string? Category { get; set; }
        [JsonPropertyName("payee")] public string? Payee { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("paidFrom")] public string? PaidFrom { get; set; }
    }

    private sealed class RulesDto
    {
        [JsonPropertyName("rules")] public List<RuleDto> Rules { get; set; } = [];
    }

    public static List<Rule> Load(string path)
    {
        var options = new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        var doc = JsonSerializer.Deserialize<RulesDto>(File.ReadAllText(path), options) ?? new RulesDto();
        var rules = new List<Rule>();
        foreach (var (raw, number) in doc.Rules.Select((r, i) => (r, i + 1)))
        {
            var where = $"{path} rule {number}";
            if (string.IsNullOrWhiteSpace(raw.Match)) throw new DataException($"{where}: needs a match pattern");
            if (raw.Kind is null || !Kinds.Contains(raw.Kind)) throw new DataException($"{where}: kind must be one of {string.Join(", ", Kinds)}");
            if (raw.Kind == "income" && !Vocab.Sources.Contains(raw.Source ?? "")) throw new DataException($"{where}: an income rule needs a source from {string.Join(", ", Vocab.Sources)}");
            if (raw.Kind == "expense" && !Vocab.Categories.Contains(raw.Category ?? "")) throw new DataException($"{where}: an expense rule needs a category from {string.Join(", ", Vocab.Categories)}");
            var paidFrom = raw.PaidFrom ?? "bank";
            if (!Vocab.PaidFrom.Contains(paidFrom)) throw new DataException($"{where}: paidFrom must be one of {string.Join(", ", Vocab.PaidFrom)}");
            rules.Add(new Rule(raw.Name ?? raw.Match, new Regex(raw.Match, RegexOptions.CultureInvariant), raw.Kind,
                raw.Source ?? "", raw.Category ?? "", raw.Payee ?? "", raw.Description ?? "", paidFrom));
        }
        return rules;
    }

    /// <summary>
    /// A credit equal to a Stripe payout sent a few days earlier is an internal transfer.
    /// Everything else goes through the rules in order; income rules only see credits,
    /// expense rules only debits; the first match wins.
    /// </summary>
    public static (List<Classified> Matched, List<BankTransaction> Unmatched) Classify(
        IEnumerable<BankTransaction> transactions, IReadOnlyList<Rule> rules, IEnumerable<(DateOnly Date, decimal Amount)> stripePayouts)
    {
        var pending = stripePayouts.ToList();
        var matched = new List<Classified>();
        var unmatched = new List<BankTransaction>();
        foreach (var t in transactions)
        {
            if (TakePayout(pending, t))
            {
                matched.Add(new Classified(t, null, "transfer"));
                continue;
            }
            var rule = rules.FirstOrDefault(r =>
                (r.Kind != "income" || t.Amount > 0) && (r.Kind != "expense" || t.Amount < 0) && r.Match.IsMatch(t.Haystack));
            if (rule is null) unmatched.Add(t);
            else matched.Add(new Classified(t, rule, rule.Kind));
        }
        return (matched, unmatched);
    }

    private static bool TakePayout(List<(DateOnly Date, decimal Amount)> payouts, BankTransaction t)
    {
        if (t.Amount <= 0) return false;
        var index = payouts.FindIndex(p => p.Amount == t.Amount && t.Date >= p.Date && t.Date.DayNumber - p.Date.DayNumber <= PayoutWindowDays);
        if (index < 0) return false;
        payouts.RemoveAt(index);
        return true;
    }
}

using System.Globalization;
using System.Text.Json;

namespace Finances.Stripe;

public sealed class StripeTotals
{
    public decimal Gross { get; set; }
    public decimal Fees { get; set; }
    public decimal Net { get; set; }
    public Dictionary<string, decimal> DonorTotals { get; } = [];
    public int Donors => DonorTotals.Count;
    public bool Any => Gross != 0 || Donors > 0;
}

public sealed record StripeMonth(string Currency)
{
    /// <summary>Charges a platform created on this account: Liberapay passes each donation through Stripe Connect.</summary>
    public StripeTotals Liberapay { get; } = new();
    /// <summary>Charges made directly on this Stripe account.</summary>
    public StripeTotals Direct { get; } = new();
    /// <summary>Fees Stripe charged on their own, outside a donation (date, amount).</summary>
    public List<(DateOnly Date, decimal Amount)> FeeExpenses { get; } = [];
    /// <summary>Money sent to the bank (date, amount); the bank side is matched against these.</summary>
    public List<(DateOnly Date, decimal Amount)> Payouts { get; } = [];
    public decimal ClosingBalance { get; set; }
    public Dictionary<string, int> Unhandled { get; } = [];
    public bool MonthFinished { get; set; } = true;
}

/// <summary>
/// Reads one month from Stripe. Needs a restricted key with read access to Balance,
/// Balance transactions and Charges, nothing else: https://dashboard.stripe.com/apikeys
/// Liberapay donations arrive here too: Liberapay is a Stripe Connect platform, so its
/// charges carry an `application` (or `source_transfer`) and their fee is already taken.
/// </summary>
public sealed class StripeClient(JsonHttp http, string apiKey, TimeZoneInfo tz, Func<DateTimeOffset> now)
{
    private const string Api = "https://api.stripe.com/v1";
    private static readonly HashSet<string> DonationTypes = ["charge", "payment"];
    private static readonly HashSet<string> RefundTypes = ["refund", "payment_refund"];

    private readonly Dictionary<string, string> _headers = new()
    {
        ["Authorization"] = $"Bearer {apiKey}",
        ["Stripe-Version"] = "2024-06-20",
    };

    public async Task<StripeMonth> MonthAsync(Month month, string currency)
    {
        var start = Unix(month.First);
        var end = Unix(month.First.AddMonths(1));
        var result = new StripeMonth(currency) { MonthFinished = end <= now().ToUnixTimeSeconds() };

        // Charges first: they tell which group a balance transaction belongs to, and who the donors are.
        // The identifying keys never leave this method; only the counts do.
        var groupOf = new Dictionary<string, StripeTotals>();
        await foreach (var charge in ListAsync("charges", [("created[gte]", start), ("created[lt]", end)], expand: "data.refunds"))
        {
            var platform = charge.StrOrNull("application") is { Length: > 0 } || charge.StrOrNull("source_transfer") is { Length: > 0 };
            var group = platform ? result.Liberapay : result.Direct;
            groupOf[charge.Str("id")] = group;
            foreach (var refund in charge.Obj("refunds")?.Items("data") ?? [])
                groupOf[refund.Str("id")] = group;
            if (charge.Str("status") != "succeeded" || !charge.Bool("paid")) continue;
            var email = charge.Obj("billing_details")?.StrOrNull("email");
            var fingerprint = charge.Obj("payment_method_details")?.Obj("card")?.StrOrNull("fingerprint");
            var key = (charge.StrOrNull("customer") ?? email ?? fingerprint ?? charge.Str("id")).ToLowerInvariant();
            var given = Money.FromMinor(charge.Long("amount") - charge.Long("amount_refunded"), currency);
            group.DonorTotals[key] = group.DonorTotals.GetValueOrDefault(key) + given;
        }

        await foreach (var bt in ListAsync("balance_transactions", [("created[gte]", start), ("created[lt]", end)]))
        {
            var kind = bt.Str("type");
            var btCurrency = bt.Str("currency").ToUpperInvariant();
            if (btCurrency != currency)
            {
                Count(result.Unhandled, $"{kind} in {btCurrency}");
                continue;
            }
            if (DonationTypes.Contains(kind) || RefundTypes.Contains(kind))
            {
                // `payment` is what a platform's transfer looks like on this account; fall back to that when the charge is unknown.
                var group = groupOf.GetValueOrDefault(bt.Str("source")) ?? (kind.StartsWith("payment", StringComparison.Ordinal) ? result.Liberapay : result.Direct);
                group.Gross += Money.FromMinor(bt.Long("amount"), currency);
                group.Fees += Money.FromMinor(bt.Long("fee"), currency);
                group.Net += Money.FromMinor(bt.Long("net"), currency);
            }
            else if (kind == "stripe_fee")
                result.FeeExpenses.Add((Day(bt.Long("created")), -Money.FromMinor(bt.Long("net"), currency)));
            else if (kind == "payout")
                result.Payouts.Add((Day(bt.Long("created")), -Money.FromMinor(bt.Long("amount"), currency)));
            else
                Count(result.Unhandled, kind);
        }

        var balance = await http.GetAsync($"{Api}/balance", headers: _headers);
        var current = 0m;
        foreach (var group in new[] { "available", "pending" })
            foreach (var b in balance.Items(group))
                if (b.Str("currency").ToUpperInvariant() == currency)
                    current += Money.FromMinor(b.Long("amount"), currency);
        var sinceMonthEnd = 0m;
        await foreach (var bt in ListAsync("balance_transactions", [("created[gte]", end)]))
            if (bt.Str("currency").ToUpperInvariant() == currency)
                sinceMonthEnd += Money.FromMinor(bt.Long("net"), currency);
        result.ClosingBalance = current - sinceMonthEnd;
        return result;
    }

    private async IAsyncEnumerable<JsonElement> ListAsync(string path, (string Key, long Value)[] filters, string? expand = null)
    {
        var query = filters.Select(f => KeyValuePair.Create(f.Key, f.Value.ToString(CultureInfo.InvariantCulture))).ToList();
        query.Add(KeyValuePair.Create("limit", "100"));
        if (expand is not null) query.Add(KeyValuePair.Create("expand[]", expand));
        string? after = null;
        while (true)
        {
            var q = after is null ? query : query.Append(KeyValuePair.Create("starting_after", after)).ToList();
            var page = await http.GetAsync($"{Api}/{path}", q, _headers);
            var data = page.Items("data").ToList();
            foreach (var item in data) yield return item;
            if (!page.Bool("has_more") || data.Count == 0) yield break;
            after = data[^1].Str("id");
        }
    }

    private long Unix(DateOnly day)
    {
        var local = day.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, tz), TimeSpan.Zero).ToUnixTimeSeconds();
    }

    private DateOnly Day(long timestamp) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(timestamp), tz).DateTime);

    private static void Count(Dictionary<string, int> counts, string key) =>
        counts[key] = counts.GetValueOrDefault(key) + 1;
}

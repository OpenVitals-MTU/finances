using System.Globalization;

namespace Finances;

/// <summary>The fixed vocabularies of the CSV files. data/README.md documents them.</summary>
public static class Vocab
{
    public static readonly string[] Sources = ["liberapay", "stripe", "bank_transfer", "other"];
    public static readonly string[] Accounts = ["bank", "stripe"];
    public static readonly string[] PaidFrom = ["bank", "stripe", "card"];
    public static readonly string[] Categories =
    [
        "hosting", "domains", "store_fees", "devices", "software", "bank_fees", "processor_fees",
        "legal", "insurance", "translation", "development", "travel", "taxes", "other",
    ];

    /// <summary>Description prefix of an expense row that no rule matched. `check` refuses to pass while one exists.</summary>
    public const string Unclassified = "unclassified";
}

public sealed record IncomeRow(string Source, int? Donors, decimal Gross, decimal Fees, decimal Net, string Currency, string Reference);

public sealed record ExpenseRow(DateOnly Date, string Category, string Payee, string Description, decimal Amount, string Currency, string PaidFrom, string Receipt)
{
    public bool IsUnclassified => Description.StartsWith(Vocab.Unclassified, StringComparison.Ordinal);
}

public sealed class DataException(string message) : Exception(message);

public readonly record struct Month(int Year, int Number) : IComparable<Month>
{
    public static Month Parse(string text) =>
        TryParse(text, out var month) ? month : throw new DataException($"'{text}' is not a month in the form YYYY-MM");

    public static bool TryParse(string? text, out Month month)
    {
        month = default;
        if (text is null || text.Length != 7 || text[4] != '-'
            || !int.TryParse(text[..4], NumberStyles.None, CultureInfo.InvariantCulture, out var year)
            || !int.TryParse(text[5..], NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            || number is < 1 or > 12)
            return false;
        month = new Month(year, number);
        return true;
    }

    public static Month Of(DateOnly day) => new(day.Year, day.Month);

    public DateOnly First => new(Year, Number, 1);
    public DateOnly Last => First.AddMonths(1).AddDays(-1);
    public Month Previous => Number == 1 ? new Month(Year - 1, 12) : new Month(Year, Number - 1);
    public bool Contains(DateOnly day) => day.Year == Year && day.Month == Number;

    public int CompareTo(Month other) => (Year, Number).CompareTo((other.Year, other.Number));
    public override string ToString() => $"{Year:D4}-{Number:D2}";
}

public static class Money
{
    public static string Format(decimal amount) => amount.ToString("0.00", CultureInfo.InvariantCulture);

    public static string FormatGrouped(decimal amount) => amount.ToString("#,##0.00", CultureInfo.InvariantCulture);

    public static decimal Parse(string text, string where)
    {
        if (!decimal.TryParse(text.Trim(), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount))
            throw new DataException($"{where}: '{text}' is not a number");
        if (amount != decimal.Round(amount, 2))
            throw new DataException($"{where}: '{text}' has more than two decimals");
        return amount;
    }

    /// <summary>Stripe reports a few currencies in whole units; everything else in hundredths.</summary>
    private static readonly HashSet<string> ZeroDecimal =
        ["BIF", "CLP", "DJF", "GNF", "JPY", "KMF", "KRW", "MGA", "PYG", "RWF", "UGX", "VND", "VUV", "XAF", "XOF", "XPF"];

    public static decimal FromMinor(long amount, string currency) =>
        ZeroDecimal.Contains(currency.ToUpperInvariant()) ? amount : amount / 100m;
}

namespace Finances.Report;

/// <summary>Everything a report shows for one month, computed once from the data store.</summary>
public sealed class MonthFigures
{
    public required Month Month { get; init; }
    public required string Currency { get; init; }
    public required List<IncomeRow> Income { get; init; }
    public required List<ExpenseRow> Expenses { get; init; }
    public required Dictionary<string, decimal>? Opening { get; init; }
    public required Dictionary<string, decimal>? Closing { get; init; }
    public required string Notes { get; init; }
    public required List<DonationBand> Donations { get; init; }

    public decimal NetIncome => Income.Sum(i => i.Net);
    public decimal GrossIncome => Income.Sum(i => i.Gross);
    public decimal FeesTotal => Income.Sum(i => i.Fees);
    public int DonorsTotal => Income.Sum(i => i.Donors ?? 0);
    public decimal TotalExpenses => Expenses.Sum(e => e.Amount);
    public decimal Result => NetIncome - TotalExpenses;
    public decimal? OpeningTotal => Opening?.Values.Sum();
    public decimal? ClosingTotal => Closing?.Values.Sum();
    public decimal? ExpectedClosing => OpeningTotal + Result;
    public decimal? Difference => ClosingTotal - ExpectedClosing;
    public bool HasUnclassified => Expenses.Any(e => e.IsUnclassified);

    public List<(string Category, decimal Amount)> ByCategory => Expenses
        .GroupBy(e => e.Category).Select(g => (g.Key, g.Sum(e => e.Amount)))
        .OrderByDescending(x => x.Item2).ThenBy(x => x.Key, StringComparer.Ordinal).ToList();

    public static List<MonthFigures> LoadAll(DataStore store)
    {
        var balances = store.ReadBalances();
        var months = store.Months();
        var orphans = balances.Keys.Where(m => !months.Contains(m)).ToList();
        if (orphans.Count > 0)
            throw new DataException($"data/balances.csv has months without income or expense files: {string.Join(", ", orphans)}");
        return months.Select(month =>
        {
            var income = store.ReadIncome(month);
            var expenses = store.ReadExpenses(month);
            var currencies = income.Select(i => i.Currency).Concat(expenses.Select(e => e.Currency)).Distinct().ToList();
            if (currencies.Count > 1)
                throw new DataException($"{month}: mixed currencies ({string.Join(", ", currencies)}); convert to the account currency and say so in the notes");
            return new MonthFigures
            {
                Month = month, Currency = currencies.FirstOrDefault() ?? "EUR", Income = income, Expenses = expenses,
                Opening = balances.GetValueOrDefault(month.Previous), Closing = balances.GetValueOrDefault(month),
                Notes = store.ReadNotes(month), Donations = store.ReadDonations(month),
            };
        }).ToList();
    }

    /// <summary>Months of the trailing year's average spending that the closing balance covers.</summary>
    public static string Runway(IReadOnlyList<MonthFigures> all, int index)
    {
        var current = all[index];
        if (current.ClosingTotal is not { } closing) return "—";
        var window = all.Skip(Math.Max(0, index - 11)).Take(index - Math.Max(0, index - 11) + 1).ToList();
        var average = window.Sum(m => m.TotalExpenses) / window.Count;
        return average <= 0 ? "—" : $"{decimal.Round(closing / average, 1).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)} months";
    }
}

using System.Globalization;

namespace Finances;

/// <summary>Reads and writes data/ and validates every row on the way in.</summary>
public sealed class DataStore(string root)
{
    public static readonly string[] IncomeHeader = ["source", "donors", "gross", "fees", "net", "currency", "reference"];
    public static readonly string[] ExpenseHeader = ["date", "category", "payee", "description", "amount", "currency", "paid_from", "receipt"];
    public static readonly string[] BalanceHeader = ["month", "account", "balance", "currency"];
    public static readonly string[] DonationHeader = ["from", "to", "donors", "total", "currency"];

    public string Root { get; } = root;
    public string DataDir => Path.Combine(Root, "data");
    public string ReportsDir => Path.Combine(Root, "reports");
    public string RulesPath => Path.Combine(Root, "config", "rules.json");

    public string IncomePath(Month month) => Path.Combine(DataDir, "income", $"{month}.csv");
    public string ExpensesPath(Month month) => Path.Combine(DataDir, "expenses", $"{month}.csv");
    public string NotesPath(Month month) => Path.Combine(DataDir, "notes", $"{month}.md");
    public string DonationsPath(Month month) => Path.Combine(DataDir, "donations", $"{month}.csv");
    public string BalancesPath => Path.Combine(DataDir, "balances.csv");
    public string PdfPath(Month month) => Path.Combine(ReportsDir, month.Year.ToString(CultureInfo.InvariantCulture), $"{month}.pdf");

    /// <summary>Every month that has an income or an expenses file.</summary>
    public List<Month> Months()
    {
        var months = new SortedSet<Month>();
        foreach (var folder in new[] { "income", "expenses" })
        {
            var dir = Path.Combine(DataDir, folder);
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.GetFiles(dir, "*.csv"))
            {
                var stem = Path.GetFileNameWithoutExtension(file);
                if (!Month.TryParse(stem, out var month))
                    throw new DataException($"{file}: file name must be YYYY-MM.csv");
                months.Add(month);
            }
        }
        return months.ToList();
    }

    public List<IncomeRow> ReadIncome(Month month)
    {
        var path = IncomePath(month);
        if (!File.Exists(path)) return [];
        var rows = new List<IncomeRow>();
        var seen = new HashSet<string>();
        var line = 1;
        foreach (var f in Csv.Read(path, IncomeHeader))
        {
            var where = $"{path}:{++line}";
            var source = Choice(f[0], Vocab.Sources, where);
            if (!seen.Add(source)) throw new DataException($"{where}: source '{source}' appears twice; aggregate it into one row");
            int? donors = f[1].Trim().Length == 0 ? null
                : int.TryParse(f[1].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var d) ? d
                : throw new DataException($"{where}: donors must be a whole number");
            var gross = Money.Parse(f[2], where);
            var fees = Money.Parse(f[3], where);
            var net = Money.Parse(f[4], where);
            if (gross - fees != net) throw new DataException($"{where}: net {Money.Format(net)} is not gross {Money.Format(gross)} minus fees {Money.Format(fees)}");
            rows.Add(new IncomeRow(source, donors, gross, fees, net, Currency(f[5], where), f[6].Trim()));
        }
        return rows;
    }

    public List<ExpenseRow> ReadExpenses(Month month)
    {
        var path = ExpensesPath(month);
        if (!File.Exists(path)) return [];
        var rows = new List<ExpenseRow>();
        var line = 1;
        foreach (var f in Csv.Read(path, ExpenseHeader))
        {
            var where = $"{path}:{++line}";
            if (!DateOnly.TryParseExact(f[0].Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) || !month.Contains(date))
                throw new DataException($"{where}: date '{f[0]}' is not a day of {month}");
            var amount = Money.Parse(f[4], where);
            if (amount <= 0) throw new DataException($"{where}: amount must be positive; record a refund as income under source 'other'");
            rows.Add(new ExpenseRow(date, Choice(f[1], Vocab.Categories, where), f[2].Trim(), f[3].Trim(), amount,
                Currency(f[5], where), Choice(f[6], Vocab.PaidFrom, where), f[7].Trim()));
        }
        return rows.OrderBy(r => r.Date).ThenBy(r => r.Payee, StringComparer.Ordinal).ToList();
    }

    /// <summary>The month's current expense rows, or none when the file is absent or unreadable; used across a partial re-fetch.</summary>
    public IReadOnlyList<ExpenseRow> ReadExpensesRaw(Month month)
    {
        try { return ReadExpenses(month); }
        catch (DataException) { return []; }
    }

    public IReadOnlyList<IncomeRow> ReadIncomeRaw(Month month)
    {
        try { return ReadIncome(month); }
        catch (DataException) { return []; }
    }

    /// <summary>Donation size bands; absent for months fetched before the bands existed.</summary>
    public List<DonationBand> ReadDonations(Month month)
    {
        var path = DonationsPath(month);
        if (!File.Exists(path)) return [];
        var bands = new List<DonationBand>();
        var line = 1;
        foreach (var f in Csv.Read(path, DonationHeader))
        {
            var where = $"{path}:{++line}";
            var from = Money.Parse(f[0], where);
            decimal? to = f[1].Trim().Length == 0 ? null : Money.Parse(f[1], where);
            if (to is { } t && t <= from) throw new DataException($"{where}: 'to' must be above 'from'");
            if (!int.TryParse(f[2].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var donors) || donors < DonationBands.MinimumDonors && bands.Count + 1 > 1)
                throw new DataException($"{where}: a band must hold at least {DonationBands.MinimumDonors} donors unless it is the only one");
            bands.Add(new DonationBand(from, to, donors, Money.Parse(f[3], where)));
            Currency(f[4], where);
        }
        return bands;
    }

    public void WriteDonations(Month month, IEnumerable<DonationBand> bands, string currency) =>
        Csv.Write(DonationsPath(month), DonationHeader, bands.Select(b => new[]
        {
            Money.Format(b.From), b.To is { } to ? Money.Format(to) : "", b.Donors.ToString(CultureInfo.InvariantCulture), Money.Format(b.Total), currency,
        }));

    public string ReadNotes(Month month) => File.Exists(NotesPath(month)) ? File.ReadAllText(NotesPath(month)).Trim() : "";

    /// <summary>Closing balance per account per month. Every listed month has all accounts.</summary>
    public SortedDictionary<Month, Dictionary<string, decimal>> ReadBalances()
    {
        var balances = new SortedDictionary<Month, Dictionary<string, decimal>>();
        if (!File.Exists(BalancesPath)) return balances;
        var line = 1;
        foreach (var f in Csv.Read(BalancesPath, BalanceHeader))
        {
            var where = $"{BalancesPath}:{++line}";
            if (!Month.TryParse(f[0].Trim(), out var month)) throw new DataException($"{where}: month '{f[0]}' is not YYYY-MM");
            var account = Choice(f[1], Vocab.Accounts, where);
            if (!balances.TryGetValue(month, out var accounts)) balances[month] = accounts = [];
            if (!accounts.TryAdd(account, Money.Parse(f[2], where))) throw new DataException($"{where}: {account} already has a balance for {month}");
            Currency(f[3], where);
        }
        foreach (var (month, accounts) in balances)
        {
            var missing = Vocab.Accounts.Where(a => !accounts.ContainsKey(a)).ToList();
            if (missing.Count > 0) throw new DataException($"{BalancesPath}: {month} lacks a balance for {string.Join(", ", missing)}");
        }
        return balances;
    }

    public void WriteIncome(Month month, IEnumerable<IncomeRow> rows) =>
        Csv.Write(IncomePath(month), IncomeHeader, rows.Select(r => new[]
        {
            r.Source, r.Donors?.ToString(CultureInfo.InvariantCulture) ?? "", Money.Format(r.Gross), Money.Format(r.Fees),
            Money.Format(r.Net), r.Currency, r.Reference,
        }));

    public void WriteExpenses(Month month, IEnumerable<ExpenseRow> rows) =>
        Csv.Write(ExpensesPath(month), ExpenseHeader, rows.Select(r => new[]
        {
            r.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), r.Category, r.Payee, r.Description,
            Money.Format(r.Amount), r.Currency, r.PaidFrom, r.Receipt,
        }));

    /// <summary>Replaces the month's rows; accounts named in <paramref name="keep"/> retain their existing value if they have one.</summary>
    public void WriteBalances(Month month, IReadOnlyDictionary<string, decimal> balances, string currency, ISet<string> keep)
    {
        var all = new SortedDictionary<(Month, string), string[]>();
        if (File.Exists(BalancesPath))
            foreach (var f in Csv.Read(BalancesPath, BalanceHeader))
                all[(Month.Parse(f[0]), f[1])] = f;
        foreach (var account in Vocab.Accounts)
        {
            if (keep.Contains(account) && all.ContainsKey((month, account))) continue;
            all[(month, account)] = [month.ToString(), account, Money.Format(balances[account]), currency];
        }
        Csv.Write(BalancesPath, BalanceHeader, all.Values);
    }

    private static string Choice(string value, string[] allowed, string where)
    {
        value = value.Trim();
        if (!allowed.Contains(value)) throw new DataException($"{where}: '{value}' must be one of {string.Join(", ", allowed)}");
        return value;
    }

    private static string Currency(string value, string where)
    {
        value = value.Trim();
        if (value.Length != 3 || !value.All(char.IsAsciiLetterUpper)) throw new DataException($"{where}: currency must be a three-letter code");
        return value;
    }
}

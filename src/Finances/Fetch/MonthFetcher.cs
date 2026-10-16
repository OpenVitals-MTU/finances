using Finances.Bank;
using Finances.Liberapay;
using Finances.Stripe;

namespace Finances.Fetch;

public sealed record FetchOptions(
    Month Month,
    string Currency,
    TimeZoneInfo TimeZone,
    Func<string, string?> Env,
    Func<DateTimeOffset> Now,
    bool SkipStripe = false,
    bool SkipBank = false,
    bool SkipLiberapay = false);

public sealed record FetchResult(List<IncomeRow> Income, List<ExpenseRow> Expenses, List<string> Warnings, List<BankTransaction> Unmatched)
{
    public List<ExpenseRow> Unclassified => Expenses.Where(e => e.IsUnclassified).ToList();
}

/// <summary>
/// Fills data/ for one month from Stripe, the bank and Liberapay. Both donation rows come
/// from Stripe (Liberapay passes its donations through Stripe Connect); Stripe credits in
/// the bank are internal transfers. Bank debits that no
/// rule matches are written as expenses in category "other" with the bank's counterparty
/// as payee and a description starting with "unclassified", so `check` refuses to pass
/// until a rule exists. Receipt ids already in the expenses file survive when the row
/// still exists.
/// </summary>
public sealed class MonthFetcher(DataStore store, JsonHttp http)
{
    public async Task<FetchResult> RunAsync(FetchOptions o)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(o.Now(), o.TimeZone).DateTime);
        var income = new Dictionary<string, IncomeRow>();
        var expenses = new List<ExpenseRow>();
        var warnings = new List<string>();
        var balances = new Dictionary<string, decimal> { ["bank"] = 0m, ["stripe"] = 0m };
        var keep = new HashSet<string>();
        var payouts = new List<(DateOnly Date, decimal Amount)>();
        var unmatchedRows = new List<BankTransaction>();
        var donorTotals = new List<decimal>();   // one entry per donor per source, identity already dropped

        int? patrons = null;
        if (!o.SkipLiberapay)
            patrons = await new LiberapayClient(http).PatronsAsync(o.Env("LIBERAPAY_USERNAME") is { Length: > 0 } u ? u : "openvitals.mtu");

        if (!o.SkipStripe)
        {
            var stripe = await new StripeClient(http, Require(o, "STRIPE_API_KEY"), o.TimeZone, o.Now).MonthAsync(o.Month, o.Currency);
            var fetched = $"Stripe API balance transactions fetched {today:yyyy-MM-dd}";
            donorTotals.AddRange(stripe.Liberapay.DonorTotals.Values);
            donorTotals.AddRange(stripe.Direct.DonorTotals.Values);
            if (stripe.Liberapay.Any)
                income["liberapay"] = new IncomeRow("liberapay", patrons ?? stripe.Liberapay.Donors, stripe.Liberapay.Gross, stripe.Liberapay.Fees,
                    stripe.Liberapay.Net, o.Currency, patrons is null
                        ? $"Liberapay charges passed through Stripe Connect; {fetched}"
                        : $"Liberapay charges passed through Stripe Connect; patron count from liberapay.com on {today:yyyy-MM-dd}; {fetched}");
            if (stripe.Direct.Any)
                income["stripe"] = new IncomeRow("stripe", stripe.Direct.Donors, stripe.Direct.Gross, stripe.Direct.Fees, stripe.Direct.Net, o.Currency, fetched);
            expenses.AddRange(stripe.FeeExpenses.Select(f =>
                new ExpenseRow(f.Date, "processor_fees", "Stripe", "fees charged separately", f.Amount, o.Currency, "stripe", "")));
            payouts = stripe.Payouts;
            balances["stripe"] = stripe.ClosingBalance;
            if (!stripe.MonthFinished) warnings.Add($"{o.Month} is not over; the Stripe closing balance is today's balance");
            foreach (var (kind, count) in stripe.Unhandled)
                warnings.Add($"Stripe: {count} balance transaction(s) of type '{kind}' were ignored; check the dashboard");
        }
        else
        {
            keep.Add("stripe");
            warnings.Add("Stripe skipped: no donation rows this month; Stripe credits in the bank are still treated as transfers");
        }

        if (!o.SkipBank)
        {
            var bank = new EnableBankingClient(http, Require(o, "ENABLEBANKING_APP_ID"), PrivateKey(o), o.Now);
            var session = await bank.SessionAsync(Require(o, "ENABLEBANKING_SESSION_ID"));
            if (session.Status is not ("AUTHORIZED" or "ACTIVE"))
                throw new DataException($"bank session is {session.Status}; run `bank-connect` and `bank-session` again locally and update the secret");
            if (session.AccountIds.Count == 0) throw new DataException("bank session covers no accounts");
            var transactions = new List<BankTransaction>();
            var fetchUntil = today > o.Month.Last ? today : o.Month.Last;
            foreach (var accountUid in session.AccountIds)
            {
                // One call covers the month and everything after it: the later rows turn today's balance into the month-end one.
                var all = await bank.TransactionsAsync(accountUid, o.Month.First, fetchUntil);
                transactions.AddRange(all.Where(t => o.Month.Contains(t.Date)));
                var (balance, currency) = await bank.CurrentBalanceAsync(accountUid);
                if (currency.ToUpperInvariant() != o.Currency)
                    throw new DataException($"bank account is in {currency}, FINANCES_CURRENCY is {o.Currency}");
                balances["bank"] += decimal.Round(balance - all.Where(t => t.Date > o.Month.Last).Sum(t => t.Amount), 2);
            }
            var foreign = transactions.Count(t => t.Currency.ToUpperInvariant() != o.Currency);
            if (foreign > 0) throw new DataException($"{foreign} bank transaction(s) are not in {o.Currency}; they need converting by hand");

            var (matched, unmatched) = Rules.Classify(transactions, Rules.Load(store.RulesPath), payouts);
            unmatchedRows = unmatched;
            var credits = new Dictionary<string, List<BankTransaction>>();
            foreach (var item in matched)
            {
                var t = item.Transaction;
                if (item.Kind == "income")
                    (credits.TryGetValue(item.Rule!.Source, out var list) ? list : credits[item.Rule.Source] = []).Add(t);
                else if (item.Kind == "expense")
                {
                    var r = item.Rule!;
                    var description = r.Description.Length > 0 ? r.Description : t.Text.Length > 0 ? Truncate(t.Text, 80) : r.Name;
                    expenses.Add(new ExpenseRow(t.Date, r.Category, r.Payee.Length > 0 ? r.Payee : t.Counterparty, description, -t.Amount, o.Currency, r.PaidFrom, ""));
                }
            }
            foreach (var t in unmatched.Where(t => t.Amount < 0))
            {
                var detail = t.Text.Length > 0 ? $"{Vocab.Unclassified}: {Truncate(t.Text, 70)}" : $"{Vocab.Unclassified}: add a rule to config/rules.json";
                expenses.Add(new ExpenseRow(t.Date, "other", t.Counterparty.Length > 0 ? t.Counterparty : "unknown payee", detail, -t.Amount, o.Currency, "bank", ""));
            }
            var unmatchedCredits = unmatched.Where(t => t.Amount > 0).ToList();
            if (unmatchedCredits.Count > 0)
                (credits.TryGetValue("other", out var others) ? others : credits["other"] = []).AddRange(unmatchedCredits);

            foreach (var (source, items) in credits)
            {
                var net = items.Sum(t => t.Amount);
                donorTotals.AddRange(items.GroupBy(t => t.Counterparty.ToLowerInvariant()).Select(g => g.Sum(t => t.Amount)));
                var (donors, reference) = source switch
                {
                    "other" when unmatchedCredits.Count > 0 && items.Count == unmatchedCredits.Count =>
                        ((int?)null, "credits that no rule in config/rules.json matched; add one"),
                    _ => (items.Select(t => t.Counterparty.ToLowerInvariant()).Where(c => c.Length > 0).Distinct().Count() is var n and > 0 ? n : null,
                          $"bank statement {o.Month}"),
                };
                if (income.TryGetValue(source, out var existing))  // a bank rule may add to a source Stripe already filled
                    income[source] = existing with { Gross = existing.Gross + net, Net = existing.Net + net, Reference = $"{existing.Reference}; plus {reference}" };
                else
                    income[source] = new IncomeRow(source, donors, net, 0m, net, o.Currency, reference);
            }
            if (payouts.Count > 0)
                warnings.Add($"{payouts.Count} Stripe payout(s) were not found in the bank statement (still in transit?); the reconciliation will show it");
            if (unmatched.Count > 0)
                warnings.Add($"{unmatched.Count} bank transaction(s) matched no rule; see the rows whose description starts with '{Vocab.Unclassified}'");
        }
        else keep.Add("bank");

        // A skipped source keeps the rows it produced last time, so a partial re-run (the bank allows few calls a day) loses nothing.
        var existingExpenses = store.ReadExpensesRaw(o.Month);
        if (o.SkipBank) expenses.AddRange(existingExpenses.Where(e => e.PaidFrom is "bank" or "card").Select(e => Reclassify(e, store)));
        if (o.SkipStripe) expenses.AddRange(existingExpenses.Where(e => e.PaidFrom == "stripe"));
        foreach (var row in store.ReadIncomeRaw(o.Month))
        {
            var fromBank = row.Source is "bank_transfer" or "other";
            if ((o.SkipBank && fromBank || o.SkipStripe && !fromBank) && !income.ContainsKey(row.Source)) income[row.Source] = row;
        }
        var incomeRows = Vocab.Sources.Where(income.ContainsKey).Select(s => income[s]).ToList();
        expenses = PreserveReceipts(existingExpenses, expenses)
            .OrderBy(e => e.Date).ThenBy(e => e.Payee, StringComparer.Ordinal).ToList();

        store.WriteIncome(o.Month, incomeRows);
        store.WriteExpenses(o.Month, expenses);
        store.WriteBalances(o.Month, balances, o.Currency, keep);
        if (!o.SkipStripe && !o.SkipBank)   // bands need every source, or a partial run would publish a misleading spread
            store.WriteDonations(o.Month, DonationBands.Build(donorTotals), o.Currency);
        else
            warnings.Add("a source was skipped, so data/donations was left as it was");
        return new FetchResult(incomeRows, expenses, warnings, unmatchedRows);
    }

    private static List<ExpenseRow> PreserveReceipts(IReadOnlyList<ExpenseRow> existing, List<ExpenseRow> fresh)
    {
        var receipts = existing.Where(e => e.Receipt.Length > 0)
            .GroupBy(e => (e.Date, e.Amount, e.Payee)).ToDictionary(g => g.Key, g => g.First().Receipt);
        return fresh.Select(e => receipts.TryGetValue((e.Date, e.Amount, e.Payee), out var receipt) ? e with { Receipt = receipt } : e).ToList();
    }

    /// <summary>A kept unclassified row gets another chance against the rules, so adding a rule needs no bank call.</summary>
    private static ExpenseRow Reclassify(ExpenseRow e, DataStore store)
    {
        if (!e.IsUnclassified) return e;
        var text = e.Description[Vocab.Unclassified.Length..].TrimStart(':', ' ');
        var haystack = $"{(e.Payee == "unknown payee" ? "" : e.Payee)} {text}".Trim();
        var rule = Rules.Load(store.RulesPath).FirstOrDefault(r => r.Kind == "expense" && r.Match.IsMatch(haystack));
        if (rule is null) return e;
        return e with
        {
            Category = rule.Category,
            Payee = rule.Payee.Length > 0 ? rule.Payee : e.Payee,
            Description = rule.Description.Length > 0 ? rule.Description : text.Length > 0 ? text : rule.Name,
            PaidFrom = rule.PaidFrom,
        };
    }

    /// <summary>The PEM text itself, or the path of a file holding it (handier on a laptop).</summary>
    public static string PrivateKey(FetchOptions o)
    {
        if (o.Env("ENABLEBANKING_PRIVATE_KEY") is { Length: > 0 } pem) return pem.Replace("\\n", "\n");
        if (o.Env("ENABLEBANKING_PRIVATE_KEY_FILE") is { Length: > 0 } file && File.Exists(file)) return File.ReadAllText(file);
        throw new DataException("ENABLEBANKING_PRIVATE_KEY (the PEM text) or ENABLEBANKING_PRIVATE_KEY_FILE (its path) is not set; see appsettings.local.example.json");
    }

    private static string Require(FetchOptions o, string name) =>
        o.Env(name) is { Length: > 0 } value ? value.Trim() : throw new DataException($"{name} is not set; put it in appsettings.local.json or export it as an environment variable");

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];
}

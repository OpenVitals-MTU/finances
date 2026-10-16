using Finances.Bank;

namespace Finances.Tests;

public sealed class RulesAndDataTests
{
    private static List<Rule> DefaultRules() => Rules.Load(Path.Combine(FetchTests.RepoRoot(), "config", "rules.json"));

    private static BankTransaction Tx(string day, string amount, string counterparty, string text = "") =>
        new(DateOnly.Parse(day, System.Globalization.CultureInfo.InvariantCulture), decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture), "EUR", counterparty, text, day + amount);

    [Fact]
    public void Income_rules_see_credits_only_and_expense_rules_debits_only()
    {
        var (matched, unmatched) = Rules.Classify([Tx("2026-10-03", "-9.80", "FLY.IO"), Tx("2026-10-03", "9.80", "FLY.IO refund")], DefaultRules(), []);
        Assert.Equal(["expense", "income"], matched.Select(m => m.Kind));
        Assert.Equal("bank_transfer", matched[1].Rule!.Source);  // the catch-all; add a refund rule above it when it happens
        Assert.Empty(unmatched);
    }

    [Fact]
    public void A_stripe_credit_is_a_transfer_by_payout_match_or_else_by_rule()
    {
        var payouts = new List<(DateOnly, decimal)> { (new DateOnly(2026, 10, 20), 19.44m) };
        var (near, _) = Rules.Classify([Tx("2026-10-22", "19.44", "STRIPE")], DefaultRules(), payouts);
        Assert.Equal("transfer", near[0].Kind);
        Assert.Null(near[0].Rule);                       // matched the payout list
        var (far, _) = Rules.Classify([Tx("2026-10-30", "19.44", "STRIPE")], DefaultRules(), payouts);
        Assert.Equal("transfer", far[0].Kind);
        Assert.NotNull(far[0].Rule);                     // too late for the window; the first rule still makes it a transfer
    }

    [Fact]
    public void Rules_file_is_validated()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, """{"rules":[{"match":"x","kind":"expense"}]}""");
        var error = Assert.Throws<DataException>(() => Rules.Load(path));
        Assert.Contains("needs a category", error.Message);
    }

    [Theory]
    [InlineData("source,donors,gross,fees,net,currency,reference\nliberapay,1,10.00,1.00,8.00,EUR,x\n", "net 8.00 is not gross 10.00 minus fees 1.00")]
    [InlineData("source,donors,gross,fees,net,currency,reference\nsnacks,1,10.00,1.00,9.00,EUR,x\n", "must be one of")]
    [InlineData("source,donors,gross,fees,net,currency,reference\nstripe,1,10.00,1.00,9.00,EUR,x\nstripe,1,1.00,0.00,1.00,EUR,y\n", "appears twice")]
    public void Income_rows_are_validated(string csv, string expected)
    {
        var root = Path.Combine(Path.GetTempPath(), "finances-data-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "data", "income"));
        File.WriteAllText(Path.Combine(root, "data", "income", "2026-10.csv"), csv);
        try
        {
            var error = Assert.Throws<DataException>(() => new DataStore(root).ReadIncome(new Month(2026, 10)));
            Assert.Contains(expected, error.Message);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Expense_dates_must_fall_in_the_month()
    {
        var root = Path.Combine(Path.GetTempPath(), "finances-data-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "data", "expenses"));
        File.WriteAllText(Path.Combine(root, "data", "expenses", "2026-10.csv"),
            "date,category,payee,description,amount,currency,paid_from,receipt\n2026-09-30,other,Shop,x,1.00,EUR,card,\n");
        try
        {
            var error = Assert.Throws<DataException>(() => new DataStore(root).ReadExpenses(new Month(2026, 10)));
            Assert.Contains("is not a day of 2026-10", error.Message);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Csv_round_trips_quoted_fields()
    {
        var text = Csv.Format(["a", "b"], [["x, y", "say \"hi\""]]);
        Assert.Equal("a,b\n\"x, y\",\"say \"\"hi\"\"\"\n", text);
        Assert.Equal(["x, y", "say \"hi\""], Csv.ParseLine(text.Split('\n')[1]));
    }
}

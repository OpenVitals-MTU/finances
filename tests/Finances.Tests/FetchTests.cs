using Finances.Fetch;

namespace Finances.Tests;

public sealed class FetchTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "finances-test-" + Guid.NewGuid().ToString("N"));
    private readonly FakeApis _apis = new();
    private readonly DataStore _store;

    private static readonly Dictionary<string, string> Secrets = new()
    {
        ["STRIPE_API_KEY"] = "rk_test", ["ENABLEBANKING_APP_ID"] = "app-1", ["ENABLEBANKING_SESSION_ID"] = "sess-1",
        ["ENABLEBANKING_PRIVATE_KEY"] = System.Security.Cryptography.RSA.Create(2048).ExportPkcs8PrivateKeyPem(),
    };

    public FetchTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "data", "income"));
        Directory.CreateDirectory(Path.Combine(_root, "data", "expenses"));
        Directory.CreateDirectory(Path.Combine(_root, "config"));
        File.Copy(Path.Combine(RepoRoot(), "config", "rules.json"), Path.Combine(_root, "config", "rules.json"));
        File.WriteAllText(Path.Combine(_root, "data", "balances.csv"),
            "month,account,balance,currency\n2026-09,bank,500.00,EUR\n2026-09,stripe,0.00,EUR\n");
        File.WriteAllText(Path.Combine(_root, "data", "expenses", "2026-10.csv"),
            "date,category,payee,description,amount,currency,paid_from,receipt\n2026-10-03,hosting,Fly.io,old text,9.80,EUR,card,R-2026-024\n");
        _store = new DataStore(_root);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private FetchOptions Options(bool skipStripe = false) => new(
        new Month(2026, 10), "EUR", TimeZoneInfo.FindSystemTimeZoneById("Europe/Tallinn"),
        name => Secrets.GetValueOrDefault(name), () => new DateTimeOffset(2026, 11, 10, 9, 0, 0, TimeSpan.Zero), SkipStripe: skipStripe);

    [Fact]
    public async Task Writes_income_expenses_and_balances_from_all_three_sources()
    {
        var result = await new MonthFetcher(_store, new JsonHttp(new HttpClient(_apis))).RunAsync(Options());

        var income = File.ReadAllLines(_store.IncomePath(new Month(2026, 10)));
        // Liberapay: the platform charge py_1 (12.00, fee already taken); patron count from the public profile
        Assert.Equal("liberapay,14,12.00,0.00,12.00,EUR,Liberapay charges passed through Stripe Connect; patron count from liberapay.com on 2026-11-10; Stripe API balance transactions fetched 2026-11-10", income[1]);
        // direct: ch_1 20.00 - 0.56 and ch_2 10.00 - 0.35 refunded in full; two distinct donors (cus_A twice, one e-mail)
        Assert.Equal("stripe,2,20.00,0.56,19.44,EUR,Stripe API balance transactions fetched 2026-11-10", income[2]);
        Assert.Equal("bank_transfer,1,50.00,0.00,50.00,EUR,bank statement 2026-10", income[3]);
        Assert.Equal(4, income.Length);

        var expenses = File.ReadAllLines(_store.ExpensesPath(new Month(2026, 10)));
        Assert.Equal(
        [
            "date,category,payee,description,amount,currency,paid_from,receipt",
            "2026-10-03,hosting,Fly.io,landing page and docs site,9.80,EUR,card,R-2026-024",
            "2026-10-29,other,Mystery Shop OÜ,unclassified: something,35.00,EUR,bank,",
            "2026-10-31,bank_fees,Bank,account fees,1.50,EUR,bank,",
            "2026-10-31,processor_fees,Stripe,fees charged separately,2.00,EUR,stripe,",
        ], expenses);

        // bank: 493.94 today minus the 9.80 booked in November = 503.74; stripe: 19.65 today minus 9.65 net since = 10.00
        var balances = File.ReadAllLines(_store.BalancesPath);
        Assert.Equal(["2026-10,bank,503.74,EUR", "2026-10,stripe,10.00,EUR"], balances[3..]);

        // donors across sources: cus_A 20 (ch_1) + 10 - 10 refunded (ch_3 is cus_A too: 20+... ) → see totals; too few for separate bands
        var donations = File.ReadAllLines(_store.DonationsPath(new Month(2026, 10)));
        Assert.Equal("from,to,donors,total,currency", donations[0]);
        Assert.Single(donations[1..]);                         // 4 donors in total (cus_A, x@example.org refunded to 0 and dropped, py_1, the bank donor)
        Assert.EndsWith(",EUR", donations[1]);

        Assert.Single(result.Unclassified);
        // the free tier is metered per day: one session call, one transactions call, one balance call
        Assert.Equal(3, _apis.Calls.Count(c => c.Url.Contains("api.enablebanking.com")));
        var bearer = _apis.BankAuthorization!;
        Assert.StartsWith("Bearer ", bearer);
        Assert.Equal(3, bearer.Split('.').Length);
        foreach (var file in Directory.GetFiles(Path.Combine(_root, "data"), "*.csv", SearchOption.AllDirectories))
            Assert.DoesNotContain("Maasikas", File.ReadAllText(file));  // a donor's name stays out; credits are aggregated
        Assert.Contains(result.Warnings, w => w.Contains("matched no rule"));
    }

    [Fact]
    public async Task Skipping_stripe_keeps_its_balance_and_warns()
    {
        File.AppendAllText(_store.BalancesPath, "2026-10,bank,1.00,EUR\n2026-10,stripe,77.00,EUR\n");
        var result = await new MonthFetcher(_store, new JsonHttp(new HttpClient(_apis))).RunAsync(Options(skipStripe: true));
        Assert.Contains("2026-10,stripe,77.00,EUR", File.ReadAllLines(_store.BalancesPath));
        Assert.Contains(result.Warnings, w => w.StartsWith("Stripe skipped"));
        // the Stripe credits in the bank are transfers by rule even without Stripe's payout list: only the direct donation is income
        Assert.Equal(["source,donors,gross,fees,net,currency,reference", "bank_transfer,1,50.00,0.00,50.00,EUR,bank statement 2026-10"],
            File.ReadAllLines(_store.IncomePath(new Month(2026, 10))));
    }

    [Fact]
    public async Task Skipping_the_bank_keeps_last_time_s_bank_rows()
    {
        var fetcher = new MonthFetcher(_store, new JsonHttp(new HttpClient(_apis)));
        await fetcher.RunAsync(Options());
        var before = (File.ReadAllLines(_store.IncomePath(new Month(2026, 10))), File.ReadAllLines(_store.ExpensesPath(new Month(2026, 10))), File.ReadAllLines(_store.BalancesPath));
        var callsBefore = _apis.Calls.Count;

        await fetcher.RunAsync(Options() with { SkipBank = true });

        Assert.Equal(before.Item1, File.ReadAllLines(_store.IncomePath(new Month(2026, 10))));
        Assert.Equal(before.Item2, File.ReadAllLines(_store.ExpensesPath(new Month(2026, 10))));
        Assert.Equal(before.Item3, File.ReadAllLines(_store.BalancesPath));
        Assert.DoesNotContain(_apis.Calls.Skip(callsBefore), c => c.Url.Contains("enablebanking"));
    }

    [Fact]
    public async Task A_missing_secret_is_a_clear_error()
    {
        var options = Options() with { Env = _ => null };
        var error = await Assert.ThrowsAsync<DataException>(() => new MonthFetcher(_store, new JsonHttp(new HttpClient(_apis))).RunAsync(options));
        Assert.Contains("STRIPE_API_KEY", error.Message);
    }

    [Fact]
    public async Task An_expired_bank_session_is_a_clear_error()
    {
        _apis.SessionStatus = "EXPIRED";
        var error = await Assert.ThrowsAsync<DataException>(() => new MonthFetcher(_store, new JsonHttp(new HttpClient(_apis))).RunAsync(Options()));
        Assert.Contains("bank-connect", error.Message);
    }

    public static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Finances.sln"))) return dir.FullName;
        throw new InvalidOperationException("Finances.sln not found above the test directory");
    }
}

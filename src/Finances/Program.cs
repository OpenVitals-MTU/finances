using System.Globalization;
using System.Text;
using Finances;
using Finances.Bank;
using Finances.Fetch;
using Finances.Report;

var root = Environment.GetEnvironmentVariable("FINANCES_ROOT") is { Length: > 0 } r ? r : FindRoot();
var store = new DataStore(root);
var settings = Settings.Load(root);
var env = (Func<string, string?>)settings.Get;
var currency = settings.Get("FINANCES_CURRENCY", "EUR").ToUpperInvariant();
var tz = TimeZoneInfo.FindSystemTimeZoneById(settings.Get("FINANCES_TIMEZONE", "Europe/Tallinn"));
var organisation = settings.Get("FINANCES_ORGANISATION", "OpenVitals MTÜ");
var http = new JsonHttp(new HttpClient { Timeout = TimeSpan.FromSeconds(60) });

try
{
    return args.Length == 0 ? Usage() : args[0] switch
    {
        "fetch" => await Fetch(args[1..]),
        "report" => Report(args[1..]),
        "check" => Check(),
        "bank-institutions" => await BankInstitutions(args.Skip(1).FirstOrDefault() ?? ""),
        "bank-connect" => await BankConnect(args[1..]),
        "bank-session" => await BankSession(args[1..]),
        "bank-status" => await BankStatus(),
        _ => Usage(),
    };
}
catch (Exception e) when (e is DataException or ApiException or HttpRequestException or TaskCanceledException)
{
    Console.Error.WriteLine($"error: {e.Message}");
    return 2;
}

async Task<int> Fetch(string[] rest)
{
    var month = rest.Length > 0 && !rest[0].StartsWith("--") ? Month.Parse(rest[0]) : Month.Of(Today()).Previous;
    var skip = rest.SkipWhile(a => a != "--skip").Skip(1).Where(a => !a.StartsWith("--")).ToHashSet();
    var options = new FetchOptions(month, currency, tz, env, () => DateTimeOffset.UtcNow,
        SkipStripe: skip.Contains("stripe"), SkipBank: skip.Contains("bank"), SkipLiberapay: skip.Contains("liberapay"));
    var result = await new MonthFetcher(store, http).RunAsync(options);
    Console.WriteLine($"wrote data/income/{month}.csv ({result.Income.Count} rows), data/expenses/{month}.csv ({result.Expenses.Count} rows), balances for {month}");
    foreach (var warning in result.Warnings) Console.Error.WriteLine($"warning: {warning}");
    var unclassified = result.Unclassified;
    foreach (var u in unclassified)
        Console.Error.WriteLine($"  unclassified: {u.Date:yyyy-MM-dd}  {Money.Format(u.Amount),10}  {u.Payee}  |  {u.Description}");
    if (env("GITHUB_OUTPUT") is { Length: > 0 } output)
    {
        var list = string.Join("; ", unclassified.Select(u => $"{u.Date:yyyy-MM-dd} {Money.Format(u.Amount)} {u.Currency} to {u.Payee}"));
        File.AppendAllText(output, $"month={month}\nunclassified={unclassified.Count}\nunclassified_list={list}\n");
    }
    return 0;
}

int Report(string[] rest)
{
    var all = MonthFigures.LoadAll(store);
    var only = rest.Length > 0 ? Month.Parse(rest[0]) : (Month?)null;
    for (var i = 0; i < all.Count; i++)
    {
        if (only is not null && all[i].Month != only) continue;
        var path = store.PdfPath(all[i].Month);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, PdfReport.Render(all[i], MonthFigures.Runway(all, i), organisation, Today()));
        Console.WriteLine($"wrote {Path.GetRelativePath(root, path)}");
    }
    Directory.CreateDirectory(store.ReportsDir);
    File.WriteAllText(Path.Combine(store.ReportsDir, "README.md"), IndexWriter.Render(all), new UTF8Encoding(false));
    Console.WriteLine("wrote reports/README.md");
    return 0;
}

int Check()
{
    var problems = Checker.Run(store);
    foreach (var p in problems) Console.Error.WriteLine($"  {p}");
    var count = store.Months().Count;
    Console.WriteLine(problems.Count > 0 ? $"{problems.Count} problem(s)" : count == 0 ? "no months published yet" : $"{count} month(s), data valid, reports present");
    return problems.Count == 0 ? 0 : 1;
}

EnableBankingClient Bank() =>
    new(http, Require("ENABLEBANKING_APP_ID"), MonthFetcher.PrivateKey(new FetchOptions(default, currency, tz, env, () => DateTimeOffset.UtcNow)), () => DateTimeOffset.UtcNow);

async Task<int> BankInstitutions(string filter)
{
    foreach (var i in await Bank().InstitutionsAsync(settings.Get("FINANCES_BANK_COUNTRY", "EE")))
        if (i.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            Console.WriteLine($"{i.Name,-36} consent up to {i.MaxConsentDays} days{(i.Business ? ", business accounts" : "")}");
    return 0;
}

async Task<int> BankConnect(string[] rest)
{
    if (rest.Length == 0) { Console.Error.WriteLine("usage: bank-connect \"<bank name as listed>\" [days]"); return 2; }
    var days = rest.Length > 1 ? int.Parse(rest[1], CultureInfo.InvariantCulture) : 90;
    var redirect = settings.Get("ENABLEBANKING_REDIRECT_URL", "https://github.com/OpenVitals-MTU/finances");
    var url = await Bank().StartAuthorizationAsync(rest[0], settings.Get("FINANCES_BANK_COUNTRY", "EE"), days, redirect, business: true);
    Console.WriteLine("1. Open this link, log in to the bank and approve read access:");
    Console.WriteLine($"   {url}");
    Console.WriteLine($"2. The bank sends you back to {redirect} with ?code=... in the address bar. Copy the code and run:");
    Console.WriteLine("   dotnet run --project src/Finances -- bank-session <code>");
    return 0;
}

async Task<int> BankSession(string[] rest)
{
    if (rest.Length == 0) { Console.Error.WriteLine("usage: bank-session <code from the redirect>"); return 2; }
    var (sessionId, accounts) = await Bank().CreateSessionAsync(rest[0]);
    Console.WriteLine("Store this value as the repository secret ENABLEBANKING_SESSION_ID (and in appsettings.local.json):");
    Console.WriteLine($"   {sessionId}");
    foreach (var (uid, name, cur, iban) in accounts)
        Console.WriteLine($"   account {uid}: {name} {cur} …{(iban.Length >= 4 ? iban[^4..] : iban)}");
    return 0;
}

async Task<int> BankStatus()
{
    var session = await Bank().SessionAsync(Require("ENABLEBANKING_SESSION_ID"));
    Console.WriteLine($"status: {session.Status}   accounts: {session.AccountIds.Count}   valid until: {session.ValidUntil}");
    return 0;
}

string Require(string name) => settings.Require(name);

DateOnly Today() => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, tz).DateTime);

static string FindRoot()
{
    for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, "Finances.sln"))) return dir.FullName;
    return Directory.GetCurrentDirectory();
}

static int Usage()
{
    Console.Error.WriteLine("""
        usage: finances <command>
          fetch [YYYY-MM] [--skip stripe bank liberapay]   pull a month (default: last month) into data/
          report [YYYY-MM]                                 render reports/<year>/<month>.pdf and reports/README.md
          check                                            validate data/ and reports/; exit 1 on a problem
          bank-institutions [name]                         list banks available in FINANCES_BANK_COUNTRY
          bank-connect "<bank name>" [days]                start a bank consent and print the link to approve it
          bank-session <code>                              turn the code from the redirect into a session id
          bank-status                                      the session's status and expiry
        """);
    return 2;
}

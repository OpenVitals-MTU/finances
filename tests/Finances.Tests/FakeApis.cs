using System.Net;
using System.Text;
using System.Text.Json;
using System.Web;

namespace Finances.Tests;

/// <summary>Answers exactly the calls a fetch makes, with invented numbers; anything else is a test failure.</summary>
public sealed class FakeApis : HttpMessageHandler
{
    public List<(string Method, string Url)> Calls { get; } = [];
    public string? BankAuthorization { get; private set; }
    public string SessionStatus { get; set; } = "AUTHORIZED";

    private static readonly TimeZoneInfo Tz = TimeZoneInfo.FindSystemTimeZoneById("Europe/Tallinn");

    public static long Ts(string local) =>
        new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(DateTime.Parse(local, System.Globalization.CultureInfo.InvariantCulture), Tz), TimeSpan.Zero).ToUnixTimeSeconds();

    private static object Bt(string id, string type, string day, long amount, long fee = 0, string currency = "eur", string? source = null) =>
        new { id, type, created = Ts(day), amount, fee, net = amount - fee, currency, source };

    private static object BankTx(string day, string amount, string? creditor = null, string? debtor = null, string text = "") =>
        new
        {
            booking_date = day, status = "BOOK",
            transaction_amount = new { amount = amount.TrimStart('-'), currency = "EUR" },
            credit_debit_indicator = amount.StartsWith('-') ? "DBIT" : "CRDT",
            creditor = creditor is null ? null : new { name = creditor },
            debtor = debtor is null ? null : new { name = debtor },
            remittance_information = new[] { text }, entry_reference = day + amount,
        };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        var path = uri.GetLeftPart(UriPartial.Path);
        var query = HttpUtility.ParseQueryString(uri.Query);
        Calls.Add((request.Method.Method, path));
        if (path.Contains("api.enablebanking.com")) BankAuthorization = request.Headers.Authorization?.ToString();
        object body = path switch
        {
            _ when path.EndsWith("/v1/balance_transactions") && query["created[lt]"] is not null => new
            {
                data = new[]
                {
                    Bt("txn_1", "charge", "2026-10-02T10:00", 2000, 56, source: "ch_1"),
                    Bt("txn_2", "charge", "2026-10-15T10:00", 1000, 35, source: "ch_2"),
                    Bt("txn_3", "refund", "2026-10-16T10:00", -1000, -35, source: "re_2"),
                    Bt("txn_4", "payout", "2026-10-20T10:00", -1944),
                    Bt("txn_5", "stripe_fee", "2026-10-31T10:00", -200),
                    Bt("txn_6", "charge", "2026-10-28T10:00", 500, 0, "usd"),
                    new { id = "txn_8", type = "payment", created = Ts("2026-10-09T10:00"), amount = 1200, fee = 0, net = 1200, currency = "eur", source = "py_1" },
                },
                has_more = false,
            },
            _ when path.EndsWith("/v1/balance_transactions") => new { data = new[] { Bt("txn_7", "charge", "2026-11-02T10:00", 1000, 35) }, has_more = false },
            _ when path.EndsWith("/v1/charges") => new
            {
                data = new object[]
                {
                    new { id = "ch_1", status = "succeeded", paid = true, customer = "cus_A" },
                    new { id = "ch_2", status = "succeeded", paid = true, billing_details = new { email = "x@example.org" }, refunds = new { data = new[] { new { id = "re_2" } } } },
                    new { id = "ch_3", status = "succeeded", paid = true, customer = "cus_A" },
                    new { id = "ch_4", status = "failed", paid = false },
                    new { id = "py_1", status = "succeeded", paid = true, application = "ca_liberapay", source_transfer = "tr_1" },
                },
                has_more = false,
            },
            _ when path.EndsWith("/v1/balance") => new { available = new[] { new { amount = 1965, currency = "eur" } }, pending = new[] { new { amount = 0, currency = "eur" } } },
            _ when path.EndsWith("/public.json") => new { npatrons = 14, receiving = new { amount = "22.00", currency = "EUR" } },
            _ when path.EndsWith("/sessions/sess-1") => new { status = SessionStatus, accounts = new[] { "acc-1" }, access = new { valid_until = "2027-01-08T00:00:00Z" } },
            _ when path.EndsWith("/accounts/acc-1/transactions") && query["date_from"] == "2026-10-01" && query["date_to"] == "2026-11-10" => new
            {
                transactions = new[]
                {
                    BankTx("2026-10-03", "-9.80", creditor: "FLY.IO", text: "card payment"),
                    BankTx("2026-10-07", "80.60", debtor: "STRIPE TECHNOLOGY EUROPE", text: "LIBERAPAY payout"),
                    BankTx("2026-10-22", "19.44", debtor: "STRIPE TECHNOLOGY EUROPE", text: "STRIPE payout"),
                    BankTx("2026-10-25", "50.00", debtor: "Mari Maasikas", text: "annetus"),
                    BankTx("2026-10-31", "-1.50", creditor: "LHV Pank", text: "Kuutasu"),
                    BankTx("2026-10-29", "-35.00", creditor: "Mystery Shop OÜ", text: "something"),
                    BankTx("2026-11-02", "-9.80", creditor: "FLY.IO"),   // after month end: shapes the closing balance only
                    new { booking_date = "2026-11-05", status = "PDNG", transaction_amount = new { amount = "99.00", currency = "EUR" }, credit_debit_indicator = "DBIT" },
                },
                continuation_key = (string?)null,
            },
            _ when path.EndsWith("/accounts/acc-1/balances") => new { balances = new[] { new { balance_type = "CLBD", balance_amount = new { amount = "493.94", currency = "EUR" } } } },
            _ => throw new InvalidOperationException($"unexpected call {request.Method} {path}"),
        };
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        });
    }
}

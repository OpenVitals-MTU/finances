using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Finances.Bank;

public sealed record BankTransaction(DateOnly Date, decimal Amount, string Currency, string Counterparty, string Text, string Id)
{
    /// <summary>What the rules are matched against.</summary>
    public string Haystack => $"{Counterparty} {Text}".Trim();
}

public sealed record Institution(string Name, string Country, string MaxConsentDays, bool Business);

public sealed record SessionInfo(string Status, List<string> AccountIds, string ValidUntil);

/// <summary>
/// The bank account through Enable Banking (PSD2 account information, free for accounts you
/// link yourself). Estonian banks are listed under country EE. Authorisation is a JWT signed
/// with the application's private key; the consent lasts up to the bank's maximum (90 or
/// 180 days). The free tier allows only a few calls per day, so a fetch makes one session
/// call plus one transactions call and one balance call per account, no more.
/// Docs: https://enablebanking.com/docs/api/reference/
/// </summary>
public sealed class EnableBankingClient
{
    private const string Api = "https://api.enablebanking.com";
    private readonly JsonHttp _http;
    private readonly Dictionary<string, string> _headers;

    public EnableBankingClient(JsonHttp http, string appId, string privateKeyPem, Func<DateTimeOffset> now)
    {
        _http = http;
        _headers = new Dictionary<string, string> { ["Authorization"] = $"Bearer {Jwt(appId, privateKeyPem, now())}" };
    }

    /// <summary>RS256 JWT: header kid = application id; iss/aud fixed by Enable Banking; one hour of validity.</summary>
    public static string Jwt(string appId, string privateKeyPem, DateTimeOffset now)
    {
        using var rsa = RSA.Create();
        try { rsa.ImportFromPem(privateKeyPem); }
        catch (ArgumentException) { throw new DataException("ENABLEBANKING_PRIVATE_KEY is not a PEM private key"); }
        var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { typ = "JWT", alg = "RS256", kid = appId }));
        var claims = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new
        {
            iss = "enablebanking.com", aud = "api.enablebanking.com",
            iat = now.ToUnixTimeSeconds(), exp = now.AddHours(1).ToUnixTimeSeconds(),
        }));
        var signature = rsa.SignData(Encoding.ASCII.GetBytes($"{header}.{claims}"), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{header}.{claims}.{Base64Url(signature)}";
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public Task<JsonElement> GetAsync(string path, IEnumerable<KeyValuePair<string, string>>? query = null) =>
        _http.GetAsync($"{Api}/{path}", query, _headers);

    public Task<JsonElement> PostAsync(string path, object body) => _http.PostJsonAsync($"{Api}/{path}", body, _headers);

    public async Task<List<Institution>> InstitutionsAsync(string country)
    {
        var list = await GetAsync("aspsps", [KeyValuePair.Create("country", country)]);
        return list.Items("aspsps").Select(a => new Institution(a.Str("name"), a.Str("country"),
                (a.Long("maximum_consent_validity") / 86400).ToString(CultureInfo.InvariantCulture),
                a.Items("psu_types").Any(t => t.GetString() == "business")))
            .ToList();
    }

    /// <summary>Starts a consent. The person opens the link, and the bank redirects back with a `code` in the query string.</summary>
    public async Task<string> StartAuthorizationAsync(string bank, string country, int days, string redirectUrl, bool business)
    {
        var auth = await PostAsync("auth", new
        {
            access = new { valid_until = DateTimeOffset.UtcNow.AddDays(days).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) },
            aspsp = new { name = bank, country },
            state = Guid.NewGuid().ToString("N"),
            redirect_url = redirectUrl,
            psu_type = business ? "business" : "personal",
        });
        return auth.Str("url");
    }

    /// <summary>Exchanges the code from the redirect for a session; returns its id and the accounts it covers.</summary>
    public async Task<(string SessionId, List<(string Uid, string Name, string Currency, string Iban)> Accounts)> CreateSessionAsync(string code)
    {
        var session = await PostAsync("sessions", new { code });
        var accounts = session.Items("accounts")
            .Select(a => (a.Str("uid"), a.Str("name"), a.Str("currency"), a.Obj("account_id")?.Str("iban") ?? ""))
            .ToList();
        return (session.Str("session_id"), accounts);
    }

    public async Task<SessionInfo> SessionAsync(string sessionId)
    {
        var session = await GetAsync($"sessions/{sessionId}");
        var accounts = session.Items("accounts")
            .Select(a => a.ValueKind == JsonValueKind.String ? a.GetString()! : a.Str("uid"))
            .Where(uid => uid.Length > 0).ToList();
        return new SessionInfo(session.Str("status"), accounts, session.Obj("access")?.Str("valid_until") ?? "");
    }

    /// <summary>Booked transactions, all pages, from <paramref name="from"/> to <paramref name="to"/> inclusive.</summary>
    public async Task<List<BankTransaction>> TransactionsAsync(string accountUid, DateOnly from, DateOnly to)
    {
        var result = new List<BankTransaction>();
        string? continuation = null;
        do
        {
            var query = new List<KeyValuePair<string, string>>
            {
                KeyValuePair.Create("date_from", from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                KeyValuePair.Create("date_to", to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            };
            if (continuation is not null) query.Add(KeyValuePair.Create("continuation_key", continuation));
            var page = await GetAsync($"accounts/{accountUid}/transactions", query);
            result.AddRange(page.Items("transactions").Where(t => t.StrOrNull("status") is null or "BOOK").Select(Parse));
            continuation = page.StrOrNull("continuation_key");
        } while (!string.IsNullOrEmpty(continuation));
        return result;
    }

    public async Task<(decimal Balance, string Currency)> CurrentBalanceAsync(string accountUid)
    {
        var balances = (await GetAsync($"accounts/{accountUid}/balances")).Items("balances").ToList();
        // ISO 20022 balance types, most authoritative first: closing booked, interim booked, expected, available.
        foreach (var preferred in new[] { "CLBD", "ITBD", "XPCD", "CLAV", "ITAV" })
            foreach (var b in balances)
                if (b.Str("balance_type") == preferred) return Amount(b);
        if (balances.Count > 0) return Amount(balances[0]);
        throw new DataException($"account {accountUid} reported no balance");

        static (decimal, string) Amount(JsonElement b)
        {
            var amount = b.Obj("balance_amount") ?? throw new DataException("balance without balance_amount");
            return (Money.Parse(amount.Str("amount"), "balance"), amount.Str("currency"));
        }
    }

    public static BankTransaction Parse(JsonElement raw)
    {
        var amountObj = raw.Obj("transaction_amount") ?? throw new DataException("transaction without transaction_amount");
        var magnitude = Math.Abs(Money.Parse(amountObj.Str("amount"), "transaction"));
        var credit = raw.Str("credit_debit_indicator") == "CRDT";
        var counterparty = (credit ? raw.Obj("debtor") : raw.Obj("creditor"))?.Str("name") ?? "";
        var text = string.Join(" ", raw.Items("remittance_information").Select(e => e.GetString())).Trim();
        if (text.Length == 0) text = raw.Str("note");
        var day = raw.StrOrNull("booking_date") ?? raw.StrOrNull("value_date") ?? raw.Str("transaction_date");
        return new BankTransaction(DateOnly.ParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture),
            credit ? magnitude : -magnitude, amountObj.Str("currency"), counterparty.Trim(), text,
            raw.StrOrNull("entry_reference") ?? raw.Str("transaction_id"));
    }
}

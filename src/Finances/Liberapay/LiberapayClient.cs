namespace Finances.Liberapay;

/// <summary>
/// Liberapay has no authenticated API for income history. Its money reaches the bank as
/// Stripe payouts; this only reads the public patron count from /&lt;username&gt;/public.json.
/// </summary>
public sealed class LiberapayClient(JsonHttp http)
{
    public async Task<int> PatronsAsync(string username)
    {
        var profile = await http.GetAsync($"https://liberapay.com/{Uri.EscapeDataString(username)}/public.json");
        return (int)profile.Long("npatrons");
    }
}

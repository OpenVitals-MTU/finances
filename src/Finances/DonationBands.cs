namespace Finances;

/// <summary>A published band: donors whose month total fell in [From, To), To null for the open top band.</summary>
public sealed record DonationBand(decimal From, decimal? To, int Donors, decimal Total)
{
    public string Label => To is null ? $"{Money.Format(From)} and above" : From == 0 ? $"under {Money.Format(To.Value)}" : $"{Money.Format(From)} – {Money.Format(To.Value)}";
}

/// <summary>
/// Groups donors by how much they gave in the month, so the spread is public while no
/// donor is identifiable. Every published band holds at least <see cref="MinimumDonors"/>
/// donors: a smaller band is merged with its neighbour until that holds, the way small
/// cells are suppressed in statistical disclosure control. The donor keys passed in never
/// leave this class; only counts and sums do.
/// </summary>
public static class DonationBands
{
    public const int MinimumDonors = 3;
    public static readonly decimal[] Edges = [5m, 20m, 50m, 100m, 500m];

    public static List<DonationBand> Build(IEnumerable<decimal> donorTotals, int minimumDonors = MinimumDonors)
    {
        var totals = donorTotals.Where(t => t > 0).ToList();
        if (totals.Count == 0) return [];
        var bands = new List<(decimal From, decimal? To, int Donors, decimal Total)>();
        decimal from = 0;
        foreach (var edge in Edges.Select(e => (decimal?)e).Append(null))
        {
            var inBand = totals.Where(t => t >= from && (edge is null || t < edge)).ToList();
            bands.Add((from, edge, inBand.Count, inBand.Sum()));
            if (edge is { } e) from = e;
        }
        while (bands.Count > 1)
        {
            var small = bands.FindIndex(b => b.Donors < minimumDonors);
            if (small < 0) break;
            var into = small < bands.Count - 1 ? small + 1 : small - 1;   // merge with the band above, or below for the top one
            var (lower, upper) = small < into ? (bands[small], bands[into]) : (bands[into], bands[small]);
            bands[Math.Min(small, into)] = (lower.From, upper.To, lower.Donors + upper.Donors, lower.Total + upper.Total);
            bands.RemoveAt(Math.Max(small, into));
        }
        return bands.Where(b => b.Donors > 0).Select(b => new DonationBand(b.From, b.To, b.Donors, b.Total)).ToList();
    }
}

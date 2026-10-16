namespace Finances.Tests;

public sealed class DonationBandsTests
{
    [Fact]
    public void Small_bands_merge_upward_until_every_band_has_three_donors()
    {
        // 4 small donors, 1 mid donor, 3 large ones: the lone 20–50 donor cannot stand alone
        var bands = DonationBands.Build([2m, 3m, 4m, 4.5m, 30m, 60m, 70m, 80m]);
        Assert.Equal([(0m, (decimal?)5m, 4, 13.5m), (5m, null, 4, 240m)], bands.Select(b => (b.From, b.To, b.Donors, b.Total)));
        Assert.Equal("under 5.00", bands[0].Label);
        Assert.Equal("5.00 and above", bands[1].Label);
    }

    [Fact]
    public void The_top_band_merges_downward_and_empty_bands_disappear()
    {
        var bands = DonationBands.Build([10m, 12m, 15m, 600m]);
        Assert.Equal([(0m, (decimal?)null, 4, 637m)], bands.Select(b => (b.From, b.To, b.Donors, b.Total)));  // empty bands fold in, so the range stays contiguous from 0
    }

    [Fact]
    public void Fewer_than_three_donors_in_total_give_one_band_and_refunded_donors_are_dropped()
    {
        var bands = DonationBands.Build([12.42m, 0m, -5m]);
        Assert.Equal([(0m, (decimal?)null, 1, 12.42m)], bands.Select(b => (b.From, b.To, b.Donors, b.Total)));
        Assert.Empty(DonationBands.Build([]));
    }

    [Fact]
    public void Well_populated_bands_stay_separate()
    {
        var totals = Enumerable.Repeat(3m, 3).Concat(Enumerable.Repeat(10m, 3)).Concat(Enumerable.Repeat(25m, 3))
            .Concat(Enumerable.Repeat(75m, 3)).Concat(Enumerable.Repeat(200m, 3)).Concat(Enumerable.Repeat(900m, 3));
        Assert.Equal(6, DonationBands.Build(totals).Count);
    }
}

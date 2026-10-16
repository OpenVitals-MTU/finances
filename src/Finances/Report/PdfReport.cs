using System.Globalization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Finances.Report;

/// <summary>The monthly PDF. One A4 document, tables only, no charts: it must read the same in print and on screen.</summary>
public static class PdfReport
{
    private const string Ink = "#1F2937";
    private const string Muted = "#6B7280";
    private const string Line = "#D1D5DB";
    private const string Shade = "#F3F4F6";

    public static byte[] Render(MonthFigures m, string runway, string organisation, DateOnly generated)
    {
        QuestPDF.Settings.License = LicenseType.Community;
        var c = m.Currency;
        return Document.Create(doc => doc.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(20, Unit.Millimetre);
            page.DefaultTextStyle(s => s.FontSize(10).FontColor(Ink));

            page.Header().Column(col =>
            {
                col.Item().Text(organisation).FontSize(11).FontColor(Muted);
                col.Item().Text($"Monthly report {m.Month}").FontSize(20).SemiBold();
                col.Item().PaddingTop(2).Text($"{MonthName(m.Month)} · all amounts in {c}").FontColor(Muted);
                col.Item().PaddingTop(6).LineHorizontal(0.75f).LineColor(Line);
            });

            page.Content().PaddingTop(8).Column(col =>
            {
                col.Spacing(14);

                if (m.HasUnclassified)
                    col.Item().Background("#FEF3C7").Padding(8).Text(
                        "Draft: some payments matched no rule and are listed as \"unclassified\". The published version will not carry this notice.");

                col.Item().Element(e => Section(e, "Summary", t =>
                {
                    t.ColumnsDefinition(d => { d.RelativeColumn(3); d.RelativeColumn(1); });
                    KeyValue(t, "Net income", Amount(m.NetIncome));
                    KeyValue(t, "Expenses", Amount(m.TotalExpenses));
                    KeyValue(t, "Result", Amount(m.Result), bold: true);
                    KeyValue(t, "Closing balance, all accounts", m.ClosingTotal is { } ct ? Amount(ct) : "—");
                    KeyValue(t, "Runway at the trailing year's average spending", runway);
                }));

                col.Item().Element(e => Section(e, "Income by source", t =>
                {
                    t.ColumnsDefinition(d => { d.RelativeColumn(3); d.RelativeColumn(1); d.RelativeColumn(1); d.RelativeColumn(1); d.RelativeColumn(1); });
                    HeaderRow(t, ["source", "donors", "gross", "fees", "net"], rightFrom: 1);
                    if (m.Income.Count == 0) Note(t, 5, "No income recorded this month.");
                    foreach (var i in m.Income)
                        Row(t, [i.Source, i.Donors?.ToString(CultureInfo.InvariantCulture) ?? "—", Amount(i.Gross), Amount(i.Fees), Amount(i.Net)], rightFrom: 1);
                    if (m.Income.Count > 0)
                        Row(t, ["total", m.DonorsTotal.ToString(CultureInfo.InvariantCulture), Amount(m.GrossIncome), Amount(m.FeesTotal), Amount(m.NetIncome)], rightFrom: 1, bold: true);
                }));
                if (m.Income.Any(i => i.Reference.Length > 0))
                    col.Item().PaddingTop(-8).Text("Sources: " + string.Join("; ", m.Income.Where(i => i.Reference.Length > 0).Select(i => $"{i.Source}: {i.Reference}")) + ".")
                        .FontSize(8).FontColor(Muted);

                if (m.Donations.Count > 0)
                {
                    col.Item().Element(e => Section(e, "Donations by size", t =>
                    {
                        t.ColumnsDefinition(d => { d.RelativeColumn(3); d.RelativeColumn(1); d.RelativeColumn(1); });
                        HeaderRow(t, ["gave in the month", "donors", "total"], rightFrom: 1);
                        foreach (var b in m.Donations)
                            Row(t, [b.Label, b.Donors.ToString(CultureInfo.InvariantCulture), Amount(b.Total)], rightFrom: 1);
                        Row(t, ["total", m.Donations.Sum(b => b.Donors).ToString(CultureInfo.InvariantCulture), Amount(m.Donations.Sum(b => b.Total))], rightFrom: 1, bold: true);
                    }));
                    col.Item().PaddingTop(-8).Text($"Each donor counted once per month across all sources, by the amount they gave before fees. A band always holds at least {DonationBands.MinimumDonors} donors; smaller ones are merged with the next, so no donation can be traced to a person.")
                        .FontSize(8).FontColor(Muted);
                }

                col.Item().Element(e => Section(e, "Expenses", t =>
                {
                    t.ColumnsDefinition(d => { d.ConstantColumn(58); d.RelativeColumn(1.2f); d.RelativeColumn(1.5f); d.RelativeColumn(2.6f); d.RelativeColumn(1); d.RelativeColumn(0.8f); d.ConstantColumn(62); });
                    HeaderRow(t, ["date", "category", "payee", "description", "amount", "paid from", "receipt"], rightFrom: 4, rightTo: 4);
                    if (m.Expenses.Count == 0) Note(t, 7, "No expenses this month.");
                    foreach (var e2 in m.Expenses)
                        Row(t, [e2.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), e2.Category, e2.Payee, e2.Description, Amount(e2.Amount), e2.PaidFrom, e2.Receipt.Length > 0 ? e2.Receipt : "—"], rightFrom: 4, rightTo: 4);
                    if (m.Expenses.Count > 0)
                        Row(t, ["", "", "", "total", Amount(m.TotalExpenses), "", ""], rightFrom: 4, rightTo: 4, bold: true);
                }));

                if (m.Expenses.Count > 0)
                    col.Item().Element(e => Section(e, "Expenses by category", t =>
                    {
                        t.ColumnsDefinition(d => { d.RelativeColumn(3); d.RelativeColumn(1); });
                        foreach (var (category, amount) in m.ByCategory) KeyValue(t, category, Amount(amount));
                    }));

                col.Item().Element(e => Section(e, "Reconciliation", t =>
                {
                    t.ColumnsDefinition(d => { d.RelativeColumn(3); d.RelativeColumn(1); });
                    if (m.Opening is not null && m.Closing is not null)
                    {
                        KeyValue(t, $"Opening balance ({m.Month.Previous})", Amount(m.OpeningTotal!.Value));
                        KeyValue(t, "+ net income", Amount(m.NetIncome));
                        KeyValue(t, "− expenses", Amount(m.TotalExpenses));
                        KeyValue(t, "= expected closing", Amount(m.ExpectedClosing!.Value));
                        KeyValue(t, "Actual closing", Amount(m.ClosingTotal!.Value));
                        KeyValue(t, "Difference", Amount(m.Difference!.Value), bold: true);
                        foreach (var account in Vocab.Accounts)
                            KeyValue(t, $"    {account}: {Amount(m.Opening[account])} → {Amount(m.Closing[account])}", "");
                    }
                    else if (m.Closing is not null)
                    {
                        Note(t, 2, "First month with a recorded balance; nothing to reconcile against.");
                        foreach (var account in Vocab.Accounts) KeyValue(t, account, Amount(m.Closing[account]));
                    }
                    else Note(t, 2, "No closing balance recorded for this month.");
                }));
                if (m.Difference is { } diff && diff != 0)
                    col.Item().PaddingTop(-8).Text("The difference is explained in the notes, or it is an error still to be found.").FontSize(8).FontColor(Muted);

                if (m.Notes.Length > 0)
                    col.Item().Column(n =>
                    {
                        n.Item().PaddingBottom(4).Text("Notes").FontSize(13).SemiBold();
                        foreach (var paragraph in m.Notes.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
                            n.Item().PaddingBottom(4).Text(paragraph.Replace('\n', ' ').Trim());
                    });
            });

            page.Footer().Row(row =>
            {
                row.RelativeItem().Text($"Generated {generated:yyyy-MM-dd} from data/ in the finances repository; the CSV files are the source of truth.")
                    .FontSize(8).FontColor(Muted);
                row.ConstantItem(60).AlignRight().Text(t => { t.DefaultTextStyle(s => s.FontSize(8).FontColor(Muted)); t.CurrentPageNumber(); t.Span(" / "); t.TotalPages(); });
            });
        })).GeneratePdf();
    }

    private static void Section(IContainer container, string title, Action<TableDescriptor> table) =>
        container.Column(col =>
        {
            col.Item().PaddingBottom(4).Text(title).FontSize(13).SemiBold();
            col.Item().Table(table);
        });

    private static void HeaderRow(TableDescriptor t, string[] labels, int rightFrom, int rightTo = int.MaxValue)
    {
        t.Header(h =>
        {
            for (var i = 0; i < labels.Length; i++)
            {
                var cell = h.Cell().BorderBottom(0.75f).BorderColor(Ink).PaddingVertical(3).PaddingHorizontal(2);
                (i >= rightFrom && i <= rightTo ? cell.AlignRight() : cell).Text(labels[i]).FontSize(8).FontColor(Muted);
            }
        });
    }

    private static void Row(TableDescriptor t, string[] values, int rightFrom, int rightTo = int.MaxValue, bool bold = false)
    {
        for (var i = 0; i < values.Length; i++)
        {
            var cell = t.Cell().BorderBottom(0.5f).BorderColor(Line).PaddingVertical(3).PaddingHorizontal(2);
            var text = (i >= rightFrom && i <= rightTo ? cell.AlignRight() : cell).Text(values[i]).FontSize(9);
            if (bold) text.SemiBold();
        }
    }

    private static void KeyValue(TableDescriptor t, string key, string value, bool bold = false)
    {
        var k = t.Cell().BorderBottom(0.5f).BorderColor(Line).PaddingVertical(3).PaddingHorizontal(2).Text(key);
        var v = t.Cell().BorderBottom(0.5f).BorderColor(Line).PaddingVertical(3).PaddingHorizontal(2).AlignRight().Text(value);
        if (bold) { k.SemiBold(); v.SemiBold(); }
    }

    private static void Note(TableDescriptor t, int columns, string text) =>
        t.Cell().ColumnSpan((uint)columns).Background(Shade).Padding(6).Text(text).FontColor(Muted);

    private static string Amount(decimal amount) => Money.FormatGrouped(amount);

    private static string MonthName(Month month) =>
        month.First.ToString("MMMM yyyy", CultureInfo.InvariantCulture);
}

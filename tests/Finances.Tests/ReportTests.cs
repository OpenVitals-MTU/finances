using Finances.Report;

namespace Finances.Tests;

public sealed class ReportTests
{
    private static DataStore Fixture() => new(Path.Combine(AppContext.BaseDirectory, "fixture"));

    [Fact]
    public void Figures_reconcile_and_summarise()
    {
        var all = MonthFigures.LoadAll(Fixture());
        var october = all[1];
        Assert.Equal(120.98m, october.NetIncome);
        Assert.Equal(202.30m, october.TotalExpenses);
        Assert.Equal(-81.32m, october.Result);
        Assert.Equal(0.15m, october.Difference);  // 500 + 120.98 - 202.30 = 418.68; actual 418.83
        Assert.Equal("3.9 months", MonthFigures.Runway(all, 1));
        Assert.Equal("51.0 months", MonthFigures.Runway(all, 0));  // 500.00 / 9.80
        Assert.Equal(("devices", 180.00m), october.ByCategory[0]);
    }

    [Fact]
    public void Pdf_is_produced_for_a_full_and_for_an_empty_month()
    {
        var all = MonthFigures.LoadAll(Fixture());
        var pdf = PdfReport.Render(all[1], MonthFigures.Runway(all, 1), "OpenVitals MTÜ", new DateOnly(2026, 11, 10));
        Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString(pdf[..4]));
        Assert.True(pdf.Length > 5_000);

        var empty = new MonthFigures { Month = new Month(2026, 11), Currency = "EUR", Income = [], Expenses = [], Opening = null, Closing = null, Notes = "", Donations = [] };
        var emptyPdf = PdfReport.Render(empty, "—", "OpenVitals MTÜ", new DateOnly(2026, 12, 10));
        Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString(emptyPdf[..4]));
    }

    [Fact]
    public void Index_lists_months_newest_first_with_yearly_totals()
    {
        var index = IndexWriter.Render(MonthFigures.LoadAll(Fixture()));
        Assert.Contains("| [2026-10](2026/2026-10.pdf) | 120.98 | 202.30 | -81.32 | 418.83 |", index);
        Assert.Contains("| 2026 | 251.58 | 212.10 | 39.48 |", index);
        Assert.True(index.IndexOf("2026-10", StringComparison.Ordinal) < index.IndexOf("2026-09", StringComparison.Ordinal));
    }

    [Fact]
    public void Check_reports_missing_pdfs_stale_index_and_unclassified_rows()
    {
        var root = Path.Combine(Path.GetTempPath(), "finances-check-" + Guid.NewGuid().ToString("N"));
        CopyDirectory(Path.Combine(AppContext.BaseDirectory, "fixture"), root);
        try
        {
            var store = new DataStore(root);
            File.AppendAllText(store.ExpensesPath(new Month(2026, 10)), "2026-10-30,other,Some Shop,unclassified: add a rule,5.00,EUR,bank,\n");
            var problems = Checker.Run(store);
            Assert.Contains(problems, p => p.Contains("2026-10.pdf is missing"));
            Assert.Contains(problems, p => p.Contains("README.md differs"));
            Assert.Contains(problems, p => p.Contains("is unclassified"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Check_passes_on_a_repository_with_no_months_yet()
    {
        var root = Path.Combine(Path.GetTempPath(), "finances-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { Assert.Empty(Checker.Run(new DataStore(root))); }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void CopyDirectory(string from, string to)
    {
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }
}

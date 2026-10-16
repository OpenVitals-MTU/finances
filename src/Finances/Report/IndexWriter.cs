using System.Text;

namespace Finances.Report;

/// <summary>reports/README.md: one line per month linking to its PDF, plus yearly totals.</summary>
public static class IndexWriter
{
    public static string Render(IReadOnlyList<MonthFigures> months)
    {
        var text = new StringBuilder();
        text.Append("# Monthly reports\n\nGenerated from [`data/`](../data/) by the finances tool. Newest first.\n\n");
        text.Append("| month | net income | expenses | result | closing balance |\n|---|---:|---:|---:|---:|\n");
        foreach (var m in months.Reverse())
            text.Append($"| [{m.Month}]({m.Month.Year}/{m.Month}.pdf) | {Money.FormatGrouped(m.NetIncome)} | {Money.FormatGrouped(m.TotalExpenses)} | {Money.FormatGrouped(m.Result)} | {(m.ClosingTotal is { } c ? Money.FormatGrouped(c) : "—")} |\n");
        text.Append("\n## By year\n\n| year | net income | expenses | result |\n|---|---:|---:|---:|\n");
        foreach (var year in months.GroupBy(m => m.Month.Year).OrderByDescending(g => g.Key))
            text.Append($"| {year.Key} | {Money.FormatGrouped(year.Sum(m => m.NetIncome))} | {Money.FormatGrouped(year.Sum(m => m.TotalExpenses))} | {Money.FormatGrouped(year.Sum(m => m.Result))} |\n");
        return text.ToString();
    }
}

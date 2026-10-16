using Finances.Report;

namespace Finances;

/// <summary>What CI verifies on every pull request.</summary>
public static class Checker
{
    public static List<string> Run(DataStore store)
    {
        var problems = new List<string>();
        List<MonthFigures> months;
        try { months = MonthFigures.LoadAll(store); }
        catch (DataException e) { return [e.Message]; }
        if (months.Count == 0) return [];  // nothing published yet; the first monthly run creates the files
        foreach (var m in months)
        {
            if (!File.Exists(store.PdfPath(m.Month)))
                problems.Add($"reports/{m.Month.Year}/{m.Month}.pdf is missing; run `finances report`");
            foreach (var e in m.Expenses.Where(e => e.IsUnclassified))
                problems.Add($"data/expenses/{m.Month}.csv: {e.Date:yyyy-MM-dd} {Money.Format(e.Amount)} to {e.Payee} is unclassified; add a rule to config/rules.json and re-run the fetch");
        }
        var index = Path.Combine(store.ReportsDir, "README.md");
        if (!File.Exists(index) || File.ReadAllText(index) != IndexWriter.Render(months))
            problems.Add("reports/README.md differs from the data; run `finances report`");
        return problems;
    }
}

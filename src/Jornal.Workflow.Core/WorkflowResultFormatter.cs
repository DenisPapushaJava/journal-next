using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Jornal.Workflow.Core;

// These rules affect the letter only, never the saved cutting calculation.
public sealed record WorkflowResultPricing(bool UseMinimumPrice = true, decimal MinimumPrice = 500m)
{
    public WorkflowResultPricing Normalize() => MinimumPrice >= 0 && decimal.Truncate(MinimumPrice) == MinimumPrice
        ? this : this with { MinimumPrice = 500m };
}

public static partial class WorkflowResultFormatter
{
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("ru-RU");

    public static string Format(IEnumerable<SpecificationState> specifications,
        IReadOnlyList<WorkflowMachine> machines, WorkflowResultPricing? pricing = null)
    {
        pricing = (pricing ?? new()).Normalize();
        var saved = specifications.OrderBy(s => s.Parameters.ThicknessMm)
            .ThenBy(s => s.Parameters.Material, StringComparer.Create(Culture, ignoreCase: true)).ToArray();
        if (saved.Length == 0) return "";
        var names = machines.ToDictionary(m => m.Id, m => m.Name);
        var rows = saved.Select(s =>
        {
            var p = s.Parameters;
            var price = decimal.Round(s.Calculation.TotalCost, 0, MidpointRounding.ToEven);
            if (pricing.UseMinimumPrice) price = Math.Max(pricing.MinimumPrice, price);
            return new[] { p.ThicknessMm.ToString("0.###", Culture), p.Material, Number(p.BlankQuantity),
                p.WeightKg is { } weight ? Number(weight) : "—", price.ToString("0", Culture),
                (names.GetValueOrDefault(p.MachineId) ?? p.Machine).ToUpper(Culture) };
        }).ToArray();
        // Measure the displayed, rounded prices, not the original decimal amounts.
        var widths = Enumerable.Range(0, rows[0].Length).Select(c => rows.Max(r => r[c].Length)).ToArray();
        var result = new StringBuilder();
        for (var i = 0; i < rows.Length; i++)
        {
            var row = rows[i]; var p = saved[i].Parameters;
            for (var c = 0; c < row.Length; c++) row[c] = new string(' ', (widths[c] - row[c].Length) * 2) + row[c];
            result.Append(Blank(p, row[0])).Append(",\t   Материал: ").Append(row[1]).Append(",\t   ")
                .Append(row[2]).Append(" шт.\t   Вес ").Append(row[3]).Append(" кг.\t   Резка: ").Append(row[4]).Append(" ₽.");
            if (p.DurationSeconds is { } seconds)
            {
                var minutes = (long)Math.Ceiling(seconds / 60m);
                result.Append("     Время резки: ").Append((minutes / 60).ToString("00", Culture))
                    .Append(':').Append((minutes % 60).ToString("00", Culture));
            }
            result.Append("\t   ").Append(row[5]);
            if (p.HasChamfer) result.Append("   ФАСКА");
            if (p.HasBending) result.Append("   ГИБКА");
            result.Append('\n');
        }
        return result.ToString();
    }

    private static string Number(decimal value) => value.ToString("0.############################", Culture);
    private static string Blank(CuttingParameters p, string thickness)
    {
        // Imported blanks have this known shape. Preserve freely edited descriptions verbatim.
        var match = Sheet().Match(p.BlankSize);
        if (!match.Success || !decimal.TryParse(match.Groups[1].Value.Replace(',', '.'), NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out var parsed) || parsed != p.ThicknessMm) return p.BlankSize;
        return "Лист " + thickness + "x" + match.Groups[2].Value.Replace(',', '.') + "x" + match.Groups[3].Value.Replace(',', '.');
    }

    [GeneratedRegex(@"^Лист\s+([0-9]+(?:[.,][0-9]+)?)\s*[xXхХ×]\s*([0-9]+(?:[.,][0-9]+)?)\s*[xXхХ×]\s*([0-9]+(?:[.,][0-9]+)?)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Sheet();
}

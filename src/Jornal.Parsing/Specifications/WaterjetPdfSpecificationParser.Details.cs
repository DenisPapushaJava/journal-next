using Jornal.Parsing.Documents;

namespace Jornal.Parsing.Specifications;

public sealed partial class WaterjetPdfSpecificationParser
{
    private const string DetailIndex = "№|No.|No|#";
    private const string DetailWeight = "Вес|Weight";
    private const string DetailQuantity = "Кол-во|Quantity|Qty";
    private const string DetailPierces = "Pierce count|Number of pierces|Pierces|Piercings";

    private static bool IsDetailsHeader(PdfLayout.Line line) =>
        Find(line, DetailWeight) is not null && Find(line, DetailQuantity) is not null &&
        (Find(line, DetailIndex) is not null || Find(line, "Quality|Качество") is not null);

    private static SpecificationValue<int>? ReadDetailPunches(IReadOnlyList<PdfPage> pages, PdfPage summary, CancellationToken token)
    {
        // The CE details table lists quantities for the entire layout, including all sheets.
        var partNumbers = new HashSet<int>();
        var sources = new List<PdfValueSource>();
        long total = 0;
        foreach (var page in pages)
        {
            token.ThrowIfCancellationRequested();
            var lines = PdfLayout.Lines(page.Words, PdfLayout.Tolerance(page.Words));
            var headers = lines.Select((line, index) => (Line: line, Index: index))
                .Where(item => IsDetailsHeader(item.Line)).ToArray();
            if (headers.Length == 0)
            {
                if (page.Number == summary.Number || lines.Count > 0 && IsDetailNote(lines[0])) continue;
                return null;
            }
            if (headers.Length != 1 ||
                !ReadDetailPage(page, lines, headers[0].Index, partNumbers, sources, ref total, token)) return null;
        }
        return sources.Count == 0 ? null : new((int)total, sources);
    }

    private static bool IsDetailNote(PdfLayout.Line line) =>
        Key(line.Text).StartsWith("примечание", StringComparison.Ordinal) ||
        Key(line.Text).StartsWith("note:", StringComparison.Ordinal);

    private static bool ReadDetailPage(PdfPage page, IReadOnlyList<PdfLayout.Line> lines, int index,
        HashSet<int> partNumbers, List<PdfValueSource> sources, ref long total, CancellationToken token)
    {
        var header = lines[index];
        var weight = Find(header, DetailWeight)![0];
        var indexLabel = Find(header, DetailIndex)?.First();
        var quantity = Find(header, DetailQuantity);
        if (quantity is null || indexLabel is null) return false;
        var quantityLabel = quantity[0];
        var russianCounts = header.Words.Where(word => Key(word.Text) == "кол-во").ToArray();
        var punchLabel = russianCounts.Length == 2 ? russianCounts[1] : Find(header, DetailPierces)?.First();
        if (punchLabel is null || quantityLabel.Right >= punchLabel.Left || punchLabel.Right >= weight.Left) return false;

        var betweenCounts = (quantityLabel.Right + punchLabel.Left) / 2;
        var afterPunches = (punchLabel.Right + weight.Left) / 2;
        var beforeQuantity = quantityLabel.Left - (betweenCounts - quantityLabel.Right);
        bool InIndexColumn(PdfWord word) => Math.Abs(PdfLayout.CenterX(word) - PdfLayout.CenterX(indexLabel)) <=
            2 * (indexLabel.Right - indexLabel.Left);
        bool InCountColumns(PdfWord word) => PdfLayout.CenterX(word) >= beforeQuantity && PdfLayout.CenterX(word) < afterPunches;
        bool InPunchColumn(PdfWord word) => InCountColumns(word) && PdfLayout.CenterX(word) >= betweenCounts;

        var start = index + 1;
        if (russianCounts.Length == 2)
        {
            if (start >= lines.Count || !lines[start].Words.Any(word => Key(word.Text) == "пробивок" && InPunchColumn(word))) return false;
            start++;
        }
        var rows = lines.Skip(start).TakeWhile(line => !IsDetailNote(line))
            .Where(line => line.Words.Any(word => InIndexColumn(word) || InCountColumns(word))).ToArray();
        if (rows.Length == 0) return false;

        foreach (var row in rows)
        {
            token.ThrowIfCancellationRequested();
            var indices = row.Words.Where(InIndexColumn).ToArray();
            var quantities = row.Words.Where(word => InCountColumns(word) && !InPunchColumn(word)).ToArray();
            var punches = row.Words.Where(InPunchColumn).ToArray();
            if (indices.Length != 1 || !BlockValueParser.Integer(indices[0].Text, out var partNumber) || partNumber < 0 ||
                !partNumbers.Add(partNumber) || quantities.Length != 1 ||
                !BlockValueParser.Integer(quantities[0].Text, out var partQuantity) || partQuantity <= 0 ||
                punches.Length != 1 || !BlockValueParser.Integer(punches[0].Text, out var perPart) || perPart < 0)
                return false;

            total += (long)partQuantity * perPart;
            if (total > int.MaxValue) return false;
            sources.Add(PdfLayout.Evidence(page.Number, "Кол-во × кол-во пробивок", [quantities[0], punches[0]]));
        }
        return true;
    }
}

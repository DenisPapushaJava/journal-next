using System.Globalization;
using System.Text.RegularExpressions;
using Jornal.Parsing.Documents;

namespace Jornal.Parsing.Specifications;

// AWJC: только подписанные ячейки шапки и итогового блока. Страницы деталей,
// имена деталей и внешний путь не участвуют в выборе полей.
public sealed partial class WaterjetPdfSpecificationParser : ISpecificationProfile
{
    public const string Profile = "waterjet-awjc/v1";
    public string Id => Profile;
    public string DisplayName => "Гидроабразив AWJC";
    public DocumentKind DocumentKind => DocumentKind.Pdf;
    public int NormalizeCutLengthMeters(decimal millimeters) => HansSpecificationNormalizer.LegacyCutLengthMeters(millimeters);
    private sealed record Cell(string Text, PdfValueSource Source);

    public SpecificationParseResult Parse(SourceDocument document, CancellationToken cancellationToken = default)
    {
        SpecificationParseResult Empty(string code, string message, bool recognized = false) =>
            new(Profile, null, null, null, null, null, null, null, null, null, null, [new(SpecificationField.Header, code, message)]) { IsRecognized = recognized };
        if (document.Kind != DocumentKind.Pdf) return Empty("UnsupportedFormat", "Нужна спецификация AWJC в PDF.");
        if (document.Pages.SelectMany(page => page.Words).Any(word => !double.IsFinite(word.Left) || !double.IsFinite(word.Right) ||
            !double.IsFinite(word.Bottom) || !double.IsFinite(word.Top) || word.Left > word.Right || word.Bottom > word.Top))
            return Empty("InvalidCoordinates", "Повреждены координаты текста PDF.");
        var matches = new List<(PdfPage Page, IReadOnlyList<PdfLayout.Line> Lines, int Section)>();
        foreach (var page in document.Pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var lines = PdfLayout.Lines(page.Words, PdfLayout.Tolerance(page.Words));
            var section = lines.ToList().FindIndex(line => Key(line.Text) == "резка материал");
            if (section < 0) continue;
            var filename = lines.Take(section).Select(line => ReadCell(page, line, "Имя файла")).FirstOrDefault(cell => cell is not null);
            var summary = lines.Skip(section + 1).ToArray();
            if (filename?.Text.Contains("--AWJC", StringComparison.OrdinalIgnoreCase) == true &&
                new[] { "Время резки", "Общий раскрой", "Толщина", "Общий вес" }.Count(label => summary.Any(line => Find(line, label) is not null)) >= 3)
                matches.Add((page, lines, section));
        }
        if (matches.Count == 0) return Empty("Unrecognized", "Структура AWJC не найдена.");
        if (matches.Count != 1) return Empty("MultipleSummaries", "В PDF несколько раскроев. Разделите файл перед импортом.", true);
        var (sheet, allLines, sectionIndex) = matches[0];
        var issues = new List<SpecificationIssue>();
        var upper = allLines.Take(sectionIndex).ToArray(); var lower = allLines.Skip(sectionIndex + 1).ToArray();
        Cell? Unique(string label, IEnumerable<PdfLayout.Line> lines, string? stop = null)
        {
            var cells = lines.Select(line => ReadCell(sheet, line, label, stop)).Where(cell => cell is not null).Cast<Cell>().ToArray();
            if (cells.Length != 1) return null;
            return cells[0];
        }
        void Missing(SpecificationField field, string caption, Cell? cell = null) => issues.Add(new(field, "MissingField", "Проверьте: " + caption + ".",
            Sources: cell is null ? [] : [cell.Source]));
        var file = Unique("Имя файла", upper); SpecificationValue<SpecificationHeader>? header = null;
        if (file is not null)
        {
            var ids = Identifier().Matches(file.Text);
            if (ids.Count == 1 && DateOnly.TryParseExact(ids[0].Groups["date"].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                header = new(new(date, ids[0].Value, date.ToString("MMdd", CultureInfo.InvariantCulture) + "-" + ids[0].Groups["number"].Value, file.Text), [file.Source]);
        }
        if (header is null) Missing(SpecificationField.Header, "номер и дата раскроя", file);
        var materialCell = Unique("Материал", upper); SpecificationValue<string>? material = null; decimal? materialThickness = null;
        if (materialCell is not null)
        {
            var parsed = Material().Match(materialCell.Text);
            if (parsed.Success && BlockValueParser.Number(parsed.Groups["thickness"].Value, out var value))
            {
                materialThickness = value;
                var materialRow = upper.Single(line => Find(line, "Материал") is not null);
                var notes = sheet.TextAnnotations.Where(note => double.IsFinite(note.Left) && double.IsFinite(note.Right) &&
                    double.IsFinite(note.Bottom) && double.IsFinite(note.Top) && note.Left <= note.Right && note.Bottom <= note.Top && note.Bottom >= 0 && note.Top <= sheet.Height &&
                    note.Left >= Find(materialRow, "Материал")!.Max(word => word.Right) - 0.1 && note.Right <= sheet.Width &&
                    Math.Abs((note.Top + note.Bottom) / 2 - materialRow.Center) <= PdfLayout.Tolerance(sheet.Words) * 2)
                    .Select(note => new Cell(BlockValueParser.Space(note.Text), new(sheet.Number, note.Left, note.Bottom, note.Right, note.Top, "Материал (пометка PDF)", note.Text))).ToArray();
                var grades = notes.Select(note => note.Text).Concat(parsed.Groups["grade"].Success ? new[] { parsed.Groups["grade"].Value.Trim() } : [])
                    .Distinct(StringComparer.Ordinal).ToArray();
                var candidate = grades.Length == 1 ? grades[0] : parsed.Groups["category"].Value.Trim();
                var sources = new[] { materialCell.Source }.Concat(notes.Select(note => note.Source)).ToArray();
                if (grades.Length > 1) issues.Add(new(SpecificationField.Material, "ConflictingMaterialNotes", "В PDF несколько вариантов материала. Проверьте значение.", Sources: sources));
                if (candidate.Length is > 0 and <= 255 && !candidate.Any(char.IsControl))
                {
                    material = new(candidate, sources);
                    issues.Add(new(SpecificationField.Material, "MaterialNeedsReview", "Проверьте материал.", SpecificationIssueSeverity.Warning, sources));
                }
            }
        }
        if (material is null) Missing(SpecificationField.Material, "материал", materialCell);
        var lengthCell = Unique("Длинна реза", lower, "Толщина") ?? Unique("Длина реза", lower, "Толщина");
        SpecificationValue<decimal>? length = null;
        if (lengthCell is not null && Millimeters().Match(lengthCell.Text) is { Success: true } lm && BlockValueParser.Number(lm.Groups[1].Value, out var lengthValue))
        {
            try { _ = NormalizeCutLengthMeters(lengthValue); length = new(lengthValue, [lengthCell.Source]); }
            catch (ArgumentOutOfRangeException) { }
        }
        if (length is null) Missing(SpecificationField.CutLength, "длина реза", lengthCell);
        var thicknessCell = Unique("Толщина", lower);
        var sizeLines = lower.Where(line => line.Words.Any(word => word.Text.StartsWith("Раскрой:", StringComparison.OrdinalIgnoreCase))).ToArray();
        Cell? sizeCell = null;
        if (sizeLines.Length == 1)
        {
            var row = sizeLines[0]; var boundary = Find(row, "Количество")?.Min(word => word.Left) ?? sheet.Width;
            var physical = row.Words.Where(word => word.Right < boundary).ToArray();
            var raw = PdfLayout.Join(physical); var colon = raw.IndexOf(':');
            if (colon >= 0) sizeCell = new(raw[(colon + 1)..].Trim(), PdfLayout.Evidence(sheet.Number, "Раскрой", physical));
        }
        SpecificationValue<SheetDimensions>? dimensions = null;
        if (thicknessCell is not null && sizeCell is not null && BlockValueParser.Number(thicknessCell.Text, out var thickness) && thickness > 0 &&
            Size().Match(sizeCell.Text) is { Success: true } sm && BlockValueParser.Number(sm.Groups[1].Value, out var width) && width > 0 &&
            BlockValueParser.Number(sm.Groups[2].Value, out var height) && height > 0)
            dimensions = new(new(thickness, height, width), [thicknessCell.Source, sizeCell.Source]);
        if (dimensions is null) Missing(SpecificationField.Dimensions, "толщина и размер листа");
        if (dimensions is not null && materialThickness is { } mt && mt != dimensions.Value.ThicknessMillimeters)
            issues.Add(new(SpecificationField.Dimensions, "MaterialThicknessMismatch", "Толщина в материале отличается от толщины листа."));
        var weightCell = Unique("Общий вес", lower); SpecificationValue<decimal>? weight = null;
        if (weightCell is not null && BlockValueParser.Number(weightCell.Text, out var weightValue)) weight = new(weightValue, [weightCell.Source]);
        if (weight is null) Missing(SpecificationField.Weight, "общий вес", weightCell);
        SpecificationValue<int>? count = null;
        var quantityLines = lower.Where(line => Find(line, "Количество") is not null && Find(line, "Общий вес") is not null).ToArray();
        if (quantityLines.Length == 1)
        {
            var start = Find(quantityLines[0], "Количество")!; var end = Find(quantityLines[0], "Общий вес")!;
            var words = sheet.Words.Where(word => word.Left >= start[0].Left - 0.1 && word.Right <= end[0].Left &&
                word.Top <= quantityLines[0].Top + 0.1 && word.Bottom >= quantityLines[0].Bottom - PdfLayout.Tolerance(sheet.Words) * 5).ToArray();
            var evidence = PdfLayout.Evidence(sheet.Number, "Количество листов", words);
            var match = Count().Match(BlockValueParser.Space(evidence.RawText));
            if (match.Success && BlockValueParser.Integer(match.Groups[1].Value, out var n) && n > 0) count = new(n, [evidence]);
        }
        if (count is null) Missing(SpecificationField.SheetCount, "количество листов");
        var durationCell = Unique("Время резки", lower, "Общий раскрой"); SpecificationValue<TimeSpan>? duration = null;
        if (durationCell is not null && TryDuration(durationCell.Text, out var time))
            duration = new(time, [durationCell.Source]);
        if (duration is null) issues.Add(new(SpecificationField.CuttingDuration, "MissingDuration", "Время резки не распознано.", SpecificationIssueSeverity.Warning));
        return new(Profile, header, new("Гидроабразивная", file is null ? [] : [file.Source]), material, dimensions, count, length, weight, null, null, duration, issues)
        {
            IsRecognized = true,
            SheetLayouts = dimensions is null ? [] : [new(new(dimensions.Value, count?.Value), dimensions.Sources)]
        };
    }

    private static string Key(string text) => BlockValueParser.Space(text).ToLowerInvariant();
    private static bool TryDuration(string text, out TimeSpan value)
    {
        value = default; var match = Time().Match(text);
        if (!match.Success || !long.TryParse(match.Groups["hours"].Value, out var hours) ||
            !int.TryParse(match.Groups["minutes"].Value, out var minutes) || minutes > 59 ||
            !int.TryParse(match.Groups["seconds"].Value, out var seconds) || seconds > 59) return false;
        if (match.Groups["days"].Success)
        {
            if (hours > 23 || !long.TryParse(match.Groups["days"].Value, out var days)) return false;
            try { hours = checked(days * 24 + hours); } catch (OverflowException) { return false; }
        }
        try { value = TimeSpan.FromTicks(checked((hours * 3600 + minutes * 60 + seconds) * TimeSpan.TicksPerSecond)); return true; }
        catch (OverflowException) { return false; }
    }
    private static PdfWord[]? Find(PdfLayout.Line line, string label)
    {
        var parts = Key(label).Split(' ');
        for (var i = 0; i + parts.Length <= line.Words.Count; i++)
            if (parts.Select((part, index) => Key(line.Words[i + index].Text) == part).All(value => value)) return line.Words.Skip(i).Take(parts.Length).ToArray();
        return null;
    }
    private static Cell? ReadCell(PdfPage page, PdfLayout.Line line, string label, string? stop = null)
    {
        var words = Find(line, label); if (words is null) return null;
        var left = words.Max(word => word.Right); var right = stop is null ? page.Width : Find(line, stop)?.Min(word => word.Left) ?? page.Width;
        var value = line.Words.Where(word => word.Left >= left - 0.1 && word.Right <= right).ToArray();
        return new(PdfLayout.Join(value), PdfLayout.Evidence(page.Number, label, value, words));
    }
    [GeneratedRegex(@"(?<![0-9])(?<date>[0-9]{4}-[0-9]{2}-[0-9]{2})-{1,2}(?<number>[0-9]{3,4})(?=--AWJC)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex Identifier();
    [GeneratedRegex(@"^(?<category>.+?)\s+(?<thickness>[0-9]+(?:[.,][0-9]+)?)\s*(?:mm|мм)(?:\s+(?<grade>.+))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex Material();
    [GeneratedRegex(@"^([0-9]+(?:[.,][0-9]+)?)\s*(?:mm|мм)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex Millimeters();
    [GeneratedRegex(@"^([0-9]+(?:[.,][0-9]+)?)\s*[xх×]\s*([0-9]+(?:[.,][0-9]+)?)\s*(?:mm|мм)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex Size();
    [GeneratedRegex(@"^Количество\s+листов:\s*([0-9]+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex Count();
    [GeneratedRegex(@"^(?:(?<days>[0-9]+)\.)?(?<hours>[0-9]+):(?<minutes>[0-9]{2}):(?<seconds>[0-9]{2})(?:\s*\([0-9]+(?:[.,][0-9]+)?\))?$", RegexOptions.CultureInvariant)] private static partial Regex Time();
}

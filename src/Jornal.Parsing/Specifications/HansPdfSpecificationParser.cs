using System.Globalization;
using System.Text.RegularExpressions;
using Jornal.Parsing.Documents;

namespace Jornal.Parsing.Specifications;

public sealed partial class HansPdfSpecificationParser : ISpecificationProfile
{
    public const string Profile = "hans-pdf/v1";
    public string Id => Profile;
    public string DisplayName => "HansLaser";
    public DocumentKind DocumentKind => DocumentKind.Pdf;
    public int NormalizeCutLengthMeters(decimal millimeters) => HansSpecificationNormalizer.LegacyCutLengthMeters(millimeters);
    private sealed record Label(string Key, IReadOnlyList<PdfWord> Words)
    {
        public double Left => Words.Min(word => word.Left);
        public double Right => Words.Max(word => word.Right);
        public double Top => Words.Max(word => word.Top);
        public double Center => (Left + Right) / 2;
    }
    private sealed record Candidate(string Text, PdfValueSource Source);
    private delegate bool TryValue<T>(string text, out T value);
    private static readonly IReadOnlyDictionary<string, SpecificationField?> Summary = new Dictionary<string, SpecificationField?>
    {
        ["заказ"] = SpecificationField.Header, ["вес"] = SpecificationField.Weight, ["вескг"] = SpecificationField.Weight,
        ["листов"] = SpecificationField.SheetCount, ["длинарезамм"] = SpecificationField.CutLength, ["длинарезам"] = SpecificationField.CutLength,
        ["общиеколводеталей"] = SpecificationField.PartCount, ["общееколводеталей"] = SpecificationField.PartCount,
        ["общееколичестводеталей"] = SpecificationField.PartCount, ["пробивки"] = SpecificationField.PunchCount,
        ["времяобработкивсего"] = SpecificationField.CuttingDuration, ["использованная"] = null,
        ["использованнаяплощадь"] = null
    };
    private static readonly IReadOnlyDictionary<string, string> Columns = new Dictionary<string, string>
    {
        ["№"] = "index", ["размерx"] = "x", ["размерxмм"] = "x", ["размерy"] = "y", ["размерyмм"] = "y",
        ["материал"] = "material", ["gas"] = "gas", ["газ"] = "gas", ["толщмм"] = "thickness",
        ["толщ"] = "thickness", ["толщинамм"] = "thickness", ["толщина"] = "thickness",
        ["кво"] = "count", ["колво"] = "count", ["количество"] = "count"
    };

    public SpecificationParseResult Parse(SourceDocument document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        cancellationToken.ThrowIfCancellationRequested();
        var issues = new List<SpecificationIssue>();
        SpecificationParseResult Empty(string code, string message) => new(Profile, null, null, null, null, null, null, null, null, null, null,
            [new(SpecificationField.Header, code, message)]);
        if (document.Kind != DocumentKind.Pdf) return Empty("UnsupportedFormat", "Этот профиль предназначен для PDF HansLaser.");
        var words = document.Pages.SelectMany(page => page.Words).Where(word => !string.IsNullOrWhiteSpace(word.Text)).ToArray();
        if (words.Length == 0) return Empty("MissingTextLayer", "В PDF нет доступного текста. Нужен текстовый отчёт HansLaser; скан автоматически не распознаётся.");
        if (words.Any(word => !double.IsFinite(word.Left) || !double.IsFinite(word.Right) || !double.IsFinite(word.Top) ||
            !double.IsFinite(word.Bottom) || word.Left > word.Right || word.Bottom > word.Top))
            return Empty("InvalidCoordinates", "Повреждена структура текста PDF. Автоматическое заполнение остановлено.");

        var candidates = Enum.GetValues<SpecificationField>().ToDictionary(field => field, _ => new List<Candidate>());
        var layouts = new List<SpecificationValue<SheetLayout>>();
        var counts = new List<int>(); var rowCount = 0; var foundSheetHeader = false;
        var hasSummaryStructure = false;
        foreach (var page in document.Pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tolerance = PdfLayout.Tolerance(page.Words);
            var lines = PdfLayout.Lines(page.Words, tolerance);
            var section = lines.ToList().FindIndex(IsSection);
            var summaryLines = lines.Take(section < 0 ? lines.Count : section).ToArray();
            var summaryLabels = summaryLines.SelectMany(line => Labels(line, key => Summary.ContainsKey(key) ? key : null)).Select(label => label.Key).ToArray();
            hasSummaryStructure |= summaryLabels.Contains("заказ") && new[] { "длинарезамм", "длинарезам", "общиеколводеталей", "общееколводеталей", "общееколичестводеталей", "пробивки" }
                .Where(summaryLabels.Contains).Select(label => Summary[label]).Distinct().Count() >= 2;
            ReadSummary(page, summaryLines, tolerance, candidates, issues);
            var inDetails = false;
            for (var index = 0; index < lines.Count; index++)
            {
                if (IsDetails(lines[index])) { inDetails = true; continue; }
                if (Key(lines[index].Text) == "листы") { inDetails = false; continue; }
                if (inDetails) continue;
                var labels = Labels(lines[index], key => Columns.GetValueOrDefault(key));
                if (!new[] { "x", "y", "material", "gas", "thickness", "count" }.All(key => labels.Any(label => label.Key == key))) continue;
                foundSheetHeader = true;
                if (labels.GroupBy(label => label.Key).Any(group => group.Count() != 1))
                { issues.Add(new(SpecificationField.Dimensions, "AmbiguousColumns", "В таблице листов повторяются заголовки колонок. Проверьте PDF.")); continue; }
                var end = index + 1;
                while (end < lines.Count && !IsSection(lines[end])) end++;
                ReadSheets(page.Number, labels, lines.Skip(index + 1).Take(end - index - 1).ToArray(), lines[index].Bottom,
                    tolerance, candidates, layouts, counts, ref rowCount, issues, cancellationToken);
                index = end - 1;
            }
        }
        if (!foundSheetHeader) issues.Add(new(SpecificationField.Dimensions, "MissingSheetTable",
            "Не найдена таблица «Листы» со всеми колонками размера, материала, газа, толщины и количества."));

        var header = Resolve<SpecificationHeader>(SpecificationField.Header, candidates, TryHeader, issues);
        var cutting = header is null ? null : new SpecificationValue<string>("HansLaser", header.Sources);
        var material = Resolve<string>(SpecificationField.Material, candidates, TryMaterialName, issues);
        var materialId = Resolve<string>(SpecificationField.MaterialId, candidates, TryMaterialId, issues);
        var gas = Resolve<string>(SpecificationField.CuttingGas, candidates, TryGas, issues);
        var sheets = Resolve<int>(SpecificationField.SheetCount, candidates, PositiveInteger, issues);
        var length = Resolve<decimal>(SpecificationField.CutLength, candidates, BlockValueParser.Number, issues);
        var weight = Resolve<decimal>(SpecificationField.Weight, candidates, BlockValueParser.Number, issues);
        var parts = Resolve<int>(SpecificationField.PartCount, candidates, PositiveInteger, issues);
        var punches = Resolve<int>(SpecificationField.PunchCount, candidates, BlockValueParser.Integer, issues);
        var duration = Resolve<TimeSpan>(SpecificationField.CuttingDuration, candidates, BlockValueParser.Duration, issues, optional: true);
        var dimensions = layouts.Select(layout => layout.Value.Dimensions).Distinct().ToArray();
        if (dimensions.Length == 0) issues.Add(new(SpecificationField.Dimensions, "MissingField", "Не найдены достоверные габариты листов."));
        if (dimensions.Select(size => size.ThicknessMillimeters).Distinct().Count() > 1)
            issues.Add(new(SpecificationField.Dimensions, "ConflictingThickness", "В PDF разные толщины листов. Одна заявка хранит одну толщину."));
        if (BlockSpecificationNormalizer.SheetSizes(dimensions).Length > 255)
            issues.Add(new(SpecificationField.Dimensions, "SheetSizeTooLong", "Перечень размеров превышает 255 символов MDB; значение не сокращено."));
        if (sheets is not null && counts.Count == rowCount && rowCount > 0 && counts.Sum(count => (long)count) != sheets.Value)
            issues.Add(new(SpecificationField.SheetCount, "SheetCountMismatch", "Сумма «К-во» в таблице листов не совпадает с итогом «Листов».",
                Sources: layouts.SelectMany(layout => layout.Sources).Concat(sheets.Sources).ToArray()));
        if (length is not null)
        {
            try { _ = HansSpecificationNormalizer.LegacyCutLengthMeters(length.Value); }
            catch (ArgumentOutOfRangeException) { issues.Add(new(SpecificationField.CutLength, "LengthOutOfRange", "Длина реза не помещается в прежнее поле метров MDB.")); }
        }
        return new(Profile, header, cutting, material, dimensions.Length == 1 ? new(dimensions[0], layouts.SelectMany(layout => layout.Sources).ToArray()) : null,
            sheets, length, weight, parts, punches, duration, issues)
            {
                SheetLayouts = layouts, MaterialId = materialId, CuttingGas = gas,
                IsRecognized = hasSummaryStructure && (foundSheetHeader || candidates[SpecificationField.Header].Any(candidate =>
                    candidate.Text.Contains("HANSLASER", StringComparison.OrdinalIgnoreCase)))
            };
    }

    private static void ReadSummary(PdfPage page, IReadOnlyList<PdfLayout.Line> lines, double tolerance,
        Dictionary<SpecificationField, List<Candidate>> candidates, List<SpecificationIssue> issues)
    {
        var labels = lines.SelectMany(line => Labels(line, key => Summary.ContainsKey(key) ? key : null)).ToArray();
        // Границы колонок берём из подписей; положение страницы и масштаб значения не имеют.
        var groups = new List<List<Label>>();
        foreach (var label in labels.OrderBy(label => label.Left))
        {
            if (groups.Count == 0 || label.Left - groups[^1][^1].Left > tolerance * 6) groups.Add([]);
            groups[^1].Add(label);
        }
        if (groups.Count > 2)
        { issues.Add(new(SpecificationField.Header, "AmbiguousSummary", "Неоднозначное расположение итогов PDF. Проверьте поля вручную.")); return; }
        var words = lines.SelectMany(line => line.Words).ToArray();
        for (var column = 0; column < groups.Count; column++)
        {
            var group = groups[column].OrderByDescending(label => label.Top).ToArray();
            var right = column + 1 < groups.Count ? groups[column + 1].Min(label => label.Left) - tolerance : page.Width;
            for (var index = 0; index < group.Length; index++)
            {
                var label = group[index];
                if (Summary[label.Key] is not { } field) continue;
                var bottom = index + 1 < group.Length ? group[index + 1].Top + tolerance / 2 : 0;
                var selected = words.Where(word => word.Left >= label.Right && PdfLayout.CenterX(word) < right &&
                    PdfLayout.CenterY(word) <= label.Top + tolerance && PdfLayout.CenterY(word) > bottom).ToArray();
                var source = PdfLayout.Evidence(page.Number, PdfLayout.Join(label.Words), selected, label.Words);
                var text = source.RawText;
                if (field == SpecificationField.CutLength && label.Key == "длинарезам" && BlockValueParser.Number(text, out var meters))
                {
                    try { text = checked(meters * 1000).ToString(CultureInfo.InvariantCulture); }
                    catch (OverflowException) { text = ""; }
                }
                candidates[field].Add(new(text, source));
            }
        }
    }

    private static void ReadSheets(int page, IReadOnlyList<Label> labels, IReadOnlyList<PdfLayout.Line> lines, double headerBottom,
        double tolerance, Dictionary<SpecificationField, List<Candidate>> candidates, List<SpecificationValue<SheetLayout>> layouts,
        List<int> counts, ref int rowCount, List<SpecificationIssue> issues, CancellationToken cancellationToken)
    {
        lines = lines.Where(line => !line.Words.All(word => Key(word.Text) == "мм")).ToArray();
        var ordered = labels.OrderBy(label => label.Center).ToArray();
        var bounds = ordered.Select((label, index) => (label.Key, Left: index == 0 ? double.NegativeInfinity : (ordered[index - 1].Center + label.Center) / 2,
            Right: index == ordered.Length - 1 ? double.PositiveInfinity : (label.Center + ordered[index + 1].Center) / 2))
            .ToDictionary(item => item.Key, item => (item.Left, item.Right));
        PdfWord[] Cell(IEnumerable<PdfWord> words, string key) => words.Where(word => PdfLayout.CenterX(word) > bounds[key].Left && PdfLayout.CenterX(word) < bounds[key].Right).ToArray();
        var seeds = lines.Where(line => new[] { "x", "y", "thickness", "count" }.Any(key => BlockValueParser.Number(PdfLayout.Join(Cell(line.Words, key)), out _))).ToArray();
        for (var index = 0; index < seeds.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested(); rowCount++;
            var top = index == 0 ? headerBottom - tolerance : (seeds[index - 1].Center + seeds[index].Center) / 2;
            var bottom = index + 1 == seeds.Length ? double.NegativeInfinity : (seeds[index].Center + seeds[index + 1].Center) / 2;
            var row = lines.SelectMany(line => line.Words).Where(word => PdfLayout.CenterY(word) < top && PdfLayout.CenterY(word) > bottom).ToArray();
            Candidate Value(string key)
            {
                var selected = Cell(row, key);
                var source = PdfLayout.Evidence(page, PdfLayout.Join(labels.Single(label => label.Key == key).Words), selected, seeds[index].Words);
                return new(source.RawText, source);
            }
            var x = Value("x"); var y = Value("y"); var thickness = Value("thickness"); var count = Value("count");
            if (BlockValueParser.Number(x.Text, out var length) && length > 0 && BlockValueParser.Number(y.Text, out var width) && width > 0 &&
                BlockValueParser.Number(thickness.Text, out var t) && t > 0)
            {
                int? quantity = PositiveInteger(count.Text, out var parsed) ? parsed : null;
                if (quantity is { } valid) counts.Add(valid);
                else issues.Add(new(SpecificationField.SheetCount, "InvalidLayoutCount", "В колонке «К-во» нужно целое положительное количество.", Sources: [count.Source]));
                layouts.Add(new(new(new(t, length, width), quantity), [x.Source, y.Source, thickness.Source, count.Source]));
            }
            else issues.Add(new(SpecificationField.Dimensions, "InvalidLayout", "Некорректные габариты или толщина в таблице листов.", Sources: [x.Source, y.Source, thickness.Source]));
            var material = Value("material");
            candidates[SpecificationField.Material].Add(material);
            candidates[SpecificationField.MaterialId].Add(material);
            candidates[SpecificationField.CuttingGas].Add(Value("gas"));
        }
    }

    private static IReadOnlyList<Label> Labels(PdfLayout.Line line, Func<string, string?> lookup)
    {
        var result = new List<Label>();
        for (var index = 0; index < line.Words.Count; index++)
        {
            for (var count = Math.Min(8, line.Words.Count - index); count > 0; count--)
            {
                var words = line.Words.Skip(index).Take(count).ToArray();
                if (lookup(Key(PdfLayout.Join(words))) is not { } key) continue;
                result.Add(new(key, words)); index += count - 1; break;
            }
        }
        return result;
    }

    private static bool IsDetails(PdfLayout.Line line) => Key(line.Text) == "детали" ||
        line.Words.Any(word => Key(word.Text) == "имя") && line.Words.Any(word => Key(word.Text) == "вид");
    private static bool IsSection(PdfLayout.Line line) => Key(line.Text) is "листы" or "примечание" || IsDetails(line) ||
        line.Text.TrimStart().StartsWith("Страница ", StringComparison.OrdinalIgnoreCase);
    private static string Key(string text) => new(text.Where(character => char.IsLetterOrDigit(character) || character == '№').Select(char.ToLowerInvariant).ToArray());
    private static bool PositiveInteger(string text, out int value) => BlockValueParser.Integer(text, out value) && value > 0;
    private static SpecificationValue<T>? Resolve<T>(SpecificationField field, Dictionary<SpecificationField, List<Candidate>> candidates,
        TryValue<T> parse, List<SpecificationIssue> issues, bool optional = false)
    {
        var found = candidates[field]; var severity = optional ? SpecificationIssueSeverity.Warning : SpecificationIssueSeverity.Error;
        var sources = found.Select(candidate => candidate.Source).ToArray();
        if (found.Count == 0) { issues.Add(new(field, "MissingField", $"Не найдено поле HansLaser: {Caption(field)}.", severity)); return null; }
        var values = new List<T>();
        foreach (var candidate in found)
            if (parse(candidate.Text, out var value)) values.Add(value);
        if (values.Count != found.Count)
        { issues.Add(new(field, "InvalidValue", $"Некорректное значение HansLaser: {Caption(field)}.", severity, sources)); return null; }
        if (values.Distinct().Count() != 1)
        { issues.Add(new(field, "ConflictingValues", $"В PDF разные значения: {Caption(field)}. Автоматический выбор остановлен.", severity, sources)); return null; }
        return new(values[0], sources);
    }

    private static bool TryHeader(string text, out SpecificationHeader value)
    {
        value = null!; var matches = Identifier().Matches(text);
        if (matches.Count != 1) return false;
        var match = matches[0]; var id = new string(match.Groups["id"].Value.Where(character => !char.IsWhiteSpace(character)).ToArray());
        if (!DateOnly.TryParseExact(id[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) || date.Year < 100) return false;
        var number = date.ToString("MMdd", CultureInfo.InvariantCulture) + "-" + match.Groups["number"].Value;
        if (number.Length > 255) return false;
        value = new(date, id, number, text); return true;
    }
    private static bool TryMaterialName(string text, out string value)
    {
        var match = MaterialSuffix().Match(text.Trim());
        value = match.Success && TryMaterialId(text, out _) ? match.Groups[1].Value : text.Trim();
        return value.Length is > 0 and <= 255 && !value.Any(char.IsWhiteSpace) && !value.Any(char.IsControl);
    }
    private static bool TryMaterialId(string text, out string value)
    {
        var match = MaterialSuffix().Match(text.Trim()); value = match.Success ? match.Groups[2].Value : "";
        return match.Success && value is "-1" or "0" or "1" or "2";
    }
    private static bool TryGas(string text, out string value)
    { value = text.Trim(); return value.Length is > 0 and <= 255 && !value.Any(char.IsWhiteSpace) && !value.Any(char.IsControl); }
    private static string Caption(SpecificationField field) => field switch
    {
        SpecificationField.Header => "номер и дата заказа", SpecificationField.Material => "материал", SpecificationField.MaterialId => "ID материала",
        SpecificationField.CuttingGas => "газ", SpecificationField.SheetCount => "количество листов", SpecificationField.CutLength => "длина реза",
        SpecificationField.Weight => "вес", SpecificationField.PartCount => "количество деталей", SpecificationField.PunchCount => "пробивки", _ => "время резки"
    };
    [GeneratedRegex(@"(?<![0-9])(?<id>[0-9]{4}\s*-\s*[0-9]{2}\s*-\s*[0-9]{2}\s*-{1,2}\s*(?<number>[0-9]+))\s*--\s*HANSLASER(?:\.Dsp)?(?=$|[\s.])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Identifier();
    [GeneratedRegex(@"^(\S+)\s*_(-?[0-9]+)$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex MaterialSuffix();
}

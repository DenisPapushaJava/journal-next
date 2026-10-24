using System.Globalization;
using System.Text.RegularExpressions;
using Jornal.Parsing.Documents;

namespace Jornal.Parsing.Specifications;

public sealed partial class BlockHtmlSpecificationParser : ISpecificationProfile
{
    public const string Profile = "block-html/v1";
    public string Id => Profile;
    public string DisplayName => "Vintech";
    public DocumentKind DocumentKind => DocumentKind.Html;
    public int NormalizeCutLengthMeters(decimal millimeters) => BlockSpecificationNormalizer.LegacyCutLengthMeters(millimeters);
    private sealed record Cell(int Table, int Row, int Column, string Raw, string Text);
    private sealed record Pair(Cell Label, Cell Value, string Text, string Unit = "");
    private sealed record Material(string Name, decimal? Thickness);
    private delegate bool TryValue<T>(string text, out T value);

    public SpecificationParseResult Parse(SourceDocument document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        cancellationToken.ThrowIfCancellationRequested();
        var issues = new List<SpecificationIssue>();
        var prefix = document.HtmlText?.TrimStart() ?? "";
        if (document.Kind != DocumentKind.Html || prefix.StartsWith('@') || prefix.StartsWith("*@", StringComparison.Ordinal))
            return Unsupported("Этот профиль предназначен для HTML БЛОК/Wintech. Признаки HansLaser нельзя подменять другим станком.");

        var tables = document.Tables.Select((table, t) => table.Rows.Select((row, r) => row.Cells
            .Select((cell, c) => new Cell(t, r, c, cell.Text, BlockValueParser.Space(cell.Text))).ToArray()).ToArray()).ToArray();
        var headers = new List<Pair>(); var sheets = new List<Pair>(); var dimensions = new List<Pair>(); var materials = new List<Pair>();
        var summary = new[] { SpecificationField.PartCount, SpecificationField.PunchCount, SpecificationField.CutLength,
            SpecificationField.Weight, SpecificationField.CuttingDuration }.ToDictionary(field => field, _ => new List<Pair>());
        var sheetSection = false; var detailSection = false; var totalSection = false;
        foreach (var table in tables)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var detailTable = table.Any(row => row.Any(cell => Is(cell.Text, "Одна деталь") || Is(cell.Text, "Все детали")));
            var totalRow = Array.FindIndex(table, row => row.Any(cell => LabelKey(cell.Text) == "всего по заказу"));
            if (detailTable)
            {
                detailSection |= table.Any(row => row.Any(cell => Is(cell.Text, "Одна деталь"))) &&
                    table.Any(row => row.Any(cell => Is(cell.Text, "Все детали")));
                foreach (var row in table)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var single = Array.FindLastIndex(row, cell => Is(cell.Text, "Одна деталь"));
                    if (single < 0) continue;
                    // Название детали находится раньше реквизитов. Берём ближайшую пару перед «Одна деталь»,
                    // поэтому даже название «Материал: ...» не становится полем заявки.
                    var material = Pairs(row, SpecificationField.Material).LastOrDefault(pair => pair.Value.Column < single);
                    if (material is not null) materials.Add(material);
                }
                continue;
            }
            if (totalRow >= 0)
            {
                var labels = table.Skip(totalRow).SelectMany(row => row).Select(cell => Field(cell.Text)).Where(field => field.HasValue).Distinct().ToArray();
                totalSection |= labels.Length >= 2 && labels.Any(field => field is SpecificationField.CutLength or SpecificationField.PartCount or SpecificationField.PunchCount);
                foreach (var row in table.Skip(totalRow))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    foreach (var (field, candidates) in summary) candidates.AddRange(Pairs(row, field));
                }
                continue;
            }
            foreach (var row in table)
            {
                foreach (var cell in row)
                {
                    var header = HeaderLabel().Match(cell.Text);
                    if (header.Success) headers.Add(new(cell, cell, header.Groups[1].Value));
                }
                // Метаданные листа определяются подписями, а не порядком таблиц или колонок.
                sheets.AddRange(Pairs(row, SpecificationField.SheetCount));
                dimensions.AddRange(Pairs(row, SpecificationField.Dimensions));
                materials.AddRange(Pairs(row, SpecificationField.Material));
            }
            var metadata = table.SelectMany(row => row).Select(cell => Field(cell.Text)).ToArray();
            sheetSection |= metadata.Contains(SpecificationField.SheetCount) && metadata.Contains(SpecificationField.Dimensions);
        }
        if (headers.Count == 0 && summary.Values.All(candidates => candidates.Count == 0))
            return Unsupported("Не найдены заголовок и итоговый раздел спецификации БЛОК. Документ не распознан.");

        var headerValue = Resolve<SpecificationHeader>(SpecificationField.Header, headers, TryHeader, issues);
        var cuttingType = Resolve<string>(SpecificationField.CuttingType, headers, TryCuttingType, issues);
        var layouts = ReadLayouts(dimensions, issues);
        var uniqueDimensions = layouts.Select(layout => layout.Value.Dimensions).Distinct().ToArray();
        SpecificationValue<SheetDimensions>? dimensionsValue = uniqueDimensions.Length == 1
            ? new(uniqueDimensions[0], layouts.SelectMany(layout => layout.Sources).ToArray()) : null;
        if (uniqueDimensions.Select(size => size.ThicknessMillimeters).Distinct().Count() > 1)
            issues.Add(new(SpecificationField.Dimensions, "ConflictingThickness",
                "В спецификации разные толщины листа. Одна заявка MDB хранит одну толщину; требуется проверка документа.",
                Sources: layouts.SelectMany(layout => layout.Sources).ToArray()));
        if (BlockSpecificationNormalizer.SheetSizes(uniqueDimensions).Length > 255)
            issues.Add(new(SpecificationField.Dimensions, "SheetSizeTooLong",
                "Перечень размеров превышает 255 символов поля MDB. Значение не сокращено; требуется проверка.",
                Sources: layouts.SelectMany(layout => layout.Sources).ToArray()));
        var materialValue = Resolve<string>(SpecificationField.Material, materials, TryMaterialName, issues);
        if (materials.Any(pair => TryMaterial(pair.Text, out var material) && material.Thickness is { } thickness &&
            uniqueDimensions.Any(size => size.ThicknessMillimeters != thickness)))
            issues.Add(new(SpecificationField.Material, "MaterialThicknessMismatch", "Толщина в материале отличается от габаритов листа. Проверьте документ."));
        var sheetValue = Resolve<int>(SpecificationField.SheetCount, sheets, PositiveInteger, issues);
        if (sheetValue is not null && layouts.Count > 0 && layouts.All(layout => layout.Value.SheetCount.HasValue) && uniqueDimensions.Length == layouts.Count &&
            layouts.Sum(layout => (long)layout.Value.SheetCount!.Value) != sheetValue.Value)
            issues.Add(new(SpecificationField.SheetCount, "SheetCountMismatch", "Количество листов по размерам не совпадает с общим количеством. Проверьте документ.",
                Sources: layouts.SelectMany(layout => layout.Sources).Concat(sheetValue.Sources).ToArray()));
        var length = Resolve<decimal>(SpecificationField.CutLength, summary[SpecificationField.CutLength]
            .Select(ConvertLength), BlockValueParser.Number, issues);
        var weight = Resolve<decimal>(SpecificationField.Weight, summary[SpecificationField.Weight], BlockValueParser.Number, issues);
        var parts = Resolve<int>(SpecificationField.PartCount, summary[SpecificationField.PartCount], PositiveInteger, issues);
        var punches = Resolve<int>(SpecificationField.PunchCount, summary[SpecificationField.PunchCount], BlockValueParser.Integer, issues);
        var duration = Resolve<TimeSpan>(SpecificationField.CuttingDuration, summary[SpecificationField.CuttingDuration], BlockValueParser.Duration, issues, optional: true);
        if (length is not null)
        {
            try { _ = BlockSpecificationNormalizer.LegacyCutLengthMeters(length.Value); }
            catch (ArgumentOutOfRangeException) { issues.Add(new(SpecificationField.CutLength, "LengthOutOfRange", "Длина реза не помещается в прежнее поле метров MDB.")); }
        }
        return new(Profile, headerValue, cuttingType, materialValue,
            dimensionsValue, sheetValue, length, weight, parts, punches, duration, issues)
        {
            SheetLayouts = layouts,
            IsRecognized = headers.Count > 0 && (totalSection || sheetSection && detailSection) || sheetSection && detailSection && totalSection
        };
    }

    private static IReadOnlyList<SpecificationValue<SheetLayout>> ReadLayouts(IReadOnlyList<Pair> pairs, List<SpecificationIssue> issues)
    {
        var layouts = new List<SpecificationValue<SheetLayout>>();
        if (pairs.Count == 0) issues.Add(new(SpecificationField.Dimensions, "MissingField", "Не найдено поле: габариты листа."));
        foreach (var pair in pairs)
        {
            var source = new HtmlValueSource(pair.Value.Table, pair.Value.Row, pair.Value.Column, pair.Label.Raw, pair.Value.Raw);
            var matches = SheetLayoutPattern().Matches(pair.Text);
            var residue = SheetLayoutPattern().Replace(pair.Text, "").Trim(' ', '\t', '\r', '\n', ';');
            var valid = matches.Count > 0 && residue.Length == 0;
            foreach (Match match in matches)
            {
                if (!BlockValueParser.Dimensions(match.Groups["dimensions"].Value, out var size)) { valid = false; continue; }
                int? count = null;
                if (match.Groups["count"].Success)
                {
                    if (!PositiveInteger(match.Groups["count"].Value, out var number)) { valid = false; continue; }
                    count = number;
                }
                layouts.Add(new(new(size, count), [source]));
            }
            if (!valid) issues.Add(new(SpecificationField.Dimensions, "InvalidValue", "Некорректное значение: габариты листа. Проверьте исходные ячейки.", Sources: [source]));
        }
        return layouts;
    }

    private static SpecificationParseResult Unsupported(string message) => new(Profile, null, null, null, null, null, null, null, null, null, null,
        [new(SpecificationField.Header, "UnsupportedFormat", message)]);

    private static SpecificationValue<T>? Resolve<T>(SpecificationField field, IEnumerable<Pair> candidates, TryValue<T> parse,
        List<SpecificationIssue> issues, bool optional = false)
    {
        var pairs = candidates.ToArray();
        var severity = optional ? SpecificationIssueSeverity.Warning : SpecificationIssueSeverity.Error;
        if (pairs.Length == 0)
        {
            issues.Add(new(field, "MissingField", "Не найдено поле: " + Caption(field) + ".", severity)); return null;
        }
        var values = new List<(T Value, HtmlValueSource Source)>();
        var sources = pairs.Select(pair => new HtmlValueSource(pair.Value.Table, pair.Value.Row, pair.Value.Column, pair.Label.Raw, pair.Value.Raw)).ToArray();
        var invalid = false;
        foreach (var pair in pairs)
        {
            if (!parse(pair.Text, out var value)) { invalid = true; continue; }
            values.Add((value, new(pair.Value.Table, pair.Value.Row, pair.Value.Column, pair.Label.Raw, pair.Value.Raw)));
        }
        if (invalid)
        {
            issues.Add(new(field, "InvalidValue", "Некорректное значение: " + Caption(field) + ". Проверьте исходные ячейки.", severity, sources)); return null;
        }
        if (values.Select(item => item.Value).Distinct().Count() != 1)
        {
            issues.Add(new(field, "ConflictingValues", "Найдены разные значения: " + Caption(field) + ". Автоматический выбор остановлен.", severity, sources)); return null;
        }
        return new(values[0].Value, values.Select(item => item.Source).ToArray());
    }

    private static IEnumerable<Pair> Pairs(Cell[] row, SpecificationField field)
    {
        for (var index = 0; index < row.Length; index++)
        {
            var cell = row[index];
            if (Field(cell.Text) == field)
            {
                var next = row.Skip(index + 1).FirstOrDefault(item => item.Text.Length > 0);
                // Подпись не поглощает следующую подпись при пропущенном значении.
                var pair = next is null || next.Text.Contains(':') && Field(next.Text) is not null
                    ? new Pair(cell, cell, "") : new Pair(cell, next, next.Text, Unit(cell.Text));
                yield return pair;
                if (field == SpecificationField.Dimensions && next is not null)
                    foreach (var extra in FollowingLayouts(row, pair)) yield return extra;
                continue;
            }
            var separator = cell.Text.IndexOf(':');
            if (separator >= 0 && Field(cell.Text[..(separator + 1)]) == field)
            {
                var label = cell.Text[..(separator + 1)];
                var text = cell.Text[(separator + 1)..].Trim();
                if (text.StartsWith('(') && text.IndexOf(')') is var end && end >= 0)
                {
                    label += text[..(end + 1)];
                    if (Field(label) != field) { yield return new(cell, cell, ""); continue; }
                    text = text[(end + 1)..].Trim();
                }
                var pair = new Pair(cell, cell, text, Unit(label));
                yield return pair;
                if (field == SpecificationField.Dimensions)
                    foreach (var extra in FollowingLayouts(row, pair)) yield return extra;
            }
        }
    }

    private static IEnumerable<Pair> FollowingLayouts(Cell[] row, Pair first)
    {
        foreach (var cell in row.Where(cell => cell.Column > first.Value.Column && cell.Text.Length > 0))
        {
            if (!DimensionsPrefix().IsMatch(cell.Text)) yield break;
            yield return new(first.Label, cell, cell.Text);
        }
    }

    private static string LabelKey(string text) => LabelPunctuation().Replace(BlockValueParser.Space(text), "$1").TrimEnd(':').Trim().ToLowerInvariant();
    private static bool Is(string value, string expected) => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
    private static SpecificationField? Field(string text) => LabelKey(text) switch
    {
        "листов" => SpecificationField.SheetCount,
        "габариты" => SpecificationField.Dimensions,
        "материал" => SpecificationField.Material,
        "общие кол-во деталей" or "общее кол-во деталей" or "общее количество деталей" => SpecificationField.PartCount,
        "пробивки" => SpecificationField.PunchCount,
        "длина реза" or "длина реза:(мм)" or "длина реза: (мм)" or "длина реза (мм)" or "длина реза:(м)" => SpecificationField.CutLength,
        "вес" or "вес:(кг)" or "вес: (кг)" or "вес (кг)" => SpecificationField.Weight,
        "время" or "время:(ч:мм)" or "время: (ч:мм)" or "время:(ч:мм:сс)" => SpecificationField.CuttingDuration,
        _ => null
    };
    private static string Unit(string label) => label.Contains("(м)", StringComparison.OrdinalIgnoreCase) ? "m" : "mm";
    private static Pair ConvertLength(Pair pair)
    {
        if (pair.Unit != "m" || !BlockValueParser.Number(pair.Text, out var value)) return pair;
        try { return pair with { Text = checked(value * 1000).ToString(CultureInfo.InvariantCulture) }; }
        catch (OverflowException) { return pair with { Text = "" }; }
    }
    private static bool PositiveInteger(string text, out int value) => BlockValueParser.Integer(text, out value) && value > 0;

    private static bool TryHeader(string text, out SpecificationHeader value)
    {
        value = null!;
        var identifiers = Identifier().Matches(text);
        if (identifiers.Count != 1) return false;
        var id = identifiers[0].Groups["id"].Value;
        var match = identifiers[0];
        if (!DateOnly.TryParseExact(id[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) || date.Year < 100) return false;
        var number = match.Groups["number"].Value;
        var journalNumber = date.ToString("MMdd", CultureInfo.InvariantCulture) + "-" + number;
        if (journalNumber.Length > 255) return false;
        value = new(date, id, journalNumber, text); return true;
    }

    private static bool TryCuttingType(string text, out string value)
    {
        value = "";
        var identifiers = Identifier().Matches(text);
        if (identifiers.Count != 1) return false;
        var id = identifiers[0].Groups["id"];
        var machine = Machine().Match(text[(id.Index + id.Length)..]);
        if (!machine.Success) return false;
        value = machine.Groups[1].Value.ToUpperInvariant() switch { "ЛАЗЕР" => "Лазер", "ПЛАЗМА" => "Плазма", _ => "Газо-кислородная" };
        return true;
    }

    private static bool TryMaterial(string text, out Material value)
    {
        value = null!;
        var match = MaterialPattern().Match(BlockValueParser.Space(text));
        if (!match.Success) return false;
        decimal? thickness = null;
        if (match.Groups[2].Success)
        {
            if (!BlockValueParser.Number(match.Groups[2].Value, out var number) || number <= 0) return false;
            thickness = number;
        }
        var name = match.Groups[1].Value;
        if (name.Length > 255 || name.Any(char.IsControl)) return false;
        value = new(name, thickness); return true;
    }

    private static bool TryMaterialName(string text, out string value)
    {
        var valid = TryMaterial(text, out var material);
        value = valid ? material.Name : ""; return valid;
    }

    private static string Caption(SpecificationField field) => field switch
    {
        SpecificationField.Header => "номер и дата заказа", SpecificationField.CuttingType => "вид резки в заголовке",
        SpecificationField.Material => "материал", SpecificationField.Dimensions => "габариты листа",
        SpecificationField.SheetCount => "количество листов", SpecificationField.CutLength => "итоговая длина реза",
        SpecificationField.Weight => "итоговый вес", SpecificationField.PartCount => "итоговое количество деталей",
        SpecificationField.PunchCount => "итоговые пробивки", _ => "длительность резки"
    };

    [GeneratedRegex(@"^Спецификация\s*№\s*(.+)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
    private static partial Regex HeaderLabel();
    [GeneratedRegex(@"(?:^|[^0-9])(?<id>[0-9]{4}-[0-9]{2}-[0-9]{2}-{1,2}(?<number>[0-9]+))", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex Identifier();
    [GeneratedRegex(@"^[-\s]*(ЛАЗЕР|ПЛАЗМА|ГАЗО-КИСЛОРОДНАЯ)(?:$|[.\s-])", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
    private static partial Regex Machine();
    [GeneratedRegex(@"^(\S+?)(?:\s+([0-9]+(?:[.,][0-9]+)?)\s*(?:mm|мм))?$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
    private static partial Regex MaterialPattern();
    [GeneratedRegex(@"\s*([:()])\s*", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex LabelPunctuation();
    [GeneratedRegex(@"(?<dimensions>[0-9]+(?:[.,][0-9]+)?\s*[xXхХ×]\s*[0-9]+(?:[.,][0-9]+)?\s*[xXхХ×]\s*[0-9]+(?:[.,][0-9]+)?)(?:\s*(?:мм|mm))?(?:\s+(?<count>[0-9]+)\s*шт\.?)?", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
    private static partial Regex SheetLayoutPattern();
    [GeneratedRegex(@"^[0-9]+(?:[.,][0-9]+)?\s*[xXхХ×]", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex DimensionsPrefix();
}

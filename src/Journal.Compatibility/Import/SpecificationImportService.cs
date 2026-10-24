using Jornal.Infrastructure.Diagnostics;
using System.Globalization;
using Jornal.Core.Pricing;
using Jornal.Core.Orders;
using Jornal.Parsing.Documents;
using Jornal.Parsing.Specifications;

namespace Jornal.Desktop.Import;

public interface ISpecificationImportService
{
    Task<SpecificationImportPreview> ReadAsync(string path, CancellationToken cancellationToken);
}

public sealed class SpecificationImportService(SpecificationProfileCatalog? profiles = null, DocumentReadLimits? limits = null) : ISpecificationImportService
{
    public static string? CheckDropFiles(IReadOnlyList<string> paths)
    {
        if (paths.Count <= 1) return CheckFiles(paths);
        if (paths.Count > ManualOrderBatch.MaximumCount) return $"В одном пакете — до {ManualOrderBatch.MaximumCount} файлов.";
        return paths.Any(path => CheckFiles([path]) is null) ? null : "В наборе нет поддерживаемых спецификаций.";
    }
    public static string? CheckFiles(IReadOnlyList<string> paths, SpecificationProfileCatalog? profiles = null) => paths.Count != 1
        ? "Перетащите одну спецификацию."
        : string.IsNullOrWhiteSpace(paths[0]) ? "Нужен доступный файл. Папки не импортируются."
        : (profiles ?? SpecificationProfileCatalog.Default).SupportedExtensions.Contains(Path.GetExtension(paths[0]), StringComparer.OrdinalIgnoreCase) == false
            ? "Поддерживаются " + (profiles ?? SpecificationProfileCatalog.Default).SupportedDescription + "."
            : null;

    public Task<SpecificationImportPreview> ReadAsync(string path, CancellationToken cancellationToken)
    {
        if (CheckFiles([path], profiles) is { } error) throw new DocumentRejectedException(DocumentRejection.UnsupportedFileType, error);
        return Task.Run(async () =>
        {
            SourceDocument document;
            try
            {
                document = await DiagnosticStages.RunAsync(DiagnosticStage.ReadSpecification, () => new DocumentReader(limits).ReadAsync(path, cancellationToken));
            }
            catch (UnauthorizedAccessException) { throw new DocumentRejectedException(DocumentRejection.FileUnavailable, "Нет доступа к файлу."); }
            catch (IOException) { throw new DocumentRejectedException(DocumentRejection.FileUnavailable, "Файл недоступен для чтения."); }
            catch (UglyToad.PdfPig.Core.PdfDocumentFormatException)
            { throw new DocumentRejectedException(DocumentRejection.InvalidDocument, "Не удалось прочитать PDF: файл повреждён или защищён."); }
            var report = DiagnosticStages.Run(DiagnosticStage.ParseSpecification, () => (profiles ?? SpecificationProfileCatalog.Default).Parse(document, cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();
            return DiagnosticStages.Run(DiagnosticStage.NormalizeSpecification, () => SpecificationImportPreview.Create(Path.GetFileName(path), report, profiles));
        }, cancellationToken).WaitAsync(cancellationToken);
    }
}

public sealed record ImportedField(string Value, string Evidence);

public sealed record SpecificationImportPreview(string FileName, SpecificationParseResult Report,
    IReadOnlyDictionary<string, ImportedField> Fields, int? PartCount, int? PunchCount)
{
    public string Diagnostics => DiagnosticsFor(true);
    public string DiagnosticsFor(bool materialNeedsReview) => string.Join("\n\n", Report.Issues.Where(issue => materialNeedsReview || issue.Code != "MaterialNeedsReview").Select(issue => issue.Message +
        (issue.Sources is { Count: > 0 } sources ? "\nВ документе: " + string.Join("; ", sources.Select(source => Short(source.RawText)).Distinct()) : "")));
    public string Duration => Report.CuttingDuration is { } duration
        ? "Время резки: " + CuttingDuration.Format(CuttingDuration.RoundUpMinutes(duration.Value))
        : "Время резки не распознано";
    public string Counts => Report.Profile == WaterjetPdfSpecificationParser.Profile ? "" : $"Детали: {PartCount?.ToString() ?? "не распознано"} · Пробивки: {PunchCount?.ToString() ?? "не распознано"}";

    public static SpecificationImportPreview Create(string fileName, SpecificationParseResult report, SpecificationProfileCatalog? profiles = null)
    {
        if (!report.IsRecognized)
            throw new DocumentRejectedException(DocumentRejection.UnrecognizedSpecification, "Формат спецификации не распознан.");
        var fields = new Dictionary<string, ImportedField>();
        bool Bad(SpecificationField field) => report.Issues.Any(issue => issue.Field == field && issue.Severity == SpecificationIssueSeverity.Error);
        void Add(string name, SpecificationField field, string? value, IEnumerable<SpecificationValueSource>? sources)
        {
            sources ??= report.Issues.Where(issue => issue.Field == field).SelectMany(issue => issue.Sources ?? []);
            var evidence = string.Join("\n", sources.Distinct().Select(source => source switch
            {
                HtmlValueSource html => $"Таблица {html.Table + 1}, строка {html.Row + 1}: {Short(html.Label)} {Short(html.RawText)}",
                PdfValueSource pdf => $"Страница {pdf.Page}, область ({pdf.Left:0.#}; {pdf.Bottom:0.#})–({pdf.Right:0.#}; {pdf.Top:0.#}): {Short(pdf.Label)} {Short(pdf.RawText)}",
                _ => $"{Short(source.Label)} {Short(source.RawText)}"
            }));
            fields[name] = new(Bad(field) ? "" : value ?? "", evidence);
        }
        string Number(decimal value) => value.ToString("0.############################", CultureInfo.InvariantCulture);
        var header = report.Header;
        Add("OrderDate", SpecificationField.Header, header?.Value.OrderDate.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture), header?.Sources);
        Add("SpecificationNumber", SpecificationField.Header, header?.Value.JournalNumber, header?.Sources);
        Add("CuttingType", SpecificationField.CuttingType, report.CuttingType?.Value, report.CuttingType?.Sources);
        Add("Material", SpecificationField.Material, report.Material?.Value, report.Material?.Sources);
        var layouts = report.SheetLayouts.Select(layout => layout.Value.Dimensions).Distinct().ToArray();
        var layoutSources = report.SheetLayouts.SelectMany(layout => layout.Sources).ToArray();
        Add("Thickness", SpecificationField.Dimensions, layouts.Select(size => size.ThicknessMillimeters).Distinct().Count() == 1
            ? Number(layouts[0].ThicknessMillimeters) : null, layoutSources);
        if (report.Issues.Any(issue => issue.Code == "MaterialThicknessMismatch")) fields["Thickness"] = new("", fields["Thickness"].Evidence);
        Add("SheetSize", SpecificationField.Dimensions, layouts.Length > 0 ? string.Join(", ", layouts.Select(size =>
            Number(size.WidthMillimeters) + "x" + Number(size.LengthMillimeters)).Distinct()) : null, layoutSources);
        Add("SheetCount", SpecificationField.SheetCount, report.SheetCount is { } count ? Number(count.Value) : null, report.SheetCount?.Sources);
        Add("Weight", SpecificationField.Weight, report.WeightKilograms is { } weight ? Number(weight.Value) : null, report.WeightKilograms?.Sources);
        Add("CutLength", SpecificationField.CutLength, report.CutLengthMillimeters is { } length && !Bad(SpecificationField.CutLength)
            ? (profiles ?? SpecificationProfileCatalog.Default).NormalizeCutLengthMeters(report.Profile, length.Value).ToString(CultureInfo.InvariantCulture)
            : null, report.CutLengthMillimeters?.Sources);
        Add("CuttingTime", SpecificationField.CuttingDuration, report.CuttingDuration is { } duration
            ? CuttingDuration.Format(CuttingDuration.RoundUpMinutes(duration.Value)) : null, report.CuttingDuration?.Sources);
        Add("MaterialId", SpecificationField.MaterialId, report.MaterialId?.Value, report.MaterialId?.Sources);
        Add("Gas", SpecificationField.CuttingGas, report.CuttingGas?.Value, report.CuttingGas?.Sources);
        var parts = !Bad(SpecificationField.PartCount) ? report.PartCount?.Value : null;
        var punches = !Bad(SpecificationField.PunchCount) ? report.PunchCount?.Value : null;
        fields["Coefficient"] = new(report.Profile == WaterjetPdfSpecificationParser.Profile ? "1" : parts is > 0 && punches is >= 0 ? Number(CuttingPriceCalculator.CalculatePunchCoefficient(parts.Value, punches.Value)) : "",
            report.Profile == WaterjetPdfSpecificationParser.Profile ? "" : "По итогам заказа. Пробивки / детали: " + (punches?.ToString() ?? "?") + " / " + (parts?.ToString() ?? "?"));
        return new(fileName, report, fields, parts, punches);
    }

    private static string Short(string text) => text.Length <= 240 ? text : text[..240] + "…";
}

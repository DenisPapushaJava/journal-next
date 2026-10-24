using Jornal.Parsing.Documents;

namespace Jornal.Parsing.Specifications;

public interface ISpecificationProfile
{
    string Id { get; }
    string DisplayName { get; }
    DocumentKind DocumentKind { get; }
    SpecificationParseResult Parse(SourceDocument document, CancellationToken cancellationToken = default);
    int NormalizeCutLengthMeters(decimal millimeters);
}

/// <summary>Расширение даёт список кандидатов. Только структура документа выбирает профиль; первый кандидат не побеждает автоматически.</summary>
public sealed class SpecificationProfileCatalog
{
    public static SpecificationProfileCatalog Default { get; } = new([new BlockHtmlSpecificationParser(), new HansPdfSpecificationParser(), new WaterjetPdfSpecificationParser()]);
    public IReadOnlyList<ISpecificationProfile> Profiles { get; }
    public IReadOnlyList<string> SupportedExtensions => Profiles.Select(profile => profile.DocumentKind).Distinct()
        .SelectMany(DocumentFormats.Extensions).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    public string SupportedDescription => string.Join(" или ", Profiles.Select(profile =>
        $"{DocumentFormats.Caption(profile.DocumentKind)} {profile.DisplayName}"));

    public SpecificationProfileCatalog(IEnumerable<ISpecificationProfile> profiles)
    {
        var items = profiles.ToArray();
        if (items.Length == 0 || items.Any(profile => string.IsNullOrWhiteSpace(profile.Id)) || items.Select(profile => profile.Id).Distinct().Count() != items.Length)
            throw new ArgumentException("Профили должны иметь уникальные непустые идентификаторы.", nameof(profiles));
        Profiles = Array.AsReadOnly(items);
    }

    public int NormalizeCutLengthMeters(string profileId, decimal millimeters) =>
        (Profiles.SingleOrDefault(profile => profile.Id == profileId) ??
            throw new InvalidOperationException("Профиль нормализации не зарегистрирован.")).NormalizeCutLengthMeters(millimeters);

    public SpecificationParseResult Parse(SourceDocument document, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (document.Kind == DocumentKind.Pdf && !document.Pages.SelectMany(page => page.Words).Any(word => !string.IsNullOrWhiteSpace(word.Text)))
            throw new DocumentRejectedException(DocumentRejection.MissingTextLayer, "В PDF нет текстового слоя.");
        var matches = new List<SpecificationParseResult>();
        foreach (var profile in Profiles.Where(profile => profile.DocumentKind == document.Kind))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var report = profile.Parse(document, cancellationToken);
            if (report.Profile != profile.Id) throw new InvalidOperationException("Профиль вернул чужой идентификатор.");
            if (report.Issues.Any(issue => issue.Code == "InvalidCoordinates"))
                throw new DocumentRejectedException(DocumentRejection.InvalidDocument, "Повреждена структура текста PDF.");
            if (report.IsRecognized) matches.Add(report);
        }
        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new DocumentRejectedException(DocumentRejection.UnrecognizedSpecification, "Формат спецификации не распознан."),
            _ => throw new DocumentRejectedException(DocumentRejection.AmbiguousSpecification, "Не удалось однозначно определить вид спецификации.")
        };
    }
}

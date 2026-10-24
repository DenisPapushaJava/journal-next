namespace Jornal.Parsing.Documents;

public enum DocumentRejection
{
    UnsupportedFileType, UnrecognizedSpecification, AmbiguousSpecification, MissingTextLayer,
    InvalidDocument, FileUnavailable, TooLarge, TooManyPages
}

/// <summary>Ожидаемый отказ импорта, не техническая ошибка приложения. Сообщение не содержит данных файла.</summary>
public sealed class DocumentRejectedException(DocumentRejection reason, string message) : Exception(message)
{
    public DocumentRejection Reason { get; } = reason;
}

public sealed record DocumentReadLimits(long MaximumBytes = 50 * 1024 * 1024, int MaximumPdfPages = 500);

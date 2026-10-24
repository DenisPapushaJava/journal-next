namespace Jornal.Parsing.Documents;

public enum DocumentKind
{
    Html,
    Pdf
}

// Source structure only. Choosing fields, a machine or a price belongs to the parser.
public sealed record SourceDocument(
    DocumentKind Kind,
    string? HtmlText,
    IReadOnlyList<HtmlTable> Tables,
    IReadOnlyList<PdfPage> Pages);

public sealed record HtmlTable(IReadOnlyList<HtmlRow> Rows);
public sealed record HtmlRow(IReadOnlyList<HtmlCell> Cells);
public sealed record HtmlCell(string Text, int ColumnSpan, int RowSpan);

// PDF coordinates are in points, with the origin at the bottom left.
public sealed record PdfPage(int Number, double Width, double Height, IReadOnlyList<PdfWord> Words)
{
    public IReadOnlyList<PdfTextAnnotation> TextAnnotations { get; init; } = [];
}
public sealed record PdfTextAnnotation(string Text, double Left, double Bottom, double Right, double Top);
public sealed record PdfWord(string Text, double Left, double Bottom, double Right, double Top);

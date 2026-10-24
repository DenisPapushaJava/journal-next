using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;
using UglyToad.PdfPig.Annotations;

namespace Jornal.Parsing.Documents;

public sealed class DocumentReader(DocumentReadLimits? limits = null)
{
    public async Task<SourceDocument> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var kind = DocumentFormats.FromPath(path) ?? throw new NotSupportedException("Поддерживаются только HTML и текстовые PDF.");
        var maximum = limits ?? new DocumentReadLimits();
        if (new FileInfo(path).Length > maximum.MaximumBytes)
            throw new DocumentRejectedException(DocumentRejection.TooLarge, $"Файл превышает допустимый размер ({maximum.MaximumBytes / 1024 / 1024} МБ).");
        return kind switch
        {
            DocumentKind.Html => await ReadHtmlAsync(path, cancellationToken),
            DocumentKind.Pdf => await Task.Run(() => ReadPdf(path, maximum, cancellationToken), cancellationToken),
            _ => throw new NotSupportedException("Поддерживаются только HTML и текстовые PDF.")
        };
    }

    private static async Task<SourceDocument> ReadHtmlAsync(string path, CancellationToken cancellationToken)
    {
        // Let the HTML parser detect encoding from BOM/meta instead of assuming UTF-8.
        await using var stream = File.OpenRead(path);
        using var document = await new HtmlParser().ParseDocumentAsync(stream, cancellationToken);
        foreach (var element in document.QuerySelectorAll("script, style"))
        {
            element.Remove();
        }

        var tables = document.QuerySelectorAll("table").OfType<IHtmlTableElement>()
            .Select(table => new HtmlTable(table.Rows
                .Where(row => ReferenceEquals(row.Closest("table"), table))
                .Select(row => new HtmlRow(row.Cells.Select(cell => new HtmlCell(
                    CellText(cell), cell.ColumnSpan, cell.RowSpan)).ToArray()))
                .ToArray()))
            .ToArray();

        cancellationToken.ThrowIfCancellationRequested();
        return new SourceDocument(DocumentKind.Html, document.Body?.TextContent, tables, []);
    }

    private static string CellText(IHtmlTableCellElement cell)
    {
        var text = new StringBuilder();
        void Read(INode node)
        {
            // Вложенная таблица будет прочитана самостоятельно. Её поля не становятся значением внешней ячейки.
            if (node is IHtmlTableElement) return;
            if (node is IText part) { text.Append(part.Data); return; }
            var boundary = node is IElement element && element.LocalName is "br" or "p" or "div";
            if (boundary) text.Append('\n');
            foreach (var child in node.ChildNodes) Read(child);
            if (boundary && node is not IElement { LocalName: "br" }) text.Append('\n');
        }
        foreach (var child in cell.ChildNodes) Read(child);
        return text.ToString();
    }

    private static SourceDocument ReadPdf(string path, DocumentReadLimits limits, CancellationToken cancellationToken)
    {
        using var document = PdfDocument.Open(path);
        if (document.NumberOfPages > limits.MaximumPdfPages)
            throw new DocumentRejectedException(DocumentRejection.TooManyPages, $"В PDF больше {limits.MaximumPdfPages} страниц.");
        var pages = new List<PdfPage>();
        foreach (var page in document.GetPages())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var words = page.GetWords(NearestNeighbourWordExtractor.Instance).Select(word => new PdfWord(
                word.Text, word.BoundingBox.Left, word.BoundingBox.Bottom,
                word.BoundingBox.Right, word.BoundingBox.Top)).ToArray();
            // FreeText — видимые надписи поверх документа; не смешиваем их с основным
            // текстом, чтобы они не меняли прежний разбор HansLaser/HTML.
            var annotations = page.GetAnnotations().Where(annotation => annotation.Type == AnnotationType.FreeText &&
                (annotation.Flags & (AnnotationFlags.Invisible | AnnotationFlags.Hidden | AnnotationFlags.NoView)) == 0 &&
                !string.IsNullOrWhiteSpace(annotation.Content)).Take(1001).ToArray();
            if (annotations.Length > 1000 || annotations.Any(annotation => annotation.Content!.Length > 4096))
                throw new DocumentRejectedException(DocumentRejection.TooLarge, "В PDF слишком много текстовых пометок.");
            pages.Add(new PdfPage(page.Number, page.Width, page.Height, words)
            {
                TextAnnotations = annotations.Select(annotation => new PdfTextAnnotation(annotation.Content!, annotation.Rectangle.Left,
                    annotation.Rectangle.Bottom, annotation.Rectangle.Right, annotation.Rectangle.Top)).ToArray()
            });
        }

        return new SourceDocument(DocumentKind.Pdf, null, [], pages);
    }
}

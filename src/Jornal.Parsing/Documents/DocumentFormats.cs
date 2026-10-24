namespace Jornal.Parsing.Documents;

/// <summary>Контейнер документа и его расширения; вид спецификации определяется отдельно по содержимому.</summary>
public static class DocumentFormats
{
    public static IReadOnlyList<string> Extensions(DocumentKind kind) => kind switch
    {
        DocumentKind.Html => [".html", ".htm"],
        DocumentKind.Pdf => [".pdf"],
        _ => []
    };

    public static DocumentKind? FromPath(string path)
    {
        var extension = Path.GetExtension(path);
        foreach (var kind in Enum.GetValues<DocumentKind>())
            if (Extensions(kind).Contains(extension, StringComparer.OrdinalIgnoreCase)) return kind;
        return null;
    }

    public static string Caption(DocumentKind kind) => kind == DocumentKind.Html ? "HTML" : "PDF";
}

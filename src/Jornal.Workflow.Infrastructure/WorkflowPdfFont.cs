using PdfSharp.Drawing;
using PdfSharp.Fonts;

namespace Jornal.Workflow.Infrastructure;

internal static class WorkflowPdfFont
{
    private const string Family = "JournalMapLiberationSans";
    private static readonly object Gate = new();
    private static readonly Lazy<byte[]> Bytes = new(() =>
    {
        using var stream = typeof(WorkflowPdfFont).Assembly.GetManifestResourceStream("Jornal.Workflow.Infrastructure.Fonts.LiberationSans-Regular.ttf")!;
        using var buffer = new MemoryStream(); stream.CopyTo(buffer); return buffer.ToArray();
    });

    internal static XFont Create(double size)
    {
        lock (Gate)
        {
            GlobalFontSettings.FontResolver ??= new Resolver();
            return new(Family, size, XFontStyleEx.Regular, new XPdfFontOptions(PdfSharp.Pdf.PdfFontEncoding.Unicode));
        }
    }

    private sealed class Resolver : IFontResolver
    {
        public byte[] GetFont(string faceName) => faceName == Family ? Bytes.Value : throw new InvalidOperationException("Unknown map font.");
        public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic) => familyName == Family ? new(Family) : null;
    }
}

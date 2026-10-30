using System.Text.RegularExpressions;
using Jornal.Parsing.Documents;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Content;
using PdfSharp.Pdf.Content.Objects;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace Jornal.Workflow.Infrastructure;

internal static partial class WorkflowMapPdf
{
    private const string WaterjetSignatureKey = "/JournalNextMapSignature";

    private static bool HasInteractiveForm(PdfSharp.Pdf.PdfDocument document, bool waterjet)
    {
        if (waterjet && document.Pages.Cast<PdfSharp.Pdf.PdfPage>().Any(p => p.Annotations.Cast<PdfSharp.Pdf.Annotations.PdfAnnotation>()
            .Any(a => a.Elements.GetName("/Subtype") == "/Widget"))) return true;
        if (!document.Internals.Catalog.Elements.ContainsKey("/AcroForm")) return false;
        if (!waterjet) return true;
        var form = document.Internals.Catalog.Elements.GetDictionary("/AcroForm");
        // PDF editors keep annotation fonts in an empty AcroForm. Keep that resource dictionary;
        // actual fields, XFA, signature flags and orphan widgets remain unsupported.
        return form is null || form.Elements.GetArray("/Fields")?.Elements.Count != 0 ||
            form.Elements.Keys.Any(k => k is not ("/Fields" or "/DR" or "/DA" or "/NeedAppearances" or "/Q"));
    }

    private static List<Cell>? WaterjetCells(PigDocument pdf, SourceDocument document, string nativeKey, CancellationToken token)
    {
        var headers = document.Pages.Select(WorkflowWaterjetShopMaps.AnalyzeHeader).ToArray();
        bool Current(WorkflowWaterjetShopMaps.Header? h) => h is { Type: WorkflowWaterjetShopMaps.Kind.Postprocessor } &&
            h.Drawings.Any(n => WorkflowShopDiscovery.Key(n) == nativeKey);
        if (!headers.Any(Current)) return null;
        var cells = new List<Cell>();
        foreach (var page in pdf.GetPages())
        {
            token.ThrowIfCancellationRequested();
            var header = headers[page.Number - 1];
            if (header is null && cells.Count > 0 && WaterjetContinuation(document.Pages[page.Number - 1])) continue;
            if (!Current(header) || header!.InvalidFields || header.Drawings.Select(WorkflowShopDiscovery.Key).Distinct().Count() != 1)
                throw Problem(page.Number, "карта AWJC другого проекта или страница не распознана как продолжение");
            var words = page.GetWords().ToArray();
            var labels = words.Where(w => Normalize(w.Text).Equals("НОМЕР ЗАКАЗА", StringComparison.OrdinalIgnoreCase)).Select(w => w.BoundingBox).ToList();
            foreach (var start in words.Where(w => w.Text.Equals("НОМЕР", StringComparison.OrdinalIgnoreCase)))
            {
                var end = words.Where(w => w.Text.Equals("ЗАКАЗА", StringComparison.OrdinalIgnoreCase) &&
                    w.BoundingBox.Left > start.BoundingBox.Right && w.BoundingBox.Left - start.BoundingBox.Right < 20 &&
                    Math.Abs(w.BoundingBox.Bottom - start.BoundingBox.Bottom) < 2).ToArray();
                if (end.Length == 1) labels.Add(new(start.BoundingBox.Left, Math.Min(start.BoundingBox.Bottom, end[0].BoundingBox.Bottom),
                    end[0].BoundingBox.Right, Math.Max(start.BoundingBox.Top, end[0].BoundingBox.Top)));
            }
            if (labels.Count != 1) throw Problem(page.Number, "не найдена однозначная графа «НОМЕР ЗАКАЗА»");
            var bounds = FindCellBounds(page, labels[0], true);
            cells.Add(new(page.Number, bounds, ReadCell(page, bounds)));
        }
        return cells;
    }

    private static bool WaterjetContinuation(Jornal.Parsing.Documents.PdfPage page)
    {
        var text = Regex.Replace(string.Join(" ", page.Words.Select(w => w.Text)), @"\s+", "").ToUpperInvariant();
        return !text.Contains("НОМЕРЗАКАЗА", StringComparison.Ordinal) && !text.Contains("DRAWING", StringComparison.Ordinal) &&
            !text.Contains("CNC", StringComparison.Ordinal) && new[] { "QUANTITY", "QUALITY", "CUTTINGLENGTH", "WEIGHT", "SINGLETIME", "TOTALTIME" }.All(s => text.Contains(s, StringComparison.Ordinal));
    }

    private static List<Cell> WaterjetAnnotations(List<Cell> cells, PdfSharp.Pdf.PdfDocument pdf)
    {
        var result = new List<Cell>();
        foreach (var cell in cells)
        {
            var page = pdf.Pages[cell.Page - 1]; var signatures = new List<int>(); var texts = new List<string>();
            if (Normalize(cell.Text).Length > 0) texts.Add(Normalize(cell.Text));
            for (var i = 0; i < page.Annotations.Count; i++)
            {
                var annotation = page.Annotations[i]; var rect = annotation.Rectangle; var content = Normalize(annotation.Contents ?? "");
                var signature = annotation.Elements.GetName("/Subtype") == "/FreeText" &&
                    rect.X1 >= cell.Bounds.Left - 3 && rect.X1 <= cell.Bounds.Left + 15 && Math.Abs(rect.Y2 - cell.Bounds.Top) <= 6 &&
                    rect.Height <= 35 && rect.Width > 50 &&
                    Regex.IsMatch(content, @"^\(?\s*\d+(?:[.,]\d+)?\s*мм\s*\)?\s+.+\bБЛЦБ\s*-\s*\S+", RegexOptions.IgnoreCase);
                if (signature) { signatures.Add(i); texts.Add(content); }
                else if (Math.Min(rect.X2, cell.Bounds.Right) - Math.Max(rect.X1, cell.Bounds.Left) > 1.5 &&
                    Math.Min(rect.Y2, cell.Bounds.Top) - Math.Max(rect.Y1, cell.Bounds.Bottom) > 1.5)
                    throw Problem(cell.Page, "поверх поля «НОМЕР ЗАКАЗА» есть неизвестная пометка");
            }
            result.Add(cell with { Text = string.Join("\n", texts.Distinct(StringComparer.Ordinal)), SignatureAnnotations = signatures.ToArray() });
        }
        return result;
    }

    private static bool RemoveWaterjetSignatureStream(PdfSharp.Pdf.PdfPage page, string nativeKey, int pageNumber)
    {
        var removed = false;
        for (var i = page.Contents.Elements.Count - 1; i >= 0; i--)
        {
            var content = page.Contents.Elements.GetDictionary(i);
            if (content?.Elements.ContainsKey(WaterjetSignatureKey) != true) continue;
            if (content.Elements.GetString(WaterjetSignatureKey) != nativeKey)
                throw Problem(pageNumber, "поток подписи относится к другому проекту");
            // An external editor can merge page contents while retaining the marker. Never
            // remove such a stream if it also contains drawing/image/clip operations.
            if (content.Stream is null || !SignatureOnly(ContentReader.ReadContent(content.Stream.UnfilteredValue)))
                throw Problem(pageNumber, "подпись объединена с другими элементами карты");
            page.Contents.Elements.RemoveAt(i); removed = true;
        }
        return removed;
    }

    private static bool SignatureOnly(CSequence content)
    {
        var level = 0; var hasText = false;
        foreach (var op in content.OfType<COperator>())
        {
            if (level == 0 && op.Name != "q") return false;
            if (op.Name is not ("q" or "Q" or "cm" or "BT" or "ET" or "Tf" or "Tm" or "Td" or "TD" or "TL" or
                "Tj" or "TJ" or "Tc" or "Tw" or "Tz" or "Ts" or "Tr" or "rg" or "RG" or "g" or "G" or "k" or "K" or "gs")) return false;
            if (op.Name == "q") level++;
            if (op.Name == "Q" && --level < 0) return false;
            hasText |= op.Name is "Tj" or "TJ";
        }
        return hasText && level == 0;
    }
}

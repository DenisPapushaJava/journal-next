using System.Text.RegularExpressions;
using Jornal.Workflow.Core;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using PigDocument = UglyToad.PdfPig.PdfDocument;
using PdfRectangle = UglyToad.PdfPig.Core.PdfRectangle;

namespace Jornal.Workflow.Infrastructure;

internal sealed record MapPdfEdit(byte[]? Bytes, int Pages, int ChangedPages);

// The labelled cell and its borders are found in the document;
// unrelated PDFs and unrecognised layouts are never stamped at guessed coordinates.
internal static partial class WorkflowMapPdf
{
    private sealed record Cell(int Page, PdfRectangle Bounds, string Text, int[]? SignatureAnnotations = null);
    private sealed record Edge(double X1, double Y1, double X2, double Y2);
    internal static string Normalize(string value) => Regex.Replace(value, @"\s+", " ").Trim();

    internal static MapPdfEdit? Prepare(byte[] source, CuttingProject project, WorkflowShopDiscovery.Rule rule, string text, CancellationToken token)
    {
        using var pdf = PigDocument.Open(source);
        if (pdf.NumberOfPages > 100) throw new WorkflowValidationException("В PDF больше 100 страниц. Подпишите карту вручную.");
        var document = ToDocument(pdf, token);
        var waterjet = rule == WorkflowShopDiscovery.Rule.Waterjet;
        var nativeKey = WorkflowShopDiscovery.Key(Path.GetFileName(project.NativePath!));
        if (pdf.GetPages().FirstOrDefault(p => p.Rotation.Value != 0 &&
            Regex.Replace(p.Text, @"\s+", "").ToUpperInvariant().Contains(nativeKey, StringComparison.Ordinal)) is { } rotated)
            throw Problem(rotated.Number, "поворот страницы пока не поддерживается");
        var cells = waterjet ? WaterjetCells(pdf, document, nativeKey, token) : BlockCells(pdf, document, nativeKey, rule, token);
        if (cells is null) return null;
        var normalized = Normalize(text);
        var changes = waterjet ? [] : cells.Where(c => Normalize(c.Text) != normalized).ToArray();
        if (!waterjet && changes.Length == 0) return new(null, cells.Count, 0);
        using var input = new MemoryStream(source, writable: false);
        using var output = PdfReader.Open(input, PdfDocumentOpenMode.Modify);
        if (waterjet)
        {
            cells = WaterjetAnnotations(cells, output);
            changes = cells.Where(c => Normalize(c.Text) != normalized).ToArray();
        }
        if (changes.Length == 0) return new(null, cells.Count, 0);
        if (output.SecuritySettings.IsEncrypted || !output.SecuritySettings.PermitModifyDocument ||
            output.Internals.Catalog.Elements.ContainsKey("/Perms") || HasInteractiveForm(output, waterjet))
            throw new WorkflowValidationException("PDF защищён или содержит интерактивные поля/электронную подпись. Автоматическое изменение недоступно.");
        foreach (var cell in changes)
        {
            token.ThrowIfCancellationRequested();
            var page = output.Pages[cell.Page - 1]; var original = pdf.GetPage(cell.Page);
            if (page.Rotate != 0 || Math.Abs(page.MediaBox.X1) > .01 || Math.Abs(page.MediaBox.Y1) > .01 ||
                page.Elements.ContainsKey("/CropBox") && (Math.Abs(page.CropBox.X1 - page.MediaBox.X1) > .01 || Math.Abs(page.CropBox.Y1 - page.MediaBox.Y1) > .01 ||
                    Math.Abs(page.CropBox.X2 - page.MediaBox.X2) > .01 || Math.Abs(page.CropBox.Y2 - page.MediaBox.Y2) > .01))
                throw Problem(cell.Page, "поворот или обрезка страницы пока не поддерживаются");
            if (!waterjet && Enumerable.Range(0, page.Annotations.Count).Any(i => Intersects(page.Annotations[i].Rectangle, cell.Bounds)))
                throw Problem(cell.Page, "поверх комментария есть пометка; проверьте её вручную");
            var managed = waterjet && RemoveWaterjetSignatureStream(page, nativeKey, cell.Page);
            if (!managed && (!waterjet || ReadCell(original, cell.Bounds).Length > 0))
            {
                var clean = RemoveCellText(original, cell.Bounds);
                page.Contents.ReplaceContent(PdfSharp.Pdf.Content.ContentReader.ReadContent(clean));
            }
            foreach (var index in (cell.SignatureAnnotations ?? []).OrderDescending()) page.Annotations.Remove(page.Annotations[index]);
            using (var graphics = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append))
            {
                var layout = Fit(graphics, text, cell.Bounds.Width - 6, cell.Bounds.Height - 1, !waterjet);
                var top = original.Height - cell.Bounds.Top;
                var lineHeight = layout.Font.GetHeight();
                var y = top + (cell.Bounds.Height - layout.Lines.Length * lineHeight) / 2;
                foreach (var line in layout.Lines)
                {
                    graphics.DrawString(line, layout.Font, XBrushes.Black, new XRect(cell.Bounds.Left + 3, y, cell.Bounds.Width - 6, lineHeight), XStringFormats.TopLeft);
                    y += lineHeight;
                }
            }
            if (waterjet) page.Contents.Elements.GetDictionary(page.Contents.Elements.Count - 1)!.Elements.SetString(WaterjetSignatureKey, nativeKey);
        }
        using var buffer = new MemoryStream(); output.Save(buffer, closeStream: false);
        var result = buffer.ToArray();
        Verify(pdf, result, changes, text, token);
        return new(result, cells.Count, changes.Length);
    }

    private static List<Cell>? BlockCells(PigDocument pdf, Jornal.Parsing.Documents.SourceDocument document,
        string nativeKey, WorkflowShopDiscovery.Rule rule, CancellationToken token)
    {
        // A drawing/requisites PDF is not a map merely because its name matches.
        if (!document.Pages.Any(p => WorkflowShopDiscovery.Key(WorkflowShopDiscovery.Field(p, "ПРОЕКТ", "ПРОЭКТ")) == nativeKey)) return null;
        var cells = new List<Cell>();
        foreach (var page in pdf.GetPages())
        {
            token.ThrowIfCancellationRequested();
            var p = document.Pages[page.Number - 1]; var name = WorkflowShopDiscovery.Field(p, "ПРОЕКТ", "ПРОЭКТ");
            if (name.Length == 0 && IsTableContinuation(p) && cells.Count > 0) continue;
            if (WorkflowShopDiscovery.Key(name) != nativeKey)
                throw Problem(page.Number, "проект не совпадает или страница не распознана как продолжение таблицы");
            // CAM station labels are free text and can be shared by gas and plasma.
            // The database machine and exact project match above identify this job.
            var programExtension = rule switch
            {
                WorkflowShopDiscovery.Rule.Laser => ".cnc",
                WorkflowShopDiscovery.Rule.Gas or WorkflowShopDiscovery.Rule.PlasmaManual => ".nc",
                _ => null
            };
            var cnc = WorkflowShopDiscovery.Field(p, "CNC");
            if (programExtension is null || !cnc.EndsWith(programExtension, StringComparison.OrdinalIgnoreCase))
                throw Problem(page.Number, "ссылка CNC не соответствует станку проекта");
            var cell = FindCell(page); cells.Add(new(page.Number, cell, ReadCell(page, cell)));
        }
        return cells;
    }

    private static Jornal.Parsing.Documents.SourceDocument ToDocument(PigDocument pdf, CancellationToken token)
    {
        var pages = new List<Jornal.Parsing.Documents.PdfPage>();
        foreach (var p in pdf.GetPages())
        {
            token.ThrowIfCancellationRequested();
            pages.Add(new(p.Number, p.Width, p.Height, p.GetWords().Select(w => new Jornal.Parsing.Documents.PdfWord(w.Text,
                w.BoundingBox.Left, w.BoundingBox.Bottom, w.BoundingBox.Right, w.BoundingBox.Top)).ToArray()));
        }
        return new(Jornal.Parsing.Documents.DocumentKind.Pdf, null, [], pages);
    }

    private static bool IsTableContinuation(Jornal.Parsing.Documents.PdfPage page)
    {
        var words = page.Words.Select(w => w.Text.ToUpperInvariant()).ToHashSet();
        return !words.Contains("CNC") && !words.Contains("КОММЕНТАРИЙ") &&
            words.Contains("НАЗВАНИЕ") && words.Contains("ДЕТАЛИ") && words.Contains("КОЛ-ВО") && words.Contains("ВСЕГО");
    }

    private static PdfRectangle FindCell(Page page)
    {
        var labels = page.GetWords().Where(w => w.Text.TrimEnd(':').Equals("Комментарий", StringComparison.OrdinalIgnoreCase) &&
            w.BoundingBox.Top > page.Height * .75).ToArray();
        if (labels.Length != 1) throw Problem(page.Number, "не найдена однозначная графа «Комментарий»");
        return FindCellBounds(page, labels[0].BoundingBox, false);
    }

    private static PdfRectangle FindCellBounds(Page page, PdfRectangle label, bool waterjet)
    {
        var middle = (label.Top + label.Bottom) / 2;
        var edges = new List<Edge>();
        foreach (var path in page.Paths.Where(p => p.IsStroked || waterjet && p.IsFilled))
        {
            foreach (var sub in path)
                foreach (var command in sub.Commands)
                    if (command is PdfSubpath.Line line) edges.Add(new(line.From.X, line.From.Y, line.To.X, line.To.Y));
            if (path.GetBoundingRectangle() is { } r)
            {
                edges.Add(new(r.Left, r.Bottom, r.Left, r.Top)); edges.Add(new(r.Right, r.Bottom, r.Right, r.Top));
                edges.Add(new(r.Left, r.Bottom, r.Right, r.Bottom)); edges.Add(new(r.Left, r.Top, r.Right, r.Top));
            }
        }
        var vertical = edges.Where(e => Math.Abs(e.X1 - e.X2) < .15 && Math.Min(e.Y1, e.Y2) < middle && Math.Max(e.Y1, e.Y2) > middle).ToArray();
        var left = vertical.Where(e => e.X1 > label.Right + 1).Select(e => e.X1).DefaultIfEmpty(double.NaN).Min();
        var right = vertical.Where(e => e.X1 > left + 100).Select(e => e.X1).DefaultIfEmpty(double.NaN).Min();
        var horizontal = edges.Where(e => Math.Abs(e.Y1 - e.Y2) < .15 && Math.Min(e.X1, e.X2) <= left + .5 && Math.Max(e.X1, e.X2) >= right - .5).ToArray();
        var bottom = horizontal.Where(e => e.Y1 < label.Bottom).Select(e => e.Y1).DefaultIfEmpty(double.NaN).Max();
        var top = horizontal.Where(e => e.Y1 > label.Top).Select(e => e.Y1).DefaultIfEmpty(double.NaN).Min();
        if (!double.IsFinite(left + right + bottom + top) || top - bottom < (waterjet ? 8 : 14) || top - bottom > 45 || right - left < page.Width * .35)
            throw Problem(page.Number, "границы ячейки не соответствуют знакомому образцу");
        return new(left, bottom, right, top);
    }

    private static bool Inside(Letter letter, PdfRectangle cell) => letter.BoundingBox.Left >= cell.Left - .5 &&
        letter.BoundingBox.Right <= cell.Right + .5 && letter.BoundingBox.Bottom >= cell.Bottom - .5 && letter.BoundingBox.Top <= cell.Top + .5;
    private static string ReadCell(Page page, PdfRectangle cell) => string.Join(" ", page.GetWords().Where(w =>
        w.BoundingBox.Left >= cell.Left && w.BoundingBox.Right <= cell.Right && w.BoundingBox.Bottom >= cell.Bottom && w.BoundingBox.Top <= cell.Top)
        .OrderByDescending(w => Math.Round(w.Letters[0].StartBaseLine.Y / 3)).ThenBy(w => w.BoundingBox.Left).Select(w => w.Text));

    private static byte[] RemoveCellText(Page page, PdfRectangle cell)
    {
        var letters = page.Letters.Where(l => !string.IsNullOrWhiteSpace(l.Value)).ToArray();
        var sequences = letters.Where(l => Inside(l, cell)).Select(l => l.TextSequence).ToHashSet();
        if (sequences.Any(s => letters.Any(l => l.TextSequence == s && !Inside(l, cell))) || page.Operations.Any(o => o.Operator == "Do"))
            throw Problem(page.Number, "текст комментария объединён с другими элементами PDF");
        var operations = page.Operations; var count = 0; var removed = new HashSet<int>();
        for (var i = 0; i < operations.Count; i++)
        {
            if (!ShowsText(operations[i].Operator)) continue;
            if (!sequences.Contains(++count)) continue;
            removed.Add(i);
        }
        if (sequences.Any(s => s > count) || removed.Count != sequences.Count)
            throw Problem(page.Number, "не удалось сопоставить текст комментария со структурой PDF");
        foreach (var i in removed)
        {
            // Multiple lines may share one text object and relative line moves. Their
            // removed advances cannot affect surviving text after an absolute reset.
            var following = operations.Skip(i + 1).Select((op, offset) => (op, index: i + 1 + offset))
                .TakeWhile(p => !ShowsText(p.op.Operator) || removed.Contains(p.index)).Select(p => p.op.Operator).ToArray();
            if (operations[i].Operator is "'" or "\"" || !following.Contains("Tm") && !following.Contains("BT") && !following.Contains("ET"))
                throw Problem(page.Number, "не удалось безопасно отделить старую подпись");
        }
        using var stream = new MemoryStream();
        for (var i = 0; i < operations.Count; i++) if (!removed.Contains(i)) operations[i].Write(stream);
        return stream.ToArray();
    }
    private static bool ShowsText(string op) => op is "Tj" or "TJ" or "'" or "\"";

    private static (XFont Font, string[] Lines) Fit(XGraphics graphics, string text, double width, double height, bool allowWrapping = true)
    {
        for (var size = 10.5; size >= 8; size -= .25)
        {
            var font = WorkflowPdfFont.Create(size);
            if (graphics.MeasureString(text, font).Width <= width && font.GetHeight() <= height) return (font, [text]);
        }
        if (!allowWrapping) throw DoesNotFit();
        var small = WorkflowPdfFont.Create(8); var lines = new List<string>(); var current = "";
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var next = current.Length == 0 ? word : current + " " + word;
            if (graphics.MeasureString(next, small).Width <= width) current = next;
            else { if (current.Length > 0) lines.Add(current); current = word; }
            if (graphics.MeasureString(current, small).Width > width) throw DoesNotFit();
        }
        if (current.Length > 0) lines.Add(current);
        if (lines.Count * small.GetHeight() > height) throw DoesNotFit();
        return (small, lines.ToArray());
    }
    private static WorkflowValidationException DoesNotFit() => new("Подпись слишком длинная для ячейки даже при шрифте 8 пт. PDF не изменён; проверьте реквизиты или подпишите вручную.");

    private static void Verify(PigDocument before, byte[] bytes, Cell[] changes, string text, CancellationToken token)
    {
        using var after = PigDocument.Open(bytes);
        if (before.NumberOfPages != after.NumberOfPages) throw new WorkflowValidationException("Проверка страниц PDF не прошла. Исходник сохранён.");
        foreach (var cell in changes)
        {
            token.ThrowIfCancellationRequested();
            var a = before.GetPage(cell.Page); var b = after.GetPage(cell.Page);
            var old = a.Letters.Where(l => !string.IsNullOrWhiteSpace(l.Value) && !Inside(l, cell.Bounds)).ToArray();
            var current = b.Letters.Where(l => !string.IsNullOrWhiteSpace(l.Value) && !Inside(l, cell.Bounds)).ToArray();
            if (Normalize(ReadCell(b, cell.Bounds)) != Normalize(text) || old.Length != current.Length ||
                old.Zip(current).Any(pair => pair.First.Value != pair.Second.Value ||
                    Math.Abs(pair.First.BoundingBox.Left - pair.Second.BoundingBox.Left) > .1 || Math.Abs(pair.First.BoundingBox.Bottom - pair.Second.BoundingBox.Bottom) > .1))
                throw new WorkflowValidationException("Проверка подписи или содержимого PDF не прошла. Исходник не изменён.");
        }
    }
    private static bool Intersects(PdfSharp.Pdf.PdfRectangle a, PdfRectangle b) => a.X1 < b.Right && a.X2 > b.Left && a.Y1 < b.Top && a.Y2 > b.Bottom;
    private static WorkflowValidationException Problem(int page, string text) => new($"Страница {page}: {text}. PDF не изменён.");
}

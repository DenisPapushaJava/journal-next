using System.Runtime.InteropServices;
using Jornal.Workflow.Core;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Jornal.Workflow.Infrastructure;

internal static partial class WorkflowWordMap
{
    private sealed record BundleContent(string Text, int Pages, int Tables, int InlineShapes, int Shapes, int Comments);
    private sealed record SheetSetup(int Orientation, float Width, float Height, float Top, float Bottom, float Left, float Right,
        float Gutter, int GutterPosition, float Header, float Footer, int FirstPage, int OddEven, int Alignment);

    private static WorkflowWordMapProcess.Response BuildBundle(string folder, WorkflowWordMapProcess.Request request, object documents)
    {
        var names = request.BundleSources!;
        var output = Path.Combine(folder, WorkflowShopTransfer.BundleFileName(request.Bundle));
        var pages = request.Bundle == ShopMapBundle.Pdf ? ExportBundlePdf(folder, names, documents, output, request.Match) :
            MergeBundleDoc(folder, names, documents, output, request.Match);
        return new(true, false, pages, 0);
    }

    private static int ExportBundlePdf(string folder, string[] names, object documents, string output, WordMapMatch? match)
    {
        using var result = new PdfDocument();
        for (var i = 0; i < names.Length; i++)
        {
            var part = Path.Combine(folder, $"pages-{i}.pdf");
            var pages = 0;
            UseBundleCard(documents, folder, i, names[i], match, (card, _) =>
            {
                dynamic doc = card;
                doc.Repaginate();
                pages = (int)doc.ComputeStatistics(2);
                doc.ExportAsFixedFormat(OutputFileName: part, ExportFormat: 17, OpenAfterExport: false);
            });
            try
            {
                using var pdf = PdfReader.Open(part, PdfDocumentOpenMode.Import);
                if (pages < 1 || pdf.PageCount != pages)
                    throw new WorkflowValidationException("При экспорте изменилось количество страниц.");
                WorkflowHansPdfDrawing.Correct(pdf, Path.Combine(folder, $"input-{i}.doc"));
                foreach (var page in pdf.Pages) result.AddPage(page);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WorkflowValidationException or PdfReaderException)
            {
                throw BundleCardError(names[i], ex);
            }
        }
        return WorkflowMapBundlePdf.Save(result, output);
    }

    private static int MergeBundleDoc(string folder, string[] names, object documents, string output, WordMapMatch? match)
    {
        var expected = new List<BundleContent>(names.Length);
        var sheets = new List<SheetSetup>(names.Length);
        UseBundleCard(documents, folder, 0, names[0], match, (firstCard, firstScope) =>
        {
            dynamic result = firstCard;
            sheets.Add(RequireOneSheet(result, firstScope));
            expected.Add(ReadBundleContent(result, firstScope));
            for (var i = 1; i < names.Length; i++)
            {
                UseBundleCard(documents, folder, i, names[i], match, (sourceCard, scope) =>
                {
                    dynamic source = sourceCard;
                    var settings = RequireOneSheet(source, scope);
                    sheets.Add(settings);
                    expected.Add(ReadBundleContent(source, scope));

                    dynamic sections = firstScope.Keep(result.Sections);
                    dynamic spot = EndOfBody(result, firstScope);
                    spot.InsertBreak(Type: 2); // wdSectionBreakNextPage
                    dynamic insertion = EndOfBody(result, firstScope);
                    insertion.InsertFile(FileName: Path.Combine(folder, $"input-{i}.doc"), ConfirmConversions: false, Link: false, Attachment: false);
                    if ((int)sections.Count != i + 1)
                        throw new WorkflowValidationException("Word изменил структуру разделов при объединении.");
                    dynamic targetSection = firstScope.Keep(sections[i + 1]);
                    dynamic sourceSections = scope.Keep(source.Sections);
                    dynamic sourceSection = scope.Keep(sourceSections[1]);
                    ApplySheet(firstScope.Keep(targetSection.PageSetup), settings);
                    CopyHeaderFooter(scope.Keep(sourceSection.Headers), firstScope.Keep(targetSection.Headers), scope, firstScope);
                    CopyHeaderFooter(scope.Keep(sourceSection.Footers), firstScope.Keep(targetSection.Footers), scope, firstScope);
                });
            }
            VerifyMergedDoc(result, expected, sheets, firstScope);
            result.SaveAs2(FileName: output, FileFormat: 0, AddToRecentFiles: false); // wdFormatDocument97
        });

        using var checkScope = new ComScope();
        dynamic check = Open((dynamic)documents, output, checkScope);
        try { VerifyMergedDoc(check, expected, sheets, checkScope); }
        finally { check.Close(SaveChanges: 0); }
        return expected.Sum(c => c.Pages);
    }

    private static void UseBundleCard(object documents, string folder, int index, string name, WordMapMatch? match, Action<object, ComScope> use)
    {
        using var scope = new ComScope();
        dynamic? card = null;
        try
        {
            var path = Path.Combine(folder, $"input-{index}.doc");
            using (var file = File.OpenRead(path))
            {
                Span<byte> header = stackalloc byte[8];
                if (file.Length < header.Length) throw new WorkflowValidationException("Файл слишком короткий для DOC.");
                file.ReadExactly(header);
                if (!header.SequenceEqual(new byte[] { 0xd0, 0xcf, 0x11, 0xe0, 0xa1, 0xb1, 0x1a, 0xe1 }))
                    throw new WorkflowValidationException("Файл не в формате Word DOC.");
            }
            card = Open((dynamic)documents, path, scope);
            if (match is not null) ValidateBundleCard((object)card, scope, documents, folder, index, name, match);
            use((object)card, scope);
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or WorkflowValidationException or PdfReaderException)
        {
            throw BundleCardError(name, ex);
        }
        finally { if (card is not null) card.Close(SaveChanges: 0); }
    }

    private static WorkflowValidationException BundleCardError(string name, Exception error) =>
        new($"Карта «{name}». " + (error is WorkflowValidationException ? error.Message : "Word не смог прочитать карту или сохранить результат."));

    private static void ValidateBundleCard(object card, ComScope scope, object documents, string folder, int index, string name, WordMapMatch match)
    {
        CardCells cells = ReadCells((dynamic)card, scope);
        if (match.RequireGnfPairs)
        {
            var pairedPath = Path.Combine(folder, $"paired-{index}.doc");
            if (!WorkflowHansShopMaps.IsGnfExport(name) || !File.Exists(pairedPath))
                throw new WorkflowValidationException("Нужны Gnf и обычный DOC для одного листа.");
            using var pairedScope = new ComScope();
            dynamic paired = Open((dynamic)documents, pairedPath, pairedScope);
            try
            {
                CardCells pairedCells = ReadCells(paired, pairedScope);
                if (!WorkflowWordMapRules.IsGnfPair(cells.Tables, pairedCells.Tables, match))
                    throw new WorkflowValidationException("Не удалось подтвердить, что Gnf и обычный DOC относятся к одной УП текущего проекта.");
            }
            finally { paired.Close(SaveChanges: 0); }
        }
        else if (WorkflowWordMapRules.Comments(cells.Tables, "", match) is null)
            throw new WorkflowValidationException(WorkflowHansShopMaps.IsGnfExport(name)
                ? "Выгрузите обычную карту DOC для этого листа. В файле Gnf.DOC не найдена карта проекта с полем «Комментарий»."
                : "В файле нет карты этого проекта.");
    }

    private static dynamic EndOfBody(dynamic document, ComScope scope)
    {
        dynamic body = scope.Keep(document.Content);
        var end = (int)body.End - 1;
        return scope.Keep(document.Range(end, end));
    }

    private static BundleContent ReadBundleContent(dynamic doc, ComScope scope)
    {
        doc.Repaginate();
        return new(WorkflowWordMapRules.Normalize((string)scope.Keep(doc.Content).Text), (int)doc.ComputeStatistics(2),
            (int)scope.Keep(doc.Tables).Count, (int)scope.Keep(doc.InlineShapes).Count, (int)scope.Keep(doc.Shapes).Count, (int)scope.Keep(doc.Comments).Count);
    }

    private static SheetSetup RequireOneSheet(dynamic doc, ComScope scope)
    {
        if ((int)scope.Keep(doc.Sections).Count != 1)
            throw new WorkflowValidationException("В карте несколько разделов Word. Для объединения выберите PDF.");
        return ReadSheet(scope.Keep(doc.PageSetup));
    }

    private static SheetSetup ReadSheet(dynamic setup) => new((int)setup.Orientation, (float)setup.PageWidth, (float)setup.PageHeight,
        (float)setup.TopMargin, (float)setup.BottomMargin, (float)setup.LeftMargin, (float)setup.RightMargin,
        (float)setup.Gutter, (int)setup.GutterPos, (float)setup.HeaderDistance, (float)setup.FooterDistance,
        (int)setup.DifferentFirstPageHeaderFooter, (int)setup.OddAndEvenPagesHeaderFooter, (int)setup.VerticalAlignment);

    private static void ApplySheet(dynamic setup, SheetSetup sheet)
    {
        setup.Orientation = sheet.Orientation;
        setup.PageWidth = sheet.Width; setup.PageHeight = sheet.Height;
        setup.TopMargin = sheet.Top; setup.BottomMargin = sheet.Bottom; setup.LeftMargin = sheet.Left; setup.RightMargin = sheet.Right;
        setup.Gutter = sheet.Gutter; setup.GutterPos = sheet.GutterPosition;
        setup.HeaderDistance = sheet.Header; setup.FooterDistance = sheet.Footer;
        setup.DifferentFirstPageHeaderFooter = sheet.FirstPage; setup.OddAndEvenPagesHeaderFooter = sheet.OddEven;
        setup.VerticalAlignment = sheet.Alignment;
    }

    private static void CopyHeaderFooter(dynamic source, dynamic target, ComScope sourceScope, ComScope targetScope)
    {
        for (var index = 1; index <= 3; index++)
        {
            dynamic from = sourceScope.Keep(source[index]);
            dynamic to = targetScope.Keep(target[index]);
            if (!(bool)from.Exists && !(bool)to.Exists) continue;
            to.LinkToPrevious = false;
            dynamic destination = targetScope.Keep(to.Range);
            if ((bool)from.Exists)
            {
                if ((int)sourceScope.Keep(from.Shapes).Count > 0)
                    throw new WorkflowValidationException("В колонтитуле есть плавающий рисунок. Для объединения выберите PDF.");
                destination.FormattedText = sourceScope.Keep(sourceScope.Keep(from.Range).FormattedText);
            }
            else destination.Text = "";
        }
    }

    private static void VerifyMergedDoc(dynamic doc, List<BundleContent> cards, List<SheetSetup> sheets, ComScope scope)
    {
        BundleContent actual = ReadBundleContent(doc, scope);
        var expected = new BundleContent(string.Join(" ", cards.Select(c => c.Text)), cards.Sum(c => c.Pages), cards.Sum(c => c.Tables),
            cards.Sum(c => c.InlineShapes), cards.Sum(c => c.Shapes), cards.Sum(c => c.Comments));
        dynamic sections = scope.Keep(doc.Sections);
        if (actual != expected || (int)sections.Count != sheets.Count)
            throw new WorkflowValidationException("После объединения изменилась разметка DOC. Выберите PDF или передачу отдельных карт.");
        for (var i = 0; i < sheets.Count; i++)
            if ((SheetSetup)ReadSheet(scope.Keep(scope.Keep(sections[i + 1]).PageSetup)) != sheets[i])
                throw new WorkflowValidationException("Word изменил параметры страниц. Выберите PDF или передачу отдельных карт.");
    }
}

internal static class WorkflowMapBundlePdf
{
    internal const int ExportVersion = 1;

    internal static int Save(PdfDocument document, string output)
    {
        // PDFsharp invalidates the in-memory document when Save completes.
        var expectedPages = document.PageCount;
        document.Save(output);
        using var saved = PdfReader.Open(output, PdfDocumentOpenMode.Import);
        if (saved.PageCount != expectedPages)
            throw new WorkflowValidationException("Не удалось проверить страницы итогового PDF.");
        return saved.PageCount;
    }
}

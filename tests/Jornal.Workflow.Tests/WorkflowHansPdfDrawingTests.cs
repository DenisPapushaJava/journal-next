using System.IO.Compression;
using System.Text;
using Jornal.Workflow.Infrastructure;
using PdfSharp.Pdf.Content;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class WorkflowHansPdfDrawingTests
{
    private const string Geometry = "10 10 m\n40 10 l\n30 25 l\nh\nS";
    private const string Drawing = """
        10 10 m
        40 10 l
        30 25 l
        h
        S
        BT
        /F8 10 Tf
        -1 0 0 1 50 90 Tm
        1 0 0 rg
        -12 Tc[(850.0)] TJ
        ET
        BT
        0 -1 -1 0 90 60 Tm
        [(1500.0)] TJ
        ET
        BT
        -1 0 0 1 60 50 Tm
        0 g
        0 Tc[(42)] TJ
        ET
        BT
        0 -1 -1 0 80 75 Tm
        [(7)] TJ
        ET
        BT
        1 0 0 -1 75 45 Tm
        [(3)] TJ
        ET
        BT
        0 1 1 0 60 25 Tm
        [(5)] TJ
        ET

        """;

    [Fact]
    public void CorrectsSheetDimensionsAndPartNumbersInTheImportedPageWithoutRewritingGeometry()
    {
        var folder = Directory.CreateTempSubdirectory("next-hans-pdf-");
        try
        {
            var source = Path.Combine(folder.FullName, "source.doc");
            var export = Path.Combine(folder.FullName, "export.pdf");
            var bundle = Path.Combine(folder.FullName, "bundle.pdf");
            WriteSource(source, "42");
            WriteExport(export, Drawing);
            using (var input = PdfReader.Open(export, PdfDocumentOpenMode.Import))
            {
                WorkflowHansPdfDrawing.Correct(input, source);
                using var output = new PdfDocument();
                output.AddPage(input.Pages[0]);
                Assert.Equal(1, WorkflowMapBundlePdf.Save(output, bundle));
            }
            using var saved = PdfReader.Open(bundle, PdfDocumentOpenMode.Import);
            var corrected = ReadDrawing(saved);
            Assert.StartsWith(Geometry, corrected);
            Assert.Contains("1 0 0 1 44 90 Tm\n1 0 0 rg\n0 Tc[(850.0)]", corrected);
            Assert.Contains("0 1 -1 0 90 54 Tm\n[(1500.0)]", corrected);
            Assert.Contains("1 0 0 1 48 50 Tm\n0 g\n0 Tc[(42)]", corrected);
            Assert.Contains("0 1 -1 0 80 69 Tm\n[(7)]", corrected);
            Assert.Contains("-1 0 0 -1 81 45 Tm\n[(3)]", corrected);
            Assert.Contains("0 -1 1 0 60 31 Tm\n[(5)]", corrected);
            Assert.Equal(Drawing, ReadOriginalExport(export));
            WorkflowHansPdfDrawing.Correct(saved, source);
            Assert.Equal(corrected, ReadDrawing(saved));
        }
        finally { folder.Delete(recursive: true); }
    }

    [Theory]
    [InlineData("unrelated source")]
    [InlineData("reflected placement")]
    [InlineData("unrelated drawing")]
    [InlineData("already legible")]
    public void KeepsThePriorPdfExportWhenTheDrawingCannotBeSafelyCorrected(string scenario)
    {
        var folder = Directory.CreateTempSubdirectory("next-hans-pdf-");
        try
        {
            var source = Path.Combine(folder.FullName, "source.doc");
            var export = Path.Combine(folder.FullName, "export.pdf");
            var drawing = scenario == "already legible" ? Drawing.Replace("-1 0 0 1 50 90 Tm", "1 0 0 1 50 90 Tm", StringComparison.Ordinal) : Drawing;
            WriteSource(source, scenario == "unrelated source" ? "24" : "42");
            var objectName = scenario == "unrelated drawing" ? "/Background" : "/Meta1";
            WriteExport(export, drawing, objectName, scenario == "reflected placement");
            using var pdf = PdfReader.Open(export, PdfDocumentOpenMode.Import);
            WorkflowHansPdfDrawing.Correct(pdf, source);
            Assert.Equal(drawing, ReadDrawing(pdf, objectName));
        }
        finally { folder.Delete(recursive: true); }
    }

    private static void WriteExport(string path, string content, string objectName = "/Meta1", bool reflect = false)
    {
        using var doc = new PdfDocument();
        doc.Options.CompressContentStreams = false;
        var page = doc.AddPage();
        var font = new PdfDictionary(doc);
        font.Elements.SetName("/Type", "/Font");
        font.Elements.SetName("/Subtype", "/TrueType");
        font.Elements.SetName("/BaseFont", "/CourierNewPSMT");
        font.Elements.SetName("/Encoding", "/WinAnsiEncoding");
        font.Elements.SetInteger("/FirstChar", 46);
        font.Elements["/Widths"] = new PdfArray(doc, Enumerable.Range(46, 12).Select(_ => new PdfInteger(600)).ToArray());
        var fonts = new PdfDictionary(doc);
        fonts.Elements["/F8"] = font;
        var resources = new PdfDictionary(doc);
        resources.Elements["/Font"] = fonts;

        var form = new PdfDictionary(doc);
        form.Elements.SetName("/Type", "/XObject");
        form.Elements.SetName("/Subtype", "/Form");
        form.Elements["/BBox"] = new PdfArray(doc, new PdfInteger(0), new PdfInteger(0), new PdfInteger(100), new PdfInteger(100));
        form.Elements["/Matrix"] = new PdfArray(doc, new PdfInteger(1), new PdfInteger(0), new PdfInteger(0), new PdfInteger(1), new PdfInteger(0), new PdfInteger(0));
        form.Elements["/Resources"] = resources;
        form.Elements.SetName("/Filter", "/FlateDecode");
        form.CreateStream(Compress(Encoding.Latin1.GetBytes(content)));
        doc.Internals.AddObject(form);
        var objects = new PdfDictionary(doc);
        objects.Elements[objectName] = form.Reference!;
        page.Resources.Elements["/XObject"] = objects;
        var placement = reflect ? "-1 0 0 1 100 0" : "1 0 0 1 0 0";
        page.Contents.ReplaceContent(ContentReader.ReadContent(Encoding.ASCII.GetBytes($"q\n{placement} cm\n{objectName} Do\nQ\n")));
        doc.Save(path);
    }

    private static string ReadOriginalExport(string path)
    {
        using var doc = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        return ReadDrawing(doc);
    }

    private static string ReadDrawing(PdfDocument pdf, string name = "/Meta1")
    {
        var form = pdf.Pages[0].Resources.Elements.GetDictionary("/XObject")!.Elements.GetDictionary(name)!;
        using var packed = new MemoryStream(form.Stream!.Value);
        using var unpacked = new ZLibStream(packed, CompressionMode.Decompress);
        using var text = new StreamReader(unpacked, Encoding.Latin1);
        return text.ReadToEnd();
    }

    private static void WriteSource(string path, string firstPart)
    {
        using var metafile = new MemoryStream();
        using var data = new BinaryWriter(metafile);
        data.Write(1); data.Write(88);
        data.Write(new byte[32]);
        data.Write(" EMF"u8);
        data.Write(0x10000);
        data.Write(0); // Patched with the complete drawing length below.
        data.Write(new byte[36]);
        WriteLabel(data, "850.0", 0, 255);
        WriteLabel(data, "1500.0", 900, 255);
        WriteLabel(data, firstPart, 0, 0);
        WriteLabel(data, "7", 900, 0);
        WriteLabel(data, "3", 1800, 0);
        WriteLabel(data, "5", 2700, 0);
        data.Write(14); data.Write(20); data.Write(new byte[12]);
        var length = (int)metafile.Length;
        metafile.Position = 48;
        data.Write(length);
        using var file = File.Create(path);
        file.Write([0xd0, 0xcf, 0x11, 0xe0, 0xa1, 0xb1, 0x1a, 0xe1]);
        file.Write(Compress(metafile.ToArray()));
    }

    private static void WriteLabel(BinaryWriter data, string value, int angle, int color)
    {
        data.Write(82); data.Write(32); data.Write(1);
        data.Write(0); data.Write(0); data.Write(angle); data.Write(0); data.Write(400);
        data.Write(37); data.Write(12); data.Write(1);
        data.Write(24); data.Write(12); data.Write(color);
        var label = Encoding.Unicode.GetBytes(value);
        var padding = (4 - label.Length % 4) % 4;
        data.Write(84); data.Write(76 + label.Length + padding);
        data.Write(new byte[36]); data.Write(value.Length); data.Write(76); data.Write(new byte[24]);
        data.Write(label); data.Write(new byte[padding]);
    }

    private static byte[] Compress(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true)) zlib.Write(bytes);
        return output.ToArray();
    }
}

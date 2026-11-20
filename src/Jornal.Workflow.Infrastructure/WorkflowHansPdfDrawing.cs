using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Content;
using PdfSharp.Pdf.Content.Objects;

namespace Jornal.Workflow.Infrastructure;

internal static class WorkflowHansPdfDrawing
{
    private const int MaximumDrawingBytes = 8 * 1024 * 1024;
    private const string Number = @"-?(?:[0-9]{1,10}(?:\.[0-9]{0,12})?|\.[0-9]{1,12})";
    private const string Space = @"[ \t\r\n]+";
    private const string Dimension = @"[0-9]{2,5}\.[0-9]{1,2}";
    private static readonly string Matrix = string.Join(Space, Enumerable.Repeat(Number, 6));
    private static readonly Regex DimensionPair = new(
        $@"(?m)^BT{Space}(?<font>/F[0-9]+){Space}(?<size>{Number}){Space}Tf{Space}(?<horizontal>{Matrix}){Space}Tm{Space}1{Space}0{Space}0{Space}rg{Space}(?<hSpacing>{Number}){Space}Tc[ \t]*\[\((?<width>{Dimension})\)\][ \t]*TJ{Space}ET{Space}BT{Space}(?<vertical>{Matrix}){Space}Tm{Space}(?:(?<vSpacing>{Number}){Space}Tc[ \t]*)?\[\((?<height>{Dimension})\)\][ \t]*TJ{Space}(?<verticalEnd>ET)(?=\s)",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex TextBlock = new(
        @"(?m)^BT\b[\s\S]*?\bET\b", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex NumberBlock = new(
        $@"\ABT{Space}(?:(?<font>/F[0-9]+){Space}(?<size>{Number}){Space}Tf{Space})?(?<matrix>{Matrix}){Space}Tm{Space}(?<color>(?:0{Space}[gG]{Space})*)(?:(?<spacing>{Number}){Space}Tc[ \t]*)?\[\((?<text>[0-9]{{1,6}})\)\][ \t]*TJ{Space}ET\z",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    // The import document is still in memory. Its pages will be copied to the output only after validation.
    internal static void Correct(PdfDocument document, string sourceDoc)
    {
        IReadOnlyList<IReadOnlyList<WorkflowHansDrawingReference.Label>>? reference = null;
        foreach (var form in FindDrawings(document))
        {
            try
            {
                var content = ReadDrawing(form);
                if (content is null) continue;
                var pair = DimensionPair.Match(content);
                if (!pair.Success || !HorizontalReflection(Coordinates(pair.Groups["horizontal"])) ||
                    !VerticalReflection(Coordinates(pair.Groups["vertical"])))
                    continue;
                reference ??= ReadReference(sourceDoc);
                foreach (var labels in reference)
                {
                    var corrected = CorrectDrawing(form, content, pair, labels);
                    if (corrected is null) continue;
                    WriteDrawing(form, corrected);
                    break;
                }
            }
            catch (Exception exception) when (exception is InvalidDataException or RegexMatchTimeoutException)
            {
                // An unfamiliar Word export is still usable through the existing PDF pipeline.
            }
        }
    }

    private static IReadOnlyList<IReadOnlyList<WorkflowHansDrawingReference.Label>> ReadReference(string path)
    {
        try { return WorkflowHansDrawingReference.Read(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return []; }
    }

    private static IEnumerable<PdfDictionary> FindDrawings(PdfDocument document)
    {
        var placements = new Dictionary<PdfDictionary, bool>(ReferenceEqualityComparer.Instance);
        foreach (var page in document.Pages)
        {
            var objects = page.Elements.GetDictionary("/Resources")?.Elements.GetDictionary("/XObject");
            if (objects is null || !objects.Elements.Keys.Any(IsWordDrawing)) continue;
            CSequence contents;
            try { contents = ContentReader.ReadContent(page); }
            catch (Exception exception) when (exception is ContentReaderException or FormatException or OverflowException)
            {
                // Skip forms whose page coordinates cannot be established.
                foreach (var key in objects.Elements.Keys.Where(IsWordDrawing))
                    if (objects.Elements.GetDictionary(key) is { } form) placements[form] = false;
                continue;
            }
            var saved = new Stack<int>();
            var orientation = 1;
            foreach (var operation in contents.OfType<COperator>())
            {
                switch (operation.Name)
                {
                    case "q": saved.Push(orientation); break;
                    case "Q": orientation = saved.TryPop(out var previous) ? previous : 0; break;
                    case "cm": orientation *= Orientation(operation.Operands); break;
                    case "Do" when operation.Operands.Count == 1 && operation.Operands[0] is CName name &&
                        IsWordDrawing(name.Name) && objects.Elements.GetDictionary(name.Name) is { } form:
                        placements[form] = orientation > 0 && placements.GetValueOrDefault(form, true);
                        break;
                }
            }
        }
        return placements.Where(item => item.Value).Select(item => item.Key);
    }

    private static bool IsWordDrawing(string name) => name.StartsWith("/Meta", StringComparison.Ordinal);

    private static int Orientation(CSequence matrix)
    {
        if (matrix.Count != 6) return 0;
        var determinant = Operand(matrix[0]) * Operand(matrix[3]) - Operand(matrix[1]) * Operand(matrix[2]);
        return double.IsFinite(determinant) ? Math.Sign(determinant) : 0;
    }

    private static double Operand(CObject value) => value switch
    {
        CInteger integer => integer.Value,
        CReal real => real.Value,
        _ => double.NaN
    };

    private static string? ReadDrawing(PdfDictionary form)
    {
        var elements = form.Elements;
        if (elements.GetName("/Subtype") != "/Form" || form.Stream is null ||
            elements.GetName("/Filter") != "/FlateDecode" || elements.ContainsKey("/DecodeParms"))
            return null;
        var matrix = elements.GetArray("/Matrix");
        if (matrix?.Elements.Count != 6 || matrix.Elements.GetReal(0) <= 0 || matrix.Elements.GetReal(3) <= 0 ||
            matrix.Elements.GetReal(1) != 0 || matrix.Elements.GetReal(2) != 0)
            return null;

        using var compressed = new MemoryStream(form.Stream.Value, writable: false);
        using var decoded = new ZLibStream(compressed, CompressionMode.Decompress);
        using var content = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = decoded.Read(buffer)) > 0)
        {
            if (content.Length + read > MaximumDrawingBytes) return null;
            content.Write(buffer, 0, read);
        }
        return Encoding.Latin1.GetString(content.GetBuffer(), 0, (int)content.Length);
    }

    private static string? CorrectDrawing(PdfDictionary form, string content, Match pair,
        IReadOnlyList<WorkflowHansDrawingReference.Label> reference)
    {
        if (reference[0] != new WorkflowHansDrawingReference.Label(pair.Groups["width"].Value, 0, 255) ||
            reference[1] != new WorkflowHansDrawingReference.Label(pair.Groups["height"].Value, 900, 255))
            return null;
        var horizontal = Coordinates(pair.Groups["horizontal"]);
        var vertical = Coordinates(pair.Groups["vertical"]);

        var fonts = form.Elements.GetDictionary("/Resources")?.Elements.GetDictionary("/Font");
        var font = fonts?.Elements.GetDictionary(pair.Groups["font"].Value);
        if (font is null || !IsExpectedFont(font)) return null;
        var labels = pair.Groups["width"].Value + pair.Groups["height"].Value;
        var size = Parse(pair.Groups["size"].Value);
        var advance = GlyphAdvance(font, labels, size);
        if (advance <= 0) return null;

        var hSpacing = Parse(pair.Groups["hSpacing"].Value);
        var verticalSpacing = pair.Groups["vSpacing"];
        var vSpacing = verticalSpacing.Success ? Parse(verticalSpacing.Value) : hSpacing;
        var fixedHSpacing = -2 * advance - hSpacing;
        var fixedVSpacing = -2 * advance - vSpacing;
        if (Math.Abs(fixedHSpacing) > .2 * advance || Math.Abs(fixedVSpacing) > .2 * advance)
            return null;

        var replacements = new List<(Group Group, string Value)>
        {
            (pair.Groups["horizontal"], CorrectedMatrix(horizontal, advance)),
            (pair.Groups["hSpacing"], Format(fixedHSpacing)),
            (pair.Groups["vertical"], CorrectedMatrix(vertical, advance))
        };
        // Word can omit Tc in the second text block if it inherits the first block's spacing.
        if (verticalSpacing.Success)
            replacements.Add((verticalSpacing, Format(fixedVSpacing)));
        replacements.Add((pair.Groups["verticalEnd"], Format(vSpacing) + " Tc\nET"));

        var partBlocks = TextBlock.Matches(content, pair.Index + pair.Length);
        if (partBlocks.Count != reference.Count - 2 || !SharesTextState(content, partBlocks.Count + 2)) return null;
        var black = false;
        var spacing = vSpacing;
        for (var index = 0; index < partBlocks.Count; index++)
        {
            var block = partBlocks[index];
            var number = NumberBlock.Match(block.Value);
            var expected = reference[index + 2];
            var digits = number.Groups["text"].Value;
            if (!number.Success || expected.Color != 0 || digits != expected.Text) return null;

            if (number.Groups["font"].Success)
            {
                font = fonts!.Elements.GetDictionary(number.Groups["font"].Value);
                size = Parse(number.Groups["size"].Value);
            }
            if (font is null || !IsExpectedFont(font, allowBold: true)) return null;
            black |= Regex.IsMatch(number.Groups["color"].Value, @"0\s+g\b");
            if (number.Groups["spacing"].Success) spacing = Parse(number.Groups["spacing"].Value);
            if (!black || !NearZero(spacing)) return null;
            var original = number.Groups["matrix"];
            var transform = Coordinates(original);
            if (!IsPartReflection(transform, expected.Escapement)) return null;
            var cellAdvance = GlyphAdvance(font, digits, size);
            if (cellAdvance <= 0) return null;
            replacements.Add((block, block.Value.Remove(original.Index, original.Length)
                .Insert(original.Index, CorrectedMatrix(transform, cellAdvance * digits.Length))));
        }

        var updated = new StringBuilder(content.Length);
        var copied = 0;
        foreach (var (group, value) in replacements)
        {
            updated.Append(content, copied, group.Index - copied).Append(value);
            copied = group.Index + group.Length;
        }
        updated.Append(content, copied, content.Length - copied);
        return updated.ToString();
    }

    private static bool SharesTextState(string content, int blocks)
    {
        // Tf, Tc and colour are inherited between Word's text blocks.
        var frames = new Stack<int>();
        var frame = 0;
        var next = 0;
        int? textFrame = null;
        var inText = false;
        foreach (Match operation in Regex.Matches(content, @"(?<!\S)(?:q|Q|BT|ET|cm|Tf|Tc|Tz|Tw|Tr|Ts)(?=\s|\[|$)"))
        {
            switch (operation.Value)
            {
                case "q": frames.Push(frame); frame = ++next; break;
                case "Q": if (!frames.TryPop(out frame)) return false; break;
                case "BT":
                    inText = true;
                    textFrame ??= frame;
                    if (textFrame != frame) return false;
                    if (--blocks == 0) return true;
                    break;
                case "ET": inText = false; break;
                case "Tf" or "Tc": if (!inText) return false; break;
                default: return false;
            }
        }
        return false;
    }

    private static void WriteDrawing(PdfDictionary form, string content)
    {
        // PDFsharp's stream Zip path does not emit a reliable Adler-32 for these forms.
        using var encoded = new MemoryStream();
        using (var zlib = new ZLibStream(encoded, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(Encoding.Latin1.GetBytes(content));
        form.Stream!.Value = encoded.ToArray();
    }

    private static bool HorizontalReflection(double[] m) =>
        m[0] < 0 && m[3] > 0 && NearZero(m[1]) && NearZero(m[2]);

    private static bool VerticalReflection(double[] m) =>
        m[1] < 0 && m[2] < 0 && NearZero(m[0]) && NearZero(m[3]);

    private static bool IsPartReflection(double[] m, int angle) => ((angle % 3600 + 3600) % 3600) switch
    {
        0 => HorizontalReflection(m),
        900 => VerticalReflection(m),
        1800 => m[0] > 0 && m[3] < 0 && NearZero(m[1]) && NearZero(m[2]),
        2700 => m[1] > 0 && m[2] > 0 && NearZero(m[0]) && NearZero(m[3]),
        _ => false
    };

    private static bool NearZero(double value) => Math.Abs(value) < .0001;

    private static bool IsExpectedFont(PdfDictionary font, bool allowBold = false)
    {
        var elements = font.Elements;
        var name = elements.GetName("/BaseFont").Split('+')[^1].TrimStart('/');
        return elements.GetName("/Subtype") == "/TrueType" && elements.GetName("/Encoding") == "/WinAnsiEncoding" &&
            (name == "CourierNewPSMT" || (allowBold && name == "CourierNewPS-BoldMT"));
    }

    private static double GlyphAdvance(PdfDictionary font, string labels, double size)
    {
        var first = font.Elements.GetInteger("/FirstChar");
        var widths = font.Elements.GetArray("/Widths");
        if (widths is null || size <= 0) return 0;
        var advance = 0d;
        foreach (var character in labels.Distinct())
        {
            var index = character - first;
            if (index < 0 || index >= widths.Elements.Count) return 0;
            var width = widths.Elements.GetReal(index) * size / 1000;
            if (width <= 0 || (advance != 0 && advance != width)) return 0;
            advance = width;
        }
        return advance;
    }

    // Flip glyphs in their existing cells and keep both the cell origins and character order.
    private static string CorrectedMatrix(double[] m, double advance) =>
        string.Join(" ", new[] { -m[0], -m[1], m[2], m[3], m[4] + m[0] * advance, m[5] + m[1] * advance }.Select(Format));

    private static double[] Coordinates(Group group) => Regex.Split(group.Value, Space).Select(Parse).ToArray();
    private static double Parse(string value) => double.Parse(value, CultureInfo.InvariantCulture);
    private static string Format(double value) => value == 0 ? "0" : value.ToString("0.#########", CultureInfo.InvariantCulture);
}

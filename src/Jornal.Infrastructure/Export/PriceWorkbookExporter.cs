using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Jornal.Core.Pricing;

namespace Jornal.Infrastructure.Export;

// Небольшой write-only SpreadsheetML: Office и дополнительные библиотеки в поставке не нужны.
// https://learn.microsoft.com/office/open-xml/spreadsheet/structure-of-a-spreadsheetml-document
public static class PriceWorkbookExporter
{
    private static readonly XNamespace Sheet = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Relationship = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace Package = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace ContentType = "http://schemas.openxmlformats.org/package/2006/content-types";
    private const string SpreadsheetType = "application/vnd.openxmlformats-officedocument.spreadsheetml.";

    public static Task SaveAsync(string path, PriceExportWorkbook workbook, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        var destination = Path.GetFullPath(path);
        if (!string.Equals(Path.GetExtension(destination), ".xlsx", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Файл экспорта должен иметь расширение .xlsx.", nameof(path));
        var temporary = Path.Combine(Path.GetDirectoryName(destination)!, ".journal-prices-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { Write(stream, workbook, cancellationToken); stream.Flush(flushToDisk: true); }
            cancellationToken.ThrowIfCancellationRequested();
            // Готовая книга заменяет назначение только после чтения всех прайсов и закрытия ZIP.
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }, cancellationToken);

    public static void Write(Stream destination, PriceExportWorkbook workbook, CancellationToken cancellationToken = default)
    {
        if (workbook.Sheets.Count == 0 || workbook.Sheets.Any(sheet => sheet.Profiles.Count == 0))
            throw new ArgumentException("В книге должен быть хотя бы один прайс.", nameof(workbook));
        foreach (var profile in workbook.Sheets.SelectMany(sheet => sheet.Profiles)) profile.Table.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        var names = SheetNames(workbook.Sheets.Select(sheet => sheet.MachineName));
        using var zip = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);
        Add(zip, "[Content_Types].xml", new XElement(ContentType + "Types",
            new XElement(ContentType + "Default", new XAttribute("Extension", "rels"), new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")),
            new XElement(ContentType + "Default", new XAttribute("Extension", "xml"), new XAttribute("ContentType", "application/xml")),
            Type("/xl/workbook.xml", SpreadsheetType + "sheet.main+xml"), Type("/xl/styles.xml", SpreadsheetType + "styles+xml"),
            Type("/docProps/core.xml", "application/vnd.openxmlformats-package.core-properties+xml"),
            names.Select((_, index) => Type($"/xl/worksheets/sheet{index + 1}.xml", SpreadsheetType + "worksheet+xml"))));
        Add(zip, "_rels/.rels", new XElement(Package + "Relationships",
            Link("rId1", "officeDocument", "xl/workbook.xml"),
            new XElement(Package + "Relationship", new XAttribute("Id", "rId2"),
                new XAttribute("Type", "http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties"), new XAttribute("Target", "docProps/core.xml"))));
        Add(zip, "xl/workbook.xml", E("workbook", new XAttribute(XNamespace.Xmlns + "r", Relationship),
            E("bookViews", E("workbookView", A("activeTab", 0))),
            E("sheets", names.Select((name, index) => E("sheet", A("name", name), A("sheetId", index + 1), new XAttribute(Relationship + "id", $"rId{index + 1}")))),
            E("definedNames", names.Select((name, index) => E("definedName", A("name", "_xlnm.Print_Titles"), A("localSheetId", index),
                "'" + name.Replace("'", "''") + "'!$1:$4")))));
        Add(zip, "xl/_rels/workbook.xml.rels", new XElement(Package + "Relationships",
            names.Select((_, index) => Link($"rId{index + 1}", "worksheet", $"worksheets/sheet{index + 1}.xml")),
            Link($"rId{names.Length + 1}", "styles", "styles.xml")));
        Add(zip, "xl/styles.xml", XElement.Parse(Styles));
        XNamespace dc = "http://purl.org/dc/elements/1.1/", dcterms = "http://purl.org/dc/terms/", xsi = "http://www.w3.org/2001/XMLSchema-instance";
        Add(zip, "docProps/core.xml", new XElement(XName.Get("coreProperties", "http://schemas.openxmlformats.org/package/2006/metadata/core-properties"),
            new XAttribute(XNamespace.Xmlns + "dcterms", dcterms), new XAttribute(XNamespace.Xmlns + "xsi", xsi),
            new XElement(dc + "creator", "Journal"), new XElement(dcterms + "created", new XAttribute(xsi + "type", "dcterms:W3CDTF"),
                workbook.ExportedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture))));
        for (var index = 0; index < workbook.Sheets.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Add(zip, $"xl/worksheets/sheet{index + 1}.xml", BuildSheet(workbook.Sheets[index], workbook.ExportedAt, cancellationToken));
        }
    }

    private static XElement BuildSheet(PriceExportSheet sheet, DateTimeOffset at, CancellationToken token)
    {
        var rates = sheet.Profiles.Select(profile => profile.Table.Rates.ToDictionary(rate => rate.ThicknessMillimeters)).ToArray();
        var thicknesses = rates.SelectMany(values => values.Keys).Distinct().Order().ToArray();
        var columns = sheet.Profiles.Count + 1; var end = Column(columns); var lastRow = thicknesses.Length + 4;
        var data = E("sheetData");
        var totalWidth = 18 + sheet.Profiles.Count * (sheet.Profiles.Count == 1 ? 24 : 34);
        var title = CleanXml(sheet.MachineName);
        var titleLines = title.Split('\n').Sum(line => Math.Max(1, Math.Ceiling(line.Length / (totalWidth * .55))));
        data.Add(Row(1, Math.Clamp(titleLines * 22 + 8, 30, 409), Text("A1", title, 1)),
            Row(2, 22, Text("A2", "Выгружено " + at.ToString("dd.MM.yyyy", CultureInfo.GetCultureInfo("ru-RU")), 2)), Row(3, 8));
        data.Add(Row(4, 44, Text("A4", "Толщина, мм", 3), sheet.Profiles.Select((profile, index) =>
            Text(Column(index + 2) + "4", sheet.Profiles.Count == 1 ? "Цена, ₽/м" : profile.Name + ", ₽/м", 3))));
        for (var index = 0; index < thicknesses.Length; index++)
        {
            token.ThrowIfCancellationRequested(); var row = index + 5; var thickness = thicknesses[index];
            data.Add(Row(row, 22, Number($"A{row}", thickness, 4), rates.Select((table, column) =>
                Number(Column(column + 2) + row, table.TryGetValue(thickness, out var rate) ? rate.RublesPerMeter : null, 5))));
        }
        return E("worksheet", E("sheetPr", E("pageSetUpPr", A("fitToPage", 1))), E("dimension", A("ref", $"A1:{end}{lastRow}")),
            E("sheetViews", E("sheetView", A("workbookViewId", 0), A("showGridLines", 0),
                E("pane", A("ySplit", 4), A("topLeftCell", "A5"), A("activePane", "bottomLeft"), A("state", "frozen")),
                E("selection", A("pane", "bottomLeft"), A("activeCell", "A5"), A("sqref", "A5")))),
            E("sheetFormatPr", A("defaultRowHeight", 22)),
            E("cols", Enumerable.Range(1, columns).Select(column => E("col", A("min", column), A("max", column),
                A("width", column == 1 ? 18 : columns == 2 ? 24 : 34), A("customWidth", 1)))), data,
            E("autoFilter", A("ref", $"A4:{end}{lastRow}")),
            E("mergeCells", A("count", 2), E("mergeCell", A("ref", $"A1:{end}1")), E("mergeCell", A("ref", $"A2:{end}2"))),
            E("printOptions", A("horizontalCentered", 1)),
            E("pageMargins", A("left", ".4"), A("right", ".4"), A("top", ".5"), A("bottom", ".5"), A("header", ".2"), A("footer", ".2")),
            E("pageSetup", A("paperSize", 9), A("orientation", columns > 2 ? "landscape" : "portrait"), A("fitToWidth", 1), A("fitToHeight", 0)),
            E("headerFooter", E("oddFooter", "&LJournal&CСтраница &P из &N")));
    }
    private static XElement E(string name, params object[] content) => new(Sheet + name, content);
    private static XAttribute A(string name, object value) => new(name, value);
    private static XElement Row(int row, double height, params object[] cells) => E("row", A("r", row), A("ht", height), A("customHeight", 1), cells);
    private static XElement Text(string address, string value, int style) => E("c", A("r", address), A("s", style), A("t", "inlineStr"),
        E("is", E("t", new XAttribute(XNamespace.Xml + "space", "preserve"), CleanXml(value))));
    private static XElement Number(string address, decimal? value, int style) => E("c", A("r", address), A("s", style), A("t", "n"),
        value is null ? null! : E("v", value.Value.ToString(CultureInfo.InvariantCulture)));
    private static XElement Type(string part, string type) => new(ContentType + "Override", A("PartName", part), A("ContentType", type));
    private static XElement Link(string id, string type, string target) => new(Package + "Relationship", A("Id", id), A("Type", Relationship.NamespaceName + "/" + type), A("Target", target));
    private static void Add(ZipArchive zip, string path, XElement content)
    { using var stream = zip.CreateEntry(path, CompressionLevel.Optimal).Open(); content.Save(stream); }
    private static string Column(int index)
    {
        var name = "";
        while (index > 0) { index--; name = (char)('A' + index % 26) + name; index /= 26; }
        return name;
    }
    private static string CleanXml(string value)
    {
        var text = new StringBuilder();
        foreach (var rune in value.EnumerateRunes())
            if (rune.Value is 9 or 10 or 13 or >= 0x20 and <= 0xD7FF or >= 0xE000 and <= 0xFFFD or >= 0x10000 and <= 0x10FFFF) text.Append(rune);
        return text.ToString();
    }
    private static string[] SheetNames(IEnumerable<string> titles)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return titles.Select(title =>
        {
            var stem = string.Concat(CleanXml(title).Select(character => "[]:*?/\\".Contains(character) || char.IsControl(character) ? '_' : character)).Trim().Trim('\'');
            if (stem.Length == 0) stem = "Станок";
            var name = Truncate(stem, 31).Trim('\''); var suffix = 1;
            while (name.Equals("History", StringComparison.OrdinalIgnoreCase) || !used.Add(name))
            { var ending = $" ({++suffix})"; name = Truncate(stem, 31 - ending.Length) + ending; }
            return name;
        }).ToArray();
    }
    private static string Truncate(string text, int length) => text.Length <= length ? text :
        text[..(char.IsHighSurrogate(text[length - 1]) ? length - 1 : length)];

    private const string Styles = """
        <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
          <numFmts count="1"><numFmt numFmtId="164" formatCode="0.############################"/></numFmts>
          <fonts count="4">
            <font><sz val="11"/><color rgb="FF24374A"/><name val="Calibri"/></font>
            <font><b/><sz val="16"/><color rgb="FF24374A"/><name val="Calibri"/></font>
            <font><sz val="10"/><color rgb="FF52677D"/><name val="Calibri"/></font>
            <font><b/><sz val="11"/><color rgb="FF24374A"/><name val="Calibri"/></font>
          </fonts>
          <fills count="3"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill><fill><patternFill patternType="solid"><fgColor rgb="FFE4EDF5"/><bgColor indexed="64"/></patternFill></fill></fills>
          <borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders>
          <cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>
          <cellXfs count="6">
            <xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/>
            <xf numFmtId="0" fontId="1" fillId="0" borderId="0" xfId="0" applyAlignment="1"><alignment vertical="center" wrapText="1"/></xf>
            <xf numFmtId="0" fontId="2" fillId="0" borderId="0" xfId="0"/>
            <xf numFmtId="0" fontId="3" fillId="2" borderId="0" xfId="0" applyAlignment="1"><alignment horizontal="right" vertical="center" wrapText="1"/></xf>
            <xf numFmtId="164" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1" applyAlignment="1"><alignment horizontal="right" vertical="center"/></xf>
            <xf numFmtId="3" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1" applyAlignment="1"><alignment horizontal="right" vertical="center"/></xf>
          </cellXfs>
          <cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles>
        </styleSheet>
        """;
}

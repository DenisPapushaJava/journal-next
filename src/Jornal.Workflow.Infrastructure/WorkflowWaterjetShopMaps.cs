using Jornal.Parsing.Documents;
using Jornal.Parsing.Specifications;
using Jornal.Workflow.Core;

namespace Jornal.Workflow.Infrastructure;

// AWJC has two different report layouts. File suffixes help pair names only after
// the document structure and its Drawing file have identified the current project.
internal static class WorkflowWaterjetShopMaps
{
    internal enum Kind { Unknown, Postprocessor, Calculation, Unrelated }
    internal sealed record Header(Kind Type, string[] Drawings, string[] Programs, bool InvalidFields);
    internal sealed record Map(ShopFile File, Kind Type, string[] Drawings, string[] Programs, bool InvalidFields = false);
    private sealed record Link(Map Map, ShopFile[] Programs);

    internal static async Task<ShopFile[]> SelectAsync(CuttingProject project, ShopFile[] files, CancellationToken token)
    {
        var maps = new List<Map>();
        foreach (var file in files.Where(f => f.IsReadable && f.Kind == ShopFileKind.Map))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var document = WorkflowShopDiscovery.ReadMap(file.SourcePath, token);
                var after = await ShopFileSystem.FingerprintAsync(file.SourcePath, token);
                if (after.Hash != file.Sha256) throw new WorkflowValidationException("Карта изменилась во время подбора. Обновите подбор.");
                maps.Add(Analyze(file, document, token));
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            { maps.Add(new(file with { Reason = "Не удалось подтвердить PDF AWJC; проверьте вручную" }, Kind.Unknown, [], [])); }
        }
        return Select(project, files, maps);
    }

    internal static Map Analyze(ShopFile file, SourceDocument document, CancellationToken token = default)
    {
        var drawings = new List<string>(); var programs = new List<string>(); var kinds = new HashSet<Kind>(); var invalid = false;
        foreach (var page in document.Pages)
        {
            token.ThrowIfCancellationRequested();
            if (AnalyzeHeader(page) is not { } header) continue; // Parts-list continuation pages are normal.
            kinds.Add(header.Type); invalid |= header.InvalidFields;
            drawings.AddRange(header.Drawings); programs.AddRange(header.Programs);
        }
        var kind = kinds.Count == 1 ? kinds.Single() : Kind.Unknown;
        if (kinds.Count == 0 && IsUnrelated(document, token)) kind = Kind.Unrelated;
        return new(file, kind, drawings.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), programs.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), invalid);
    }

    internal static Header? AnalyzeHeader(PdfPage page)
    {
        var lines = Lines(page);
        var text = Compact(string.Join(" ", lines.SelectMany(line => line).Select(w => w.Text)));
        var pp = text.Contains("FILES", StringComparison.Ordinal) && text.Contains("CNCFILE", StringComparison.Ordinal) &&
            text.Contains("DRAWING", StringComparison.Ordinal) && text.Contains("SHEETSIZE", StringComparison.Ordinal) && text.Contains("TOOLSETUP", StringComparison.Ordinal);
        var ce = (text.Contains("DRAWINGFILE", StringComparison.Ordinal) || text.Contains("ИМЯФАЙЛА", StringComparison.Ordinal)) &&
            (text.Contains("CUTTINGLENGTH", StringComparison.Ordinal) || text.Contains("ДЛИНАРЕЗА", StringComparison.Ordinal) || text.Contains("ДЛИННАРЕЗА", StringComparison.Ordinal)) &&
            (text.Contains("THICKNESS", StringComparison.Ordinal) || text.Contains("ТОЛЩИНА", StringComparison.Ordinal));
        if (!pp && !ce) return null;
        var source = Names(lines, ".dig", pp ? ["Drawing file", "Drawing"] : ["Drawing file", "Имя файла"]);
        var cnc = pp ? Names(lines, ".nc", ["CNC file"]) : (Values: Array.Empty<string>(), Invalid: false);
        return new(pp ? Kind.Postprocessor : Kind.Calculation, source.Values, cnc.Values,
            source.Invalid || source.Values.Length == 0 || cnc.Invalid || pp && cnc.Values.Length == 0);
    }

    internal static ShopFile[] Select(CuttingProject project, ShopFile[] files, IReadOnlyList<Map> maps)
    {
        var native = WorkflowShopDiscovery.Key(Path.GetFileName(project.NativePath!));
        var programs = files.Where(f => f.Kind == ShopFileKind.Program && f.Length > 0).ToArray();
        var current = new List<Link>(); var ignored = new Dictionary<string, string>(); var foreign = new HashSet<string>();
        var calculations = new List<Map>();
        foreach (var map in maps)
        {
            if (map.Type == Kind.Unrelated) { ignored[map.File.Name] = "Документ другого типа — пропускается"; continue; }
            if (map.Type == Kind.Unknown || map.InvalidFields || map.Drawings.Length == 0) continue;
            var drawingKeys = map.Drawings.Select(WorkflowShopDiscovery.Key).Distinct().ToArray();
            if (drawingKeys.Length != 1) continue; // Mixed projects in one PDF require review.
            var linked = MatchPrograms(map, programs);
            if (drawingKeys[0] != native)
            {
                ignored[map.File.Name] = "Отчёт AWJC другого проекта — пропускается";
                if (linked is not null) foreach (var program in linked) foreign.Add(program.Name);
                continue;
            }
            if (map.Type == Kind.Calculation) calculations.Add(map);
            if (linked is not null) current.Add(new(map, linked));
        }
        var pp = current.Where(l => l.Map.Type == Kind.Postprocessor).ToArray();
        var possibleCurrentPrograms = current.SelectMany(l => l.Programs).Select(p => p.Name).ToHashSet();
        var ppPrograms = pp.SelectMany(l => l.Programs).Select(f => f.Name).ToHashSet();
        var candidates = pp.ToList();
        foreach (var map in calculations)
        {
            var ce = current.SingleOrDefault(l => l.Map == map);
            if (pp.Length > 0 && (ce is not null && ce.Programs.All(p => ppPrograms.Contains(p.Name)) ||
                programs.Where(p => !foreign.Contains(p.Name) || possibleCurrentPrograms.Contains(p.Name)).All(p => ppPrograms.Contains(p.Name))))
                ignored[map.File.Name] = "Расчётный отчёт: УП проекта уже покрыты картами постпроцессора";
            else if (ce is not null) candidates.Add(ce); // A linked CE with layout can be the only map in an older job.
        }
        var conflicts = candidates.SelectMany(l => l.Programs.Select(p => (Program: p.Name, Hash: l.Map.File.Sha256)))
            .GroupBy(p => p.Program).Where(g => g.Select(p => p.Hash).Distinct().Count() > 1).Select(g => g.Key).ToHashSet();
        var certain = candidates.Where(l => !l.Programs.Any(p => conflicts.Contains(p.Name))).ToArray();
        var selectedMaps = certain.Select(l => l.Map.File.Name).ToHashSet();
        var selectedPrograms = certain.SelectMany(l => l.Programs).Select(p => p.Name).ToHashSet();
        return files.Select(file =>
        {
            if (!file.IsReadable) return file;
            var selected = file.Kind == ShopFileKind.Map ? selectedMaps.Contains(file.Name) : selectedPrograms.Contains(file.Name);
            var excluded = ignored.TryGetValue(file.Name, out var reason) || file.Kind == ShopFileKind.Program && foreign.Contains(file.Name) && !possibleCurrentPrograms.Contains(file.Name);
            var parsed = maps.SingleOrDefault(m => m.File.Name == file.Name);
            return file with { Suggested = selected, Ignored = !selected && excluded,
                Reason = selected ? "Карта AWJC связана с текущим проектом и УП" : excluded ? reason ?? "УП другого проекта — пропускается" : parsed?.File.Reason ?? file.Reason };
        }).ToArray();
    }

    private static ShopFile[]? MatchPrograms(Map map, ShopFile[] programs)
    {
        var direct = map.Programs.Select(name => programs.Where(p => WorkflowShopDiscovery.Key(p.Name) == WorkflowShopDiscovery.Key(name)).ToArray()).ToArray();
        var stem = Path.GetFileNameWithoutExtension(map.File.Name);
        if (stem.EndsWith("_pp", StringComparison.OrdinalIgnoreCase) || stem.EndsWith("_ce", StringComparison.OrdinalIgnoreCase)) stem = stem[..^3];
        var named = programs.Where(p => WorkflowShopDiscovery.Key(Path.GetFileNameWithoutExtension(p.Name)) == WorkflowShopDiscovery.Key(stem)).ToArray();
        if (map.Type == Kind.Postprocessor && direct.Length > 0 && direct.All(matches => matches.Length == 1))
        {
            var found = direct.Select(matches => matches[0]).Distinct().ToArray();
            // An existing header target and a different external-name target are competing interpretations.
            if (named.Any(p => !found.Contains(p))) return null;
            return found;
        }
        if (map.Programs.Length <= 1 && direct.All(matches => matches.Length == 0) && named.Length == 1) return named;
        return null;
    }

    private static string Compact(string value) => string.Concat(value.Where(c => !char.IsWhiteSpace(c))).ToUpperInvariant().TrimEnd(':');
    private static IReadOnlyList<PdfWord[]> Lines(PdfPage page)
    {
        var rows = new List<List<PdfWord>>();
        // AWJC places Files/Times below the layout picture, sometimes in the lower half.
        foreach (var word in page.Words.Where(w => !string.IsNullOrWhiteSpace(w.Text)).OrderByDescending(w => (w.Top + w.Bottom) / 2).ThenBy(w => w.Left))
        {
            var row = rows.LastOrDefault();
            if (row is null || Math.Abs(row.Average(w => (w.Top + w.Bottom) / 2) - (word.Top + word.Bottom) / 2) > 3) rows.Add(row = []);
            row.Add(word);
        }
        return rows.Select(row => row.OrderBy(w => w.Left).ToArray()).ToArray();
    }

    private static (string[] Values, bool Invalid) Names(IReadOnlyList<PdfWord[]> lines, string extension, string[] labels)
    {
        var values = new List<string>(); var invalid = false;
        foreach (var row in lines)
            foreach (var label in labels)
            {
                var count = 1;
                while (count <= Math.Min(3, row.Length) && Compact(string.Join(" ", row[..count].Select(w => w.Text))) != Compact(label)) count++;
                if (count > Math.Min(3, row.Length)) continue;
                var tail = row[count..];
                var endpoints = tail.Select((w, index) => (w, index)).Where(p => p.w.Text.Trim('"', '\'', ';').EndsWith(extension, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (endpoints.Length != 1) { invalid = true; continue; }
                values.Add(string.Join(" ", tail[..(endpoints[0].index + 1)].Select(w => w.Text)).Trim().Trim('"', '\'', ';'));
                break;
            }
        return (values.ToArray(), invalid);
    }

    private static bool IsUnrelated(SourceDocument document, CancellationToken token)
    {
        var text = Compact(string.Join(" ", document.Pages.SelectMany(p => p.Words).Select(w => w.Text)));
        if (text.Contains("CNC", StringComparison.Ordinal) || text.Contains(".DIG", StringComparison.Ordinal)) return false;
        if (WorkflowShopDiscovery.IsBankDetails(document)) return true;
        if (text.Contains(".DSP", StringComparison.Ordinal) && new HansPdfSpecificationParser().Parse(document, token).IsRecognized) return true;
        return text.Contains("МАСШТАБ", StringComparison.Ordinal) && text.Contains("РАЗРАБ", StringComparison.Ordinal) &&
            text.Contains("ПРОВ", StringComparison.Ordinal) && text.Contains("ЛИСТ", StringComparison.Ordinal);
    }
}

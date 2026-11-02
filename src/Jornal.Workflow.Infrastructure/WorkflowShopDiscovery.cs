using System.Text.RegularExpressions;
using Jornal.Parsing.Documents;
using Jornal.Workflow.Core;
using UglyToad.PdfPig;

namespace Jornal.Workflow.Infrastructure;

public static class WorkflowShopDiscovery
{
    internal enum Rule { Unknown, Laser, Hans, Waterjet, Gas, Plasma }
    internal static Rule MachineRule(WorkflowMachine machine)
    {
        var identities = WorkflowMachine.Defaults().Where(m => m.Id == machine.Id ||
            machine.LegacyKey is not null && m.LegacyKey == machine.LegacyKey ||
            machine.LegacyName is not null && m.LegacyName == machine.LegacyName).ToArray();
        var known = identities.Length == 1 ? identities[0] : null;
        return known?.LegacyName switch { "Лазер" => Rule.Laser, "HansLaser" => Rule.Hans, "ГАР" => Rule.Waterjet, "Газо-кислородная" => Rule.Gas,
            "Плазма" => Rule.Plasma, _ => Rule.Unknown };
    }

    public static Task<ShopProjectFiles[]> DiscoverAsync(string root, WorkflowOrder order, WorkflowWorkspace book, CancellationToken token = default,
        IWorkflowWordMapEditor? word = null, IProgress<WorkflowShopProgress>? progress = null) => Task.Run(async () =>
    {
        var paths = WorkflowShopFolderPaths.Plan(root, order, book.Employees, book.Customers, book.Machines);
        if (order.ActiveSpecifications.Any(s => !order.ActiveProjects.Any(p => p.Id == s.Id)))
            throw new WorkflowValidationException("У спецификации нет рабочего проекта. Передать её файлы автоматически пока нельзя.");
        var result = new List<ShopProjectFiles>();
        var hansCache = new Dictionary<string, WordMapReference[]?>();
        foreach (var path in paths)
        {
            token.ThrowIfCancellationRequested();
            var project = order.ActiveProjects.Single(p => p.Id == path.ProjectId);
            progress?.Report(new(WorkflowShopStage.Discovery, project.Name, Activity: "Ищем карты и программы проекта"));
            var bending = order.Specifications.SingleOrDefault(s => s.Id == project.Id)?.Current.Parameters.HasBending == true;
            result.Add(await DiscoverProjectAsync(project, book.Machines.Single(m => m.Id == project.MachineId), path.Path, bending, token, word, hansCache, progress));
        }
        return result.ToArray();
    }, token);

    internal static async Task<ShopProjectFiles> DiscoverProjectAsync(CuttingProject project, WorkflowMachine machine,
        string destination, bool hasBending, CancellationToken token, IWorkflowWordMapEditor? word = null,
        Dictionary<string, WordMapReference[]?>? hansCache = null, IProgress<WorkflowShopProgress>? progress = null)
    {
        var rule = MachineRule(machine);
        if (project.NativePath is null || !Path.IsPathFullyQualified(project.NativePath))
            return new(project, destination, [], null, "У проекта не задан путь к рабочему файлу.") { Machine = machine };
        try
        {
            ShopFileSystem.RequireOrdinaryPath(project.NativePath);
            if (!File.Exists(project.NativePath)) throw new WorkflowValidationException("Рабочий файл проекта не найден.");
            var folder = Path.GetDirectoryName(project.NativePath)!;
            if (destination.Length > 0 && (ShopFileSystem.Under(destination, folder) || ShopFileSystem.Under(folder, destination)))
                throw new WorkflowValidationException("Рабочая папка и папка задания не должны находиться друг внутри друга.");
            var programExtension = rule switch { Rule.Laser => ".cnc", Rule.Hans => ".gnc", _ => ".nc" };
            var mapExtension = rule == Rule.Hans ? ".doc" : ".pdf";
            var extensions = new[] { programExtension, mapExtension };
            var files = new List<ShopFile>();
            var candidates = Directory.EnumerateFiles(folder).Where(f => ShopFileSystem.IsCandidate(f, extensions)).Take(2001).ToArray();
            if (candidates.Length > 2000) throw new WorkflowValidationException("Слишком много кандидатов в одной рабочей папке. Проверьте путь проекта.");
            foreach (var file in candidates.Order(StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                var kind = Path.GetExtension(file).Equals(programExtension, StringComparison.OrdinalIgnoreCase) ? ShopFileKind.Program : ShopFileKind.Map;
                try
                {
                    progress?.Report(new(WorkflowShopStage.Discovery, project.Name, Path.GetFileName(file), Activity: "Читаем файл проекта"));
                    var fingerprint = await ShopFileSystem.FingerprintAsync(file, token);
                    files.Add(new(Path.GetFileName(file), file, fingerprint.Length, fingerprint.Hash, kind, false,
                        kind == ShopFileKind.Program ? "Проверьте принадлежность текущему проекту" : "Карта не подтверждена автоматически"));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    files.Add(new(Path.GetFileName(file), file, 0, "", kind, false,
                        "Файл недоступен. Закройте его и обновите подбор; ненужный файл можно оставить невыбранным.") { IsReadable = false });
                }
            }
            var links = new Dictionary<string, string[]>();
            var foreignPrograms = new HashSet<string>();
            if (rule is Rule.Laser or Rule.Gas or Rule.Plasma)
                foreach (var file in files.Where(f => f.IsReadable && f.Kind == ShopFileKind.Map).ToArray())
                {
                    try
                    {
                        progress?.Report(new(WorkflowShopStage.Discovery, project.Name, file.Name, Activity: "Проверяем карту раскроя"));
                        var pdf = ReadMap(file.SourcePath, token);
                        var references = BlockReferences(pdf, project, rule);
                        var after = await ShopFileSystem.FingerprintAsync(file.SourcePath, token);
                        if (after.Hash != file.Sha256) throw new WorkflowValidationException("Карта изменилась во время подбора. Повторите подбор.");
                        if (references is not null && references.Length > 0)
                        {
                            var missing = references.Where(r => files.Count(f => f.Kind == ShopFileKind.Program && f.IsReadable && Key(f.Name) == Key(r) && f.Length > 0) != 1).ToArray();
                            if (missing.Length == 0) links[file.Name] = references;
                            else files[files.IndexOf(file)] = file with { Reason = "Не найдена однозначная доступная УП из карты: " + string.Join(", ", missing) };
                        }
                        else if (ForeignReferences(pdf, project) is { } foreign)
                        {
                            foreach (var reference in foreign) foreignPrograms.Add(Key(reference));
                            files[files.IndexOf(file)] = file with { Ignored = true, Reason = "Карта другого проекта — пропускается" };
                        }
                        else if (IsBankDetails(pdf)) files[files.IndexOf(file)] = file with { Ignored = true, Reason = "Реквизиты — пропускаются" };
                        else if (IsUnrelatedBlockDocument(pdf)) files[files.IndexOf(file)] = file with { Ignored = true, Reason = "Документ без признаков карты БЛОК — пропускается" };
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
                    { files[files.IndexOf(file)] = file with { Reason = ex is WorkflowValidationException ? ex.Message : "Не удалось подтвердить PDF; откройте и проверьте вручную" }; }
                }
            // Two maps for the same UP may be competing views/versions. Never choose one by date or suffix.
            var duplicates = links.SelectMany(pair => pair.Value.Select(Key).Distinct().Select(reference => (Reference: reference, Hash: files.Single(f => f.Name == pair.Key).Sha256)))
                .GroupBy(v => v.Reference).Where(g => g.Select(v => v.Hash).Distinct().Count() > 1).Select(g => g.Key).ToHashSet();
            var certain = links.Where(p => !p.Value.Any(v => duplicates.Contains(Key(v)))).ToArray();
            foreach (var pair in links.Where(p => p.Value.Any(v => duplicates.Contains(Key(v)))))
            {
                var index = files.FindIndex(f => f.Name == pair.Key);
                files[index] = files[index] with { Reason = "Несколько разных карт ссылаются на одну УП: " + string.Join(", ", pair.Value.Where(v => duplicates.Contains(Key(v)))) + ". Карты: " +
                    string.Join(", ", links.Where(other => other.Value.Any(v => pair.Value.Any(reference => Key(reference) == Key(v)))).Select(other => other.Key)) + ". Выберите актуальную карту." };
            }
            files = files.Select(f => !f.IsReadable ? f : f with
            {
                Suggested = rule == Rule.Hans ? f.Length > 0 : f.Kind == ShopFileKind.Map ? certain.Any(p => p.Key == f.Name) : certain.Any(p => p.Value.Any(r => Key(r) == Key(f.Name))),
                Ignored = f.Kind == ShopFileKind.Program ? foreignPrograms.Contains(Key(f.Name)) && !links.Values.Any(refs => refs.Any(r => Key(r) == Key(f.Name))) : f.Ignored,
                Reason = rule == Rule.Hans ? "Комплект HansLaser в папке текущего проекта" :
                    (f.Kind == ShopFileKind.Map ? certain.Any(p => p.Key == f.Name) : certain.Any(p => p.Value.Any(r => Key(r) == Key(f.Name)))) ?
                    "Проект и ссылка УП совпали; формат соответствует станку" : f.Kind == ShopFileKind.Program && foreignPrograms.Contains(Key(f.Name)) &&
                        !links.Values.Any(refs => refs.Any(r => Key(r) == Key(f.Name))) ? "УП другого проекта — пропускается" : f.Reason
            }).ToList();
            if (rule == Rule.Waterjet) files = (await WorkflowWaterjetShopMaps.SelectAsync(project, files.ToArray(), token)).ToList();
            var nativeNames = ShopFileSystem.Names(folder, Path.GetExtension(project.NativePath));
            progress?.Report(new(WorkflowShopStage.Discovery, project.Name, Activity: "Сопоставляем карты и программы"));
            var hans = rule == Rule.Hans ? await WorkflowHansShopMaps.SelectAsync(project, files.ToArray(), nativeNames, word, hansCache ?? new(), token,
                name => progress?.Report(new(WorkflowShopStage.Discovery, project.Name, name, Activity: "Проверяем принадлежность карт HansLaser"))) : null;
            if (hans is not null) files = hans.Files.ToList();
            var otherNative = nativeNames.Where(n => !n.Equals(Path.GetFileName(project.NativePath), StringComparison.OrdinalIgnoreCase));
            var ambiguousFolder = rule != Rule.Hans && otherNative.Any(n => Key(n) == Key(Path.GetFileName(project.NativePath)));
            var bending = hasBending ? await ShopFileSystem.TreeAsync(Path.Combine(folder, "ГИБКА"), token,
                onFile: name => progress?.Report(new(WorkflowShopStage.Discovery, project.Name, "ГИБКА/" + name, Activity: "Проверяем файлы гибки"))) : null;
            var reviewReasons = new List<string>();
            if (ambiguousFolder) reviewReasons.Add("В папке есть другой рабочий проект того же формата: " + string.Join(", ", otherNative) + ". Проверьте принадлежность УП и карт.");
            foreach (var file in files.Where(f => !f.Ignored && !f.Suggested).OrderByDescending(f => !f.IsReadable))
                reviewReasons.Add($"«{file.Name}»: {file.Reason}");
            return new(project, destination, files.ToArray(), bending, rule == Rule.Unknown ?
                "Для этого станка правило ещё не подтверждено. Автоматическая передача пока недоступна." : null)
            {
                CandidateExtensions = extensions, NativeNames = nativeNames, Machine = machine, WordMatch = hans?.Match, NativeProof = hans?.NativeProof,
                ReviewReason = reviewReasons.Count == 0 ? null : string.Join(Environment.NewLine, reviewReasons.Take(2)) +
                    (reviewReasons.Count > 2 ? $"\nЕщё причин: {reviewReasons.Count - 2}. Подробности указаны у файлов ниже." : "")
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WorkflowValidationException)
        { return new(project, destination, [], null, ex is WorkflowValidationException ? ex.Message : "Не удалось прочитать рабочую папку. Проверьте доступ и повторите подбор.") { Machine = machine }; }
    }

    internal static bool IsBlockContinuation(Jornal.Parsing.Documents.PdfPage page)
    {
        var words = page.Words.Select(w => w.Text.ToUpperInvariant()).ToHashSet();
        return !words.Contains("CNC") && !words.Contains("КОММЕНТАРИЙ") &&
            words.Contains("НАЗВАНИЕ") && words.Contains("ДЕТАЛИ") && words.Contains("КОЛ-ВО") && words.Contains("ВСЕГО");
    }

    internal static string Key(string value) => Regex.Replace(value.Replace('\\', '/').Split('/')[^1], @"\s+", "").Trim('"', '\'').ToUpperInvariant();

    internal static SourceDocument ReadMap(string path, CancellationToken token)
    {
        if (new FileInfo(path).Length > 50 * 1024 * 1024) throw new WorkflowValidationException("Карта больше 50 МБ. Сохраните карты меньшего размера и обновите подбор.");
        using var pdf = PdfDocument.Open(path);
        if (pdf.NumberOfPages > 100) throw new WorkflowValidationException("В PDF больше 100 страниц. Разделите карты на несколько PDF и обновите подбор.");
        var pages = new List<PdfPage>();
        foreach (var page in pdf.GetPages())
        {
            token.ThrowIfCancellationRequested();
            // DefaultWordExtractor respects text runs in BLOK headers. NearestNeighbour may merge
            // the neighbouring Project/CNC columns when a long project name reaches the label.
            pages.Add(new(page.Number, page.Width, page.Height, page.GetWords().Select(w => new PdfWord(w.Text,
                w.BoundingBox.Left, w.BoundingBox.Bottom, w.BoundingBox.Right, w.BoundingBox.Top)).ToArray()));
        }
        return new(DocumentKind.Pdf, null, [], pages);
    }

    internal static string[]? BlockReferences(SourceDocument document, CuttingProject project, Rule rule)
    {
        var refs = new List<string>();
        foreach (var page in document.Pages)
        {
            var projectField = Field(page, "ПРОЕКТ", "ПРОЭКТ");
            if (projectField.Length == 0 && refs.Count > 0 && IsBlockContinuation(page)) continue;
            if (Key(projectField) != Key(Path.GetFileName(project.NativePath!))) return null;
            var cnc = Field(page, "CNC"); if (string.IsNullOrWhiteSpace(cnc)) return null;
            var extension = rule == Rule.Laser ? ".cnc" : ".nc";
            if (!cnc.Trim().Trim('"').EndsWith(extension, StringComparison.OrdinalIgnoreCase)) return null;
            refs.Add(cnc.Trim().Trim('"'));
        }
        return refs.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static string[]? ForeignReferences(SourceDocument document, CuttingProject project)
    {
        if (document.Pages.Count == 0) return null;
        var refs = new List<string>(); string? foreignProject = null;
        foreach (var page in document.Pages)
        {
            var name = Key(Field(page, "ПРОЕКТ", "ПРОЭКТ")); var cnc = Field(page, "CNC");
            if (name.Length == 0 && refs.Count > 0 && IsBlockContinuation(page)) continue;
            if (!name.EndsWith(".RCAM", StringComparison.Ordinal) || name == Key(Path.GetFileName(project.NativePath!)) ||
                string.IsNullOrWhiteSpace(cnc) || foreignProject is not null && foreignProject != name) return null;
            foreignProject = name; refs.Add(cnc.Trim().Trim('"'));
        }
        return refs.ToArray();
    }

    internal static bool IsBankDetails(SourceDocument document)
    {
        var text = string.Join(" ", document.Pages.SelectMany(p => p.Words).Select(w => w.Text)).ToUpperInvariant();
        if (Regex.IsMatch(text, @"\bCNC\b|ФАЙЛ\s*УП|\.RCAM\b|\.CNC\b|\.NC\b")) return false;
        return Regex.IsMatch(text, @"\bИНН\b") && Regex.IsMatch(text, @"\bБИК\b") &&
            Regex.IsMatch(text, @"РЕКВИЗИТЫ|РАСЧ[ЕЁ]ТНЫЙ\s+СЧ[ЕЁ]Т");
    }

    internal static bool IsUnrelatedBlockDocument(SourceDocument document) => document.Pages.Count > 0 &&
        document.Pages.All(p => p.Words.Any(w => !string.IsNullOrWhiteSpace(w.Text)) &&
            string.IsNullOrWhiteSpace(Field(p, "ПРОЕКТ", "ПРОЭКТ")) && string.IsNullOrWhiteSpace(Field(p, "CNC")) &&
            !p.Words.Any(w => Key(w.Text).Contains(".RCAM", StringComparison.Ordinal) || Key(w.Text).Contains(".CNC", StringComparison.Ordinal) || Key(w.Text).Contains(".NC", StringComparison.Ordinal)));

    // Read the labelled row in its coordinate column, stopping at the next header label.
    // Flattening the complete page mixes project names with CNC names in BLOK reports.
    internal static string Field(PdfPage page, params string[] labels)
    {
        static string Label(PdfWord w) => w.Text.Trim().TrimEnd(':').ToUpperInvariant();
        var header = page.Words.Where(w => w.Top > page.Height * .5).ToArray();
        var label = header.FirstOrDefault(w => labels.Contains(Label(w), StringComparer.Ordinal));
        if (label is null) return "";
        var end = header.Where(w => w.Left > label.Right && Math.Abs(w.Bottom - label.Bottom) < 4 &&
            (Label(w) is "CNC" or "ПРОЕКТ" or "ПРОЭКТ" or "СТАНОК")).Select(w => w.Left).DefaultIfEmpty(page.Width).Min();
        return string.Join(" ", header.Where(w => w.Left >= label.Right - .5 && w.Left < end && Math.Abs(w.Bottom - label.Bottom) < 4 && w.Text != ":").OrderBy(w => w.Left).Select(w => w.Text));
    }
}

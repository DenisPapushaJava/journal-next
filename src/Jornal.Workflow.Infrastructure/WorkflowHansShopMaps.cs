using System.Security.Cryptography;
using Jornal.Workflow.Core;

namespace Jornal.Workflow.Infrastructure;

internal sealed record HansShopSelection(ShopFile[] Files, WordMapMatch? Match, ShopTreeFile[]? NativeProof);

internal static class WorkflowHansShopMaps
{
    internal static async Task<HansShopSelection> SelectAsync(CuttingProject project, ShopFile[] files, string[] nativeNames,
        IWorkflowWordMapEditor? word, Dictionary<string, WordMapReference[]?> cache, CancellationToken token, Action<string>? inspecting = null)
    {
        var folder = Path.GetDirectoryName(project.NativePath!)!;
        if (nativeNames.Length > 2000) throw new WorkflowValidationException("В папке больше 2000 рабочих проектов. Проверьте путь проекта.");
        var currentName = Path.GetFileName(project.NativePath!);
        var others = nativeNames.Where(n => !ShopFileSystem.PathComparer.Equals(n, currentName) && MayHaveContent(Path.Combine(folder, n))).ToArray();
        if (others.Length == 0) return new(files, null, null); // Keep the established single-project folder flow.
        // DSP is never copied or edited here. CAM may keep it open exclusively;
        // unreadable DSP bytes merely disable the identical-copy shortcut.
        (long Length, string Hash)? native = null;
        inspecting?.Invoke(currentName);
        try { native = await ShopFileSystem.FingerprintAsync(project.NativePath!, token); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        var aliases = new List<string> { currentName }; var distinct = new List<string>();
        var proof = new List<ShopTreeFile>();
        if (native is { } original) proof.Add(new(currentName, original.Length, original.Hash));
        foreach (var name in others)
        {
            inspecting?.Invoke(name);
            (long Length, string Hash)? fingerprint = null;
            if (native is not null)
                try { fingerprint = await ShopFileSystem.FingerprintAsync(Path.Combine(folder, name), token); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            if (fingerprint is { } copy && native == fingerprint)
            { aliases.Add(name); proof.Add(new(name, copy.Length, copy.Hash)); }
            else distinct.Add(name);
        }
        var match = new WordMapMatch(aliases.ToArray(), distinct.ToArray());
        if (distinct.Count == 0) return new(files, match, proof.ToArray());

        var currentKeys = aliases.Select(WorkflowWordMapRules.ProjectKey).ToHashSet();
        var otherKeys = distinct.Select(WorkflowWordMapRules.ProjectKey).ToHashSet();
        var cards = new Dictionary<string, WordMapReference[]>(); var result = files.ToDictionary(f => f.Name);
        foreach (var file in files.Where(f => f.IsReadable && f.Kind == ShopFileKind.Map))
        {
            inspecting?.Invoke(file.Name);
            try
            {
                var key = file.SourcePath + "\n" + file.Sha256;
                if (!cache.TryGetValue(key, out var references))
                {
                    if (file.Length > 50 * 1024 * 1024) throw new WorkflowValidationException("DOC больше 50 МБ. Разделите карты в CAM.");
                    byte[] bytes;
                    await using (var input = new FileStream(file.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous))
                    {
                        if (input.Length != file.Length) throw new WorkflowValidationException("DOC изменился во время подбора. Обновите подбор.");
                        bytes = new byte[checked((int)file.Length)]; await input.ReadExactlyAsync(bytes, token);
                        if (input.Length != file.Length) throw new WorkflowValidationException("DOC изменился во время подбора. Обновите подбор.");
                    }
                    if (bytes.LongLength != file.Length || Convert.ToHexString(SHA256.HashData(bytes)) != file.Sha256)
                        throw new WorkflowValidationException("DOC изменился во время подбора. Завершите сохранение в CAM и обновите подбор.");
                    references = word is null ? null : await word.InspectAsync(bytes, token);
                    cache[key] = references;
                }
                if (references is null) throw new WorkflowValidationException("Не удалось прочитать поля «Заказ» и «Файл УП». Проверьте DOC и выберите нужные файлы.");
                cards[file.Name] = references;
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                if (ex is WordMapUnavailableException) word = null; // Do not repeat a timed-out/unavailable Word startup for every DOC.
                result[file.Name] = file with { Suggested = false, Reason = ex is WorkflowValidationException or WordMapUnavailableException ? ex.Message : "DOC недоступен для проверки. Закройте файл или выберите нужные файлы вручную." };
            }
        }
        string? KnownOwner(WordMapReference card)
        {
            var key = WorkflowWordMapRules.ProjectKey(card.Order);
            return currentKeys.Contains(key) ? "current" : otherKeys.Contains(key) ? key : null;
        }
        var anchors = cards.ToDictionary(p => p.Key, p =>
        {
            var known = p.Value.Select(KnownOwner).Where(o => o is not null).Distinct().ToArray();
            return known.Length == 1 ? known[0] : null;
        });
        // CAM may fill Order on the first card only. Empty later blocks inherit the
        // document's single explicit project; competing GNC claims still veto this inference.
        string? AnchoredOwner(WordMapReference card, string file) => KnownOwner(card) ??
            (string.IsNullOrWhiteSpace(card.Order) ? anchors[file] : null);
        var owners = cards.SelectMany(p => p.Value.Select(c => (Card: c, Owner: AnchoredOwner(c, p.Key))))
            .Where(v => v.Owner is not null && v.Card.Program.Length > 0)
            .GroupBy(v => WorkflowWordMapRules.ProgramKey(v.Card.Program)).ToDictionary(g => g.Key, g => g.Select(v => v.Owner!).Distinct().ToArray());
        string? Owner(WordMapReference card, string file) => AnchoredOwner(card, file) ??
            (owners.TryGetValue(WorkflowWordMapRules.ProgramKey(card.Program), out var found) && found.Length == 1 ? found[0] : null);
        var programs = files.Where(f => f.Kind == ShopFileKind.Program).GroupBy(f => WorkflowWordMapRules.ProgramKey(f.Name)).ToDictionary(g => g.Key, g => g.ToArray());
        var ownMaps = new Dictionary<string, string[]>();
        foreach (var (name, references) in cards)
        {
            var file = result[name]; var projectOwners = references.Select(c => Owner(c, name)).ToArray();
            var keys = references.Select(r => WorkflowWordMapRules.ProgramKey(r.Program)).Distinct().ToArray();
            var conflict = keys.Any(k => owners.TryGetValue(k, out var claimed) && claimed.Length > 1);
            if (references.Length == 0) result[name] = file with { Suggested = false, Reason = "Структура карты DOC не распознана. Проверьте документ и исключите его, если он не нужен в задании." };
            else if (!conflict && projectOwners.All(o => o is not null && o != "current"))
                result[name] = file with { Suggested = false, Ignored = true, Reason = "Поле «Заказ» или связанная УП указывают на другой DSP — пропускается" };
            else if (conflict) result[name] = file with { Suggested = false, Reason = "Одна УП указана у разных DSP. Проверьте принадлежность карты и GNC." };
            else if (projectOwners.Any(o => o is not null && o != "current") && projectOwners.Contains("current"))
                result[name] = file with { Suggested = false, Reason = "В одном DOC карты разных проектов. Разделите файл в CAM или исключите его из комплекта." };
            else if (projectOwners.All(o => o == "current"))
            {
                result[name] = file = file with { ConfirmedProjectLink = true };
                var missing = keys.Where(k => !programs.TryGetValue(k, out var found) || found.Length != 1 || !found[0].IsReadable || found[0].Length == 0).ToArray();
                if (missing.Length == 0) ownMaps[name] = keys;
                else result[name] = file with { Suggested = false, Reason = "УП из DOC не найдена однозначно: " + string.Join(", ", missing.Select(k => k.Length == 0 ? "поле «Файл УП» пусто" : k)) + ". Проверьте или выберите комплект." };
            }
            else result[name] = file with { Suggested = false, Reason = "Принадлежность DOC не установлена: поля «Заказ»/«Файл УП» пусты или не совпали с DSP и GNC. Выберите нужные файлы." };
        }
        var duplicatePrograms = ownMaps.SelectMany(pair => pair.Value.Select(k => (Key: k, Hash: result[pair.Key].Sha256)))
            .GroupBy(v => v.Key).Where(g => g.Select(v => v.Hash).Distinct().Count() > 1).Select(g => g.Key).ToHashSet();
        var selectedPrograms = new HashSet<string>();
        foreach (var (name, keys) in ownMaps)
            if (keys.Any(duplicatePrograms.Contains)) result[name] = result[name] with { Suggested = false, Reason = "Несколько разных DOC ссылаются на одну УП. Выберите актуальную карту." };
            else
            {
                result[name] = result[name] with { Suggested = true, ConfirmedProjectLink = true, Reason = "Проект и ссылка GNC подтверждены по DOC" };
                selectedPrograms.UnionWith(keys);
            }
        foreach (var file in files.Where(f => f.Kind == ShopFileKind.Program))
        {
            var key = WorkflowWordMapRules.ProgramKey(file.Name);
            var foreign = owners.TryGetValue(key, out var known) && known.All(o => o != "current");
            result[file.Name] = file with { Suggested = selectedPrograms.Contains(key), Ignored = foreign,
                ConfirmedProjectLink = selectedPrograms.Contains(key), Reason = selectedPrograms.Contains(key) ? "GNC указана в карте текущего проекта" :
                    foreign ? "GNC указана только в картах другого DSP — пропускается" : "Принадлежность GNC не подтверждена картой. Проверьте файл и выберите при необходимости." };
        }
        return new(files.Select(f => result[f.Name]).ToArray(), match, aliases.Count > 1 ? proof.ToArray() : null);
    }
    private static bool MayHaveContent(string path)
    {
        try { return new FileInfo(path).Length > 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }
}

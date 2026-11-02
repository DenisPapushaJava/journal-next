using Jornal.Workflow.Core;
using System.Security.Cryptography;

namespace Jornal.Workflow.Infrastructure;

public enum MapSigningStatus { Signed, Unchanged, Skipped, Failed, NeedsVerification }
public sealed record MapSigningResult(Guid ProjectId, string ProjectName, string? FilePath, MapSigningStatus Status, int Pages, string Message, string? BackupPath = null)
{
    // Exact bytes prepared/verified by the editor; copying must still revalidate the source.
    public ShopFile? VerifiedFile { get; init; }
    // Text already passed to the editor/checker; never reconstructed by the result UI.
    public string? Caption { get; init; }
    public bool HasCaption => !string.IsNullOrEmpty(Caption);
    public bool NeedsAttention => Status is MapSigningStatus.Skipped or MapSigningStatus.Failed or MapSigningStatus.NeedsVerification;
    public bool MayHaveChanged => Status is MapSigningStatus.Signed or MapSigningStatus.NeedsVerification;
    public string Details => ProjectName + (FilePath is null ? "" : " · " + Path.GetFileName(FilePath)) + ": " + Message +
        (Status == MapSigningStatus.NeedsVerification && BackupPath is not null ? "\nПуть резервной копии: " + BackupPath : "");
}

public static class WorkflowMapSigning
{
    internal const string BackupFolder = ".workflow-map-originals";
    public static bool Supports(WorkflowMachine machine) => machine.ImportProfile == "block-html/v1" &&
        WorkflowShopDiscovery.MachineRule(machine) is WorkflowShopDiscovery.Rule.Laser or WorkflowShopDiscovery.Rule.Gas or WorkflowShopDiscovery.Rule.Plasma ||
        machine.ImportProfile == "hans-pdf/v1" && WorkflowShopDiscovery.MachineRule(machine) == WorkflowShopDiscovery.Rule.Hans ||
        machine.ImportProfile == "waterjet-awjc/v1" && WorkflowShopDiscovery.MachineRule(machine) == WorkflowShopDiscovery.Rule.Waterjet;

    public static Task<MapSigningResult[]> SignAsync(WorkflowOrder order, WorkflowWorkspace book, Guid? projectId,
        Func<CancellationToken, Task> validate, CancellationToken token = default, IWorkflowWordMapEditor? word = null) =>
        SignAsync(order, book, projectId, validate, token, word, WorkflowMapCommit.Default);

    private sealed class BatchValidationException(Exception inner) : Exception(inner.Message, inner);

    internal static Task<MapSigningResult[]> SignAsync(WorkflowOrder order, WorkflowWorkspace book, Guid? projectId,
        Func<CancellationToken, Task> validate, CancellationToken token, IWorkflowWordMapEditor? word, WorkflowMapCommit commit) =>
        SignCoreAsync(order, book, projectId, validate, token, word, commit);

    public static Task<MapSigningResult[]> SignSelectedAsync(WorkflowOrder order, WorkflowWorkspace book, ShopTransferSelection[] selections,
        Func<CancellationToken, Task> validate, CancellationToken token = default, IWorkflowWordMapEditor? word = null, IProgress<string>? progress = null,
        IProgress<WorkflowShopProgress>? details = null)
    {
        var included = order.Items.Where(i => i.IsIncluded && order.ActiveProjects.Any(p => p.Id == i.CurrentProjectId)).Select(i => i.CurrentProjectId).ToHashSet();
        if (selections.Length != included.Count || !included.SetEquals(selections.Select(s => s.Project.Project.Id)) ||
            selections.Any(s => s.Project.Project != order.ActiveProjects.Single(p => p.Id == s.Project.Project.Id) || !s.Files.Any(f => f.Kind == ShopFileKind.Map)))
            throw new WorkflowValidationException("Состав заявки изменился. Обновите подбор перед подписанием.");
        var selected = selections.ToDictionary(s => s.Project.Project.Id);
        return SignCoreAsync(order, book, null, validate, token, word, WorkflowMapCommit.Default, selected, progress, details);
    }

    private static Task<MapSigningResult[]> SignCoreAsync(WorkflowOrder order, WorkflowWorkspace book, Guid? projectId,
        Func<CancellationToken, Task> validate, CancellationToken token, IWorkflowWordMapEditor? word, WorkflowMapCommit commit,
        IReadOnlyDictionary<Guid, ShopTransferSelection>? selected = null, IProgress<string>? progress = null, IProgress<WorkflowShopProgress>? details = null) => Task.Run(async () =>
    {
        if (order.IsDeleted || order.HasProjectRename) throw new WorkflowValidationException("Восстановите заявку или завершите переименование проекта.");
        var projects = projectId is { } id ? order.ActiveProjects.Where(p => p.Id == id).ToArray() :
            // A trashed position keeps its inclusion flag for restoration, but has no active variants.
            order.Items.Where(i => i.IsIncluded && order.ActiveProjects.Any(p => p.ItemId == i.Id))
                .OrderBy(i => i.Number).Select(i => order.ActiveProjects.Single(p => p.Id == i.CurrentProjectId)).ToArray();
        if (projects.Length == 0) throw new WorkflowValidationException("Добавьте или включите проект в состав заявки.");
        var results = new List<MapSigningResult>(); string? wordUnavailable = null;
        var needsAttention = false;
        void AddResult(MapSigningResult result)
        {
            results.Add(result);
            needsAttention |= result.NeedsAttention;
        }
        var completed = 0;
        int? total = selected?.Values.Sum(s => s.Files.Count(f => f.Kind == ShopFileKind.Map));
        async Task Validate(CancellationToken cancellation)
        {
            try { await validate(cancellation); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is not OutOfMemoryException) { throw new BatchValidationException(ex); }
        }
        foreach (var project in projects)
        {
            if (selected is not null && needsAttention) return results.ToArray();
            token.ThrowIfCancellationRequested();
            string? caption = null;
            MapSigningResult Result(string? path, MapSigningStatus status, string message, int pages = 0, string? backup = null) =>
                new(project.Id, project.Name, path, status, pages, message, backup)
                { Caption = status is MapSigningStatus.Signed or MapSigningStatus.Unchanged ? caption : null };
            var machine = book.Machines.Single(m => m.Id == project.MachineId);
            if (!Supports(machine))
            { AddResult(Result(null, MapSigningStatus.Skipped, "Поддерживаются PDF БЛОК/AWJC и DOC HansLaser через Microsoft Word.")); continue; }
            if (machine.ImportProfile == "hans-pdf/v1" && wordUnavailable is not null)
            { AddResult(Result(null, MapSigningStatus.Failed, wordUnavailable)); continue; }
            try
            {
                var text = WorkflowProjectComment.Format(order, project, book.Employees, book.Customers);
                caption = text;
                if (project.NativePath is null || !Path.IsPathFullyQualified(project.NativePath))
                    throw new WorkflowValidationException("Не задан полный путь к рабочему проекту.");
                ShopFileSystem.RequireOrdinaryPath(project.NativePath);
                if (!File.Exists(project.NativePath)) throw new WorkflowValidationException("Рабочий файл проекта не найден.");
                var folder = Path.GetDirectoryName(project.NativePath)!;
                var hans = machine.ImportProfile == "hans-pdf/v1";
                // Direct signing follows the same membership rules, without a copy destination.
                var hansPlan = hans && selected is null ? await WorkflowShopDiscovery.DiscoverProjectAsync(project, machine, "", false, token, word) : null;
                if (hansPlan?.Problem is { } discoveryProblem) throw new WorkflowValidationException(discoveryProblem);
                if (hansPlan?.WordMatch is not null)
                    foreach (var uncertain in hansPlan.Files.Where(f => f.Kind == ShopFileKind.Map && !f.Ignored && !f.Suggested && !f.ConfirmedProjectLink))
                        AddResult(Result(uncertain.SourcePath, MapSigningStatus.Skipped, uncertain.Reason + " Уточните файлы через «Отправить в работу…»."));
                var extension = hans ? ".doc" : ".pdf";
                var files = selected is not null ? selected[project.Id].Files.Where(f => f.Kind == ShopFileKind.Map).Select(f => f.SourcePath).ToArray() :
                    hansPlan?.WordMatch is not null ? hansPlan.Files.Where(f => f.Kind == ShopFileKind.Map && !f.Ignored && (f.Suggested || f.ConfirmedProjectLink)).Select(f => f.SourcePath).ToArray() :
                    Directory.EnumerateFiles(folder).Where(p => Path.GetExtension(p).Equals(extension, StringComparison.OrdinalIgnoreCase) && !Path.GetFileName(p).StartsWith("~$", StringComparison.Ordinal))
                        .Order(StringComparer.Ordinal).Take(2001).ToArray();
                if (files.Length > 2000) throw new WorkflowValidationException("В папке слишком много карт. Проверьте путь проекта.");
                var found = false;
                foreach (var path in files)
                {
                    if (selected is not null && needsAttention) return results.ToArray();
                    token.ThrowIfCancellationRequested();
                    var verified = false;
                    try
                    {
                        progress?.Report("Подписываем: " + Path.GetFileName(path));
                        details?.Report(new(WorkflowShopStage.Signing, project.Name, Path.GetFileName(path), completed, total, "Подписываем карту или проверяем совпадение подписи"));
                        ShopFile? expected = selected?[project.Id].Files.Single(f => f.SourcePath == path);
                        if (expected is not null)
                        {
                            if (!ShopFileSystem.PathComparer.Equals(Path.GetFullPath(path), Path.GetFullPath(Path.Combine(folder, expected.Name))) ||
                                !Path.GetExtension(path).Equals(extension, StringComparison.OrdinalIgnoreCase))
                                throw new WorkflowValidationException("Выбранная карта находится вне папки проекта или имеет другой формат.");
                            await Validate(token);
                        }
                        var bytes = await ReadAsync(path, token);
                        if (expected is not null && (bytes.LongLength != expected.Length || Convert.ToHexString(SHA256.HashData(bytes)) != expected.Sha256))
                            throw new WorkflowValidationException("Карта изменилась после выбора. Обновите подбор.");
                        MapSigningResult Verified(MapSigningResult result, byte[] content)
                        {
                            if (expected is null || result.NeedsAttention) return result;
                            verified = true;
                            return result with { VerifiedFile = expected with { Length = content.LongLength, Sha256 = Convert.ToHexString(SHA256.HashData(content)) } };
                        }
                        if (hans)
                        {
                            if (word is null) throw new WordMapUnavailableException("Подписание DOC требует Microsoft Word на Windows. Файл не изменён.");
                            var match = selected?[project.Id].Project.WordMatch ?? hansPlan?.WordMatch;
                            var confirmed = selected?[project.Id].Reviewed == true || (expected ?? hansPlan?.Files.SingleOrDefault(f => f.SourcePath == path))?.ConfirmedProjectLink == true;
                            var doc = match is null ? await word.PrepareAsync(bytes, Path.GetFileName(project.NativePath), text, token) :
                                await word.PrepareMatchedAsync(bytes, Path.GetFileName(project.NativePath), text, match with { AllowUnknownOrder = confirmed }, token);
                            if (doc is null)
                            {
                                if (selected is not null) throw new WorkflowValidationException("Выбранный DOC не является картой текущего проекта.");
                                continue;
                            }
                            found = true;
                            if (doc.Bytes is null) AddResult(Verified(Result(path, MapSigningStatus.Unchanged, "Подпись уже совпадает", doc.Pages), bytes));
                            else
                            {
                                var saved = await commit.SaveAsync(path, bytes, doc.Bytes, Validate, token);
                                AddResult(Verified(Result(path, saved.NeedsVerification ? MapSigningStatus.NeedsVerification : MapSigningStatus.Signed,
                                    saved.Problem ?? $"Подписано карт: {doc.ChangedCards}", doc.Pages, saved.BackupPath), doc.Bytes));
                                if (saved.NeedsVerification) return results.ToArray();
                            }
                            continue;
                        }
                        var edit = WorkflowMapPdf.Prepare(bytes, project, WorkflowShopDiscovery.MachineRule(machine), text, token);
                        if (edit is null)
                        {
                            if (selected is not null) throw new WorkflowValidationException("Выбранный PDF не является картой текущего проекта.");
                            continue;
                        }
                        found = true;
                        if (edit.Bytes is null)
                        { AddResult(Verified(Result(path, MapSigningStatus.Unchanged, $"Подпись уже совпадает · страниц с раскроем: {edit.Pages}", edit.Pages), bytes)); continue; }
                        var savedPdf = await commit.SaveAsync(path, bytes, edit.Bytes, Validate, token);
                        AddResult(Verified(Result(path, savedPdf.NeedsVerification ? MapSigningStatus.NeedsVerification : MapSigningStatus.Signed,
                            savedPdf.Problem ?? $"Подписано страниц: {edit.ChangedPages} из {edit.Pages}", edit.Pages, savedPdf.BackupPath), edit.Bytes));
                        if (savedPdf.NeedsVerification) return results.ToArray();
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (BatchValidationException ex)
                    {
                        var reason = ex.InnerException is WorkflowValidationException or WorkflowConflictException or WorkflowOutcomeUnknownException ?
                            ex.Message : "Не удалось проверить актуальность заявки.";
                        AddResult(Result(path, MapSigningStatus.Failed, reason + "\nПодписание остановлено. Уже подписанные карты сохранены; обновите журнал (F5)."));
                        return results.ToArray();
                    }
                    catch (WordMapUnavailableException ex)
                    {
                        wordUnavailable = ex.Message; found = true;
                        AddResult(Result(path, MapSigningStatus.Failed, ex.Message)); break;
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        found = true;
                        AddResult(Result(path, MapSigningStatus.Failed, Describe(ex)));
                    }
                    finally
                    {
                        if (verified) completed++;
                        details?.Report(new(WorkflowShopStage.Signing, project.Name, Path.GetFileName(path), completed, total,
                            verified ? "Карта проверена" : "Проверка карты остановлена"));
                    }
                }
                if (!found) AddResult(Result(null, MapSigningStatus.Skipped, "Не найдены карты с этим проектом в шапке. Сохраните карты рядом с рабочим файлом."));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { AddResult(Result(null, MapSigningStatus.Failed, Describe(ex))); }
        }
        return results.ToArray();
    }, token);

    private static string Describe(Exception ex) => ex switch
    {
        WorkflowValidationException => ex.Message,
        IOException or UnauthorizedAccessException => "Карта или папка недоступны либо заняты. Закройте файл и повторите подписание.",
        _ => "Не удалось подписать карту. Исходник сохранён; проверьте файл вручную."
    };

    private static async Task<byte[]> ReadAsync(string path, CancellationToken token)
    {
        ShopFileSystem.RequireOrdinaryPath(path);
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        if (input.Length > 50 * 1024 * 1024) throw new WorkflowValidationException("Карта больше 50 МБ. Сохраните карты меньшего размера и обновите подбор.");
        var bytes = new byte[checked((int)input.Length)]; await input.ReadExactlyAsync(bytes, token); return bytes;
    }

}

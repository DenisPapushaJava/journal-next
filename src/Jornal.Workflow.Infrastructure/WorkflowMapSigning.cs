using Jornal.Workflow.Core;

namespace Jornal.Workflow.Infrastructure;

public enum MapSigningStatus { Signed, Unchanged, Skipped, Failed, NeedsVerification }
public sealed record MapSigningResult(Guid ProjectId, string ProjectName, string? FilePath, MapSigningStatus Status, int Pages, string Message, string? BackupPath = null)
{
    public bool NeedsAttention => Status is MapSigningStatus.Skipped or MapSigningStatus.Failed or MapSigningStatus.NeedsVerification;
    public bool MayHaveChanged => Status is MapSigningStatus.Signed or MapSigningStatus.NeedsVerification;
    public string Details => ProjectName + (FilePath is null ? "" : " · " + Path.GetFileName(FilePath)) + ": " + Message +
        (Status == MapSigningStatus.NeedsVerification && BackupPath is not null ? "\nПуть резервной копии: " + BackupPath : "");
}

public static class WorkflowMapSigning
{
    internal const string BackupFolder = ".workflow-map-originals";
    public static bool Supports(WorkflowMachine machine) => machine.ImportProfile == "block-html/v1" &&
        WorkflowShopDiscovery.MachineRule(machine) is WorkflowShopDiscovery.Rule.Laser or WorkflowShopDiscovery.Rule.Gas or WorkflowShopDiscovery.Rule.PlasmaManual ||
        machine.ImportProfile == "hans-pdf/v1" && WorkflowShopDiscovery.MachineRule(machine) == WorkflowShopDiscovery.Rule.Hans ||
        machine.ImportProfile == "waterjet-awjc/v1" && WorkflowShopDiscovery.MachineRule(machine) == WorkflowShopDiscovery.Rule.Waterjet;

    public static Task<MapSigningResult[]> SignAsync(WorkflowOrder order, WorkflowWorkspace book, Guid? projectId,
        Func<CancellationToken, Task> validate, CancellationToken token = default, IWorkflowWordMapEditor? word = null) =>
        SignAsync(order, book, projectId, validate, token, word, WorkflowMapCommit.Default);

    private sealed class BatchValidationException(Exception inner) : Exception(inner.Message, inner);

    internal static Task<MapSigningResult[]> SignAsync(WorkflowOrder order, WorkflowWorkspace book, Guid? projectId,
        Func<CancellationToken, Task> validate, CancellationToken token, IWorkflowWordMapEditor? word, WorkflowMapCommit commit) => Task.Run(async () =>
    {
        if (order.IsDeleted || order.HasProjectRename) throw new WorkflowValidationException("Восстановите заявку или завершите переименование проекта.");
        var projects = projectId is { } id ? order.ActiveProjects.Where(p => p.Id == id).ToArray() :
            order.Items.Where(i => i.IsIncluded).OrderBy(i => i.Number).Select(i => order.ActiveProjects.Single(p => p.Id == i.CurrentProjectId)).ToArray();
        if (projects.Length == 0) throw new WorkflowValidationException("Добавьте или включите проект в состав заявки.");
        var results = new List<MapSigningResult>(); string? wordUnavailable = null;
        async Task Validate(CancellationToken cancellation)
        {
            try { await validate(cancellation); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is not OutOfMemoryException) { throw new BatchValidationException(ex); }
        }
        foreach (var project in projects)
        {
            token.ThrowIfCancellationRequested();
            MapSigningResult Result(string? path, MapSigningStatus status, string message, int pages = 0, string? backup = null) =>
                new(project.Id, project.Name, path, status, pages, message, backup);
            var machine = book.Machines.Single(m => m.Id == project.MachineId);
            if (!Supports(machine))
            { results.Add(Result(null, MapSigningStatus.Skipped, "Поддерживаются PDF БЛОК/AWJC и DOC HansLaser через Microsoft Word.")); continue; }
            if (machine.ImportProfile == "hans-pdf/v1" && wordUnavailable is not null)
            { results.Add(Result(null, MapSigningStatus.Failed, wordUnavailable)); continue; }
            try
            {
                var text = WorkflowProjectComment.Format(order, project, book.Employees, book.Customers);
                if (project.NativePath is null || !Path.IsPathFullyQualified(project.NativePath))
                    throw new WorkflowValidationException("Не задан полный путь к рабочему проекту.");
                ShopFileSystem.RequireOrdinaryPath(project.NativePath);
                if (!File.Exists(project.NativePath)) throw new WorkflowValidationException("Рабочий файл проекта не найден.");
                var folder = Path.GetDirectoryName(project.NativePath)!;
                var hans = machine.ImportProfile == "hans-pdf/v1";
                if (hans && Directory.EnumerateFiles(folder).Any(p => Path.GetExtension(p).Equals(".dsp", StringComparison.OrdinalIgnoreCase) &&
                    !Path.GetFileName(p).Equals(Path.GetFileName(project.NativePath), StringComparison.OrdinalIgnoreCase) && new FileInfo(p).Length > 0))
                    throw new WorkflowValidationException("В папке несколько проектов DSP. Проверьте принадлежность DOC-карт перед подписанием.");
                var extension = hans ? ".doc" : ".pdf";
                var files = Directory.EnumerateFiles(folder).Where(p => Path.GetExtension(p).Equals(extension, StringComparison.OrdinalIgnoreCase) && !Path.GetFileName(p).StartsWith("~$", StringComparison.Ordinal))
                    .Order(StringComparer.Ordinal).Take(2001).ToArray();
                if (files.Length > 2000) throw new WorkflowValidationException("В папке слишком много карт. Проверьте путь проекта.");
                var found = false;
                foreach (var path in files)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        var bytes = await ReadAsync(path, token);
                        if (hans)
                        {
                            if (word is null) throw new WordMapUnavailableException("Подписание DOC требует Microsoft Word на Windows. Файл не изменён.");
                            var doc = await word.PrepareAsync(bytes, Path.GetFileName(project.NativePath), text, token);
                            if (doc is null) continue;
                            found = true;
                            if (doc.Bytes is null) results.Add(Result(path, MapSigningStatus.Unchanged, "Подпись уже совпадает", doc.Pages));
                            else
                            {
                                var saved = await commit.SaveAsync(path, bytes, doc.Bytes, Validate, token);
                                results.Add(Result(path, saved.NeedsVerification ? MapSigningStatus.NeedsVerification : MapSigningStatus.Signed,
                                    saved.Problem ?? $"Подписано карт: {doc.ChangedCards}", doc.Pages, saved.BackupPath));
                                if (saved.NeedsVerification) return results.ToArray();
                            }
                            continue;
                        }
                        var edit = WorkflowMapPdf.Prepare(bytes, project, WorkflowShopDiscovery.MachineRule(machine), text, token);
                        if (edit is null) continue;
                        found = true;
                        if (edit.Bytes is null)
                        { results.Add(Result(path, MapSigningStatus.Unchanged, $"Подпись уже совпадает · страниц с раскроем: {edit.Pages}", edit.Pages)); continue; }
                        var savedPdf = await commit.SaveAsync(path, bytes, edit.Bytes, Validate, token);
                        results.Add(Result(path, savedPdf.NeedsVerification ? MapSigningStatus.NeedsVerification : MapSigningStatus.Signed,
                            savedPdf.Problem ?? $"Подписано страниц: {edit.ChangedPages} из {edit.Pages}", edit.Pages, savedPdf.BackupPath));
                        if (savedPdf.NeedsVerification) return results.ToArray();
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (BatchValidationException ex)
                    {
                        var reason = ex.InnerException is WorkflowValidationException or WorkflowConflictException or WorkflowOutcomeUnknownException ?
                            ex.Message : "Не удалось проверить актуальность заявки.";
                        results.Add(Result(path, MapSigningStatus.Failed, reason + "\nПодписание остановлено. Уже подписанные карты сохранены; обновите журнал (F5)."));
                        return results.ToArray();
                    }
                    catch (WordMapUnavailableException ex)
                    {
                        wordUnavailable = ex.Message; found = true;
                        results.Add(Result(path, MapSigningStatus.Failed, ex.Message)); break;
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        found = true;
                        results.Add(Result(path, MapSigningStatus.Failed, Describe(ex)));
                    }
                }
                if (!found) results.Add(Result(null, MapSigningStatus.Skipped, "Не найдены карты с этим проектом в шапке. Сохраните карты рядом с рабочим файлом."));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { results.Add(Result(null, MapSigningStatus.Failed, Describe(ex))); }
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
        if (input.Length > 50 * 1024 * 1024) throw new WorkflowValidationException("Карта больше 50 МБ. Подпишите её вручную.");
        var bytes = new byte[checked((int)input.Length)]; await input.ReadExactlyAsync(bytes, token); return bytes;
    }

}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jornal.Workflow.Core;

namespace Jornal.Workflow.Infrastructure;

public sealed record ShopTransferResult(Guid ProjectId, bool Success, bool Unchanged, string Message, string? DestinationRevision);
public sealed record ShopDestinationInspection(string? Revision, ShopTree? Contents);

public static class WorkflowShopTransfer
{
    private const string ReceiptName = ".journal-next-task.json";
    private sealed record Receipt(int Schema, string SourceId, Guid OrderId, Guid ProjectId, ShopTree Contents);
    private sealed record Target(string Revision, Receipt Receipt);

    public static async Task<string?> DestinationRevisionAsync(ShopProjectFiles project, string sourceId, Guid orderId, CancellationToken token = default) =>
        (await ReadTargetAsync(project, sourceId, orderId, token))?.Revision;

    public static async Task<ShopDestinationInspection> InspectDestinationAsync(ShopProjectFiles project, string sourceId, Guid orderId, CancellationToken token = default)
    {
        var target = await ReadTargetAsync(project, sourceId, orderId, token);
        return new(target?.Revision, target?.Receipt.Contents);
    }
    public static bool MatchesDestination(ShopTransferSelection selection, ShopTree contents) => ShopFileSystem.SameTree(DesiredTree(selection), contents);
    public static async Task<bool> HasOwnedDestinationAsync(ShopProjectFiles project, string sourceId, Guid orderId, CancellationToken token = default) =>
        await ReadReceiptAsync(project, sourceId, orderId, token) is not null;

    private static async Task<Target?> ReadTargetAsync(ShopProjectFiles project, string sourceId, Guid orderId, CancellationToken token)
    {
        var target = await ReadReceiptAsync(project, sourceId, orderId, token);
        if (target is null) return null;
        var actual = await ShopFileSystem.TreeAsync(project.Destination, token, ReceiptName);
        if (!ShopFileSystem.SameTree(target.Receipt.Contents, actual))
            throw new WorkflowValidationException("В готовой папке файлы добавлены или изменены вне программы. Разберите их вручную; автоматическая замена остановлена.");
        return target;
    }

    private static async Task<Target?> ReadReceiptAsync(ShopProjectFiles project, string sourceId, Guid orderId, CancellationToken token)
    {
        ShopFileSystem.RequireOrdinaryPath(project.Destination);
        if (File.Exists(project.Destination)) throw new WorkflowValidationException("По пути задания уже существует файл.");
        if (!Directory.Exists(project.Destination)) return null;
        var receiptPath = Path.Combine(project.Destination, ReceiptName);
        ShopFileSystem.RequireOrdinaryPath(receiptPath);
        if (!File.Exists(receiptPath))
            throw new WorkflowValidationException("Папка задания уже существует без подтверждения программы «Заявки». Выберите другое расположение или разберите её вручную; файлы не заменены.");
        byte[] bytes;
        // Passive availability reads must not lock a folder against publication/cleanup on Windows.
        await using (var stream = new FileStream(receiptPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 4096, FileOptions.Asynchronous))
        {
            if (stream.Length > 8 * 1024 * 1024) throw new WorkflowValidationException("Служебная запись задания слишком велика. Существующие файлы не заменены.");
            bytes = new byte[checked((int)stream.Length)];
            await stream.ReadExactlyAsync(bytes, token);
        }
        Receipt receipt;
        try { receipt = JsonSerializer.Deserialize<Receipt>(bytes) ?? throw new JsonException(); }
        catch (JsonException) { throw new WorkflowValidationException("Служебная запись задания повреждена. Существующие файлы не заменены."); }
        if (receipt.Schema != 1 || receipt.SourceId != sourceId || receipt.OrderId != orderId || receipt.ProjectId != project.Project.Id ||
            receipt.Contents?.Files is null || receipt.Contents.Directories is null || receipt.Contents.Files.Any(f => f is null) || receipt.Contents.Directories.Any(d => d is null))
            throw new WorkflowValidationException("Эта папка относится к другому проекту или хранилищу. Существующие файлы не заменены.");
        return new(Convert.ToHexString(SHA256.HashData(bytes)), receipt);
    }

    public static Task<ShopTransferResult[]> CopyAsync(string root, string sourceId, Guid orderId, ShopTransferSelection[] selections,
        bool allowReplacement, Func<CancellationToken, Task> validateOrder, IProgress<string>? progress = null, CancellationToken token = default) => Task.Run(async () =>
    {
        root = WorkflowShopFolderPaths.NormalizeRoot(root) ?? throw new WorkflowValidationException("Выберите папку заданий в цех.");
        token.ThrowIfCancellationRequested();
        ValidateSelections(root, selections);
        await validateOrder(token);
        var results = new List<ShopTransferResult>();
        // Discover known problems across the whole order before creating any output folders.
        foreach (var selection in selections)
        {
            token.ThrowIfCancellationRequested(); progress?.Report("Проверка: " + selection.Project.Project.Name);
            try
            {
                var target = await ReadTargetAsync(selection.Project, sourceId, orderId, token);
                if (target?.Revision != selection.DestinationRevision) throw new WorkflowValidationException("Папка задания изменилась после подбора. Обновите подбор.");
                if (target is not null && !MatchesDestination(selection, target.Receipt.Contents) && !allowReplacement)
                    throw new WorkflowValidationException("Комплект уже передавался и теперь изменился. Подтвердите замену после проверки файлов.");
                await VerifySourcesAsync(selection, token);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WorkflowValidationException)
            { return new[] { Failure(selection, ex) }; }
        }
        foreach (var selection in selections)
        {
            if (token.IsCancellationRequested) break;
            progress?.Report("Подготовка: " + selection.Project.Project.Name);
            try { results.Add(await CopyProjectAsync(root, sourceId, orderId, selection, allowReplacement, validateOrder, token)); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WorkflowValidationException)
            {
                results.Add(Failure(selection, ex));
                // A failed project does not silently send the rest of the order.
                break;
            }
        }
        return results.ToArray();
    }, CancellationToken.None);

    private static ShopTransferResult Failure(ShopTransferSelection selection, Exception ex) => new(selection.Project.Project.Id, false, false,
        ex is WorkflowValidationException ? ex.Message : "Не удалось подготовить комплект. Закройте открытые файлы задания и проверьте доступ к папкам. Обновите подбор и повторите.", selection.DestinationRevision);

    private static void ValidateSelections(string root, ShopTransferSelection[] selections)
    {
        if (selections.Length == 0 || selections.Select(s => s.Project.Destination).Distinct(ShopFileSystem.PathComparer).Count() != selections.Length)
            throw new WorkflowValidationException("Проверьте состав задания.");
        foreach (var s in selections)
        {
            if (s.Project.Problem is not null) throw new WorkflowValidationException(s.Project.Problem);
            if ((s.Project.NeedsReview || !s.Files.OrderBy(f => f.Name).SequenceEqual(s.Project.Files.Where(f => f.Suggested).OrderBy(f => f.Name))) && !s.Reviewed)
                throw new WorkflowValidationException("Подтвердите состав каждого спорного проекта.");
            if (s.Files.FirstOrDefault(f => !f.IsReadable) is { } unavailable)
                throw new WorkflowValidationException($"Файл «{unavailable.Name}» недоступен. Закройте его и обновите подбор или исключите из комплекта.");
            if (!s.Files.Any(f => f.Kind == ShopFileKind.Program) || !s.Files.Any(f => f.Kind == ShopFileKind.Map) || s.Files.Any(f => f.Length == 0))
                throw new WorkflowValidationException("Для каждого проекта выберите непустые УП и карту раскроя.");
            if (s.Files.Any(f => !s.Project.Files.Contains(f)) || s.Files.Select(f => f.Name).Distinct(ShopFileSystem.PathComparer).Count() != s.Files.Length)
                throw new WorkflowValidationException("Состав изменился. Повторите подбор.");
            var sources = selections.Select(p => Path.GetDirectoryName(p.Project.Project.NativePath!)!);
            if (!ShopFileSystem.Under(s.Project.Destination, root) || ShopFileSystem.PathComparer.Equals(s.Project.Destination, root) ||
                sources.Any(source => ShopFileSystem.Under(s.Project.Destination, source) || ShopFileSystem.Under(source, s.Project.Destination)))
                throw new WorkflowValidationException("Рабочая папка и папка задания пересекаются. Проверьте путь в настройках.");
        }
    }

    private static async Task<ShopTransferResult> CopyProjectAsync(string root, string sourceId, Guid orderId, ShopTransferSelection selection,
        bool allowReplacement, Func<CancellationToken, Task> validateOrder, CancellationToken token)
    {
        var project = selection.Project; var destination = project.Destination; var parent = Path.GetDirectoryName(destination)!;
        ShopFileSystem.RequireOrdinaryPath(destination);
        var locks = Path.Combine(root, ".journal-next-transfer-locks");
        ShopFileSystem.RequireOrdinaryPath(locks); Directory.CreateDirectory(locks); WorkflowServiceFiles.TryHide(locks);
        var lockName = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetRelativePath(root, destination).Replace('\\', '/').ToUpperInvariant()))) + ".lock";
        var lockPath = Path.Combine(locks, lockName); ShopFileSystem.RequireOrdinaryPath(lockPath);
        // Keep the lock file: deleting it after release can split concurrent clients across different file handles.
        await using var lease = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var target = await ReadTargetAsync(project, sourceId, orderId, token);
        if (target?.Revision != selection.DestinationRevision) throw new WorkflowValidationException("Папка задания изменилась после предпросмотра. Обновите подбор.");
        var tree = DesiredTree(selection);
        await VerifySourcesAsync(selection, token);
        if (target is not null && ShopFileSystem.SameTree(target.Receipt.Contents, tree))
            return new(project.Project.Id, true, true, "Комплект уже подготовлен, изменений нет", target.Revision);
        if (target is not null && !allowReplacement)
            throw new WorkflowValidationException("Комплект изменился. Разрешите замену ранее подготовленных комплектов и повторите.");
        ShopFileSystem.RequireOrdinaryPath(parent); Directory.CreateDirectory(parent); ShopFileSystem.RequireOrdinaryPath(parent);
        var stage = Path.Combine(parent, ".journal-next-new-" + Guid.NewGuid().ToString("N"));
        var backup = Path.Combine(parent, ".journal-next-replaced-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            foreach (var directory in tree.Directories) Directory.CreateDirectory(Path.Combine(stage, directory));
            foreach (var file in tree.Files)
            {
                token.ThrowIfCancellationRequested();
                var source = file.RelativePath.StartsWith("ГИБКА" + Path.DirectorySeparatorChar, StringComparison.Ordinal) ?
                    Path.Combine(Path.GetDirectoryName(project.Project.NativePath!)!, file.RelativePath) : selection.Files.Single(f => f.Name == file.RelativePath).SourcePath;
                var output = Path.Combine(stage, file.RelativePath);
                await CopyFileAsync(source, output, file, token);
            }
            var receipt = JsonSerializer.SerializeToUtf8Bytes(new Receipt(1, sourceId, orderId, project.Project.Id, tree));
            if (receipt.Length > 8 * 1024 * 1024) throw new WorkflowValidationException("Состав комплекта слишком большой для служебной записи. Готовая папка не изменена.");
            await File.WriteAllBytesAsync(Path.Combine(stage, ReceiptName), receipt, token);
            WorkflowServiceFiles.TryHide(Path.Combine(stage, ReceiptName));
            await VerifySourcesAsync(selection, token);
            if (!ShopFileSystem.SameTree(tree, await ShopFileSystem.TreeAsync(stage, token, ReceiptName)))
                throw new WorkflowValidationException("Не удалось проверить скопированные файлы. Готовая папка не изменена.");
            await validateOrder(token);
            if ((await ReadTargetAsync(project, sourceId, orderId, token))?.Revision != target?.Revision)
                throw new WorkflowValidationException("Папка задания изменилась во время подготовки. Обновите подбор.");
            ShopFileSystem.RequireOrdinaryPath(destination); token.ThrowIfCancellationRequested();
            // No cancellation between moves: finish promotion or restore the previous complete folder.
            if (target is not null) Directory.Move(destination, backup);
            try { Directory.Move(stage, destination); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                try { if (target is not null && !Directory.Exists(destination)) Directory.Move(backup, destination); }
                catch (Exception restore) when (restore is IOException or UnauthorizedAccessException)
                { throw new WorkflowValidationException("Не удалось вернуть прежнюю папку задания. Она сохранена здесь: " + backup + ". Проверьте папки вручную перед повтором."); }
                throw;
            }
            string? warning = null;
            if (target is not null)
            {
                try
                {
                    if (!ShopFileSystem.SameTree(target.Receipt.Contents, await ShopFileSystem.TreeAsync(backup, CancellationToken.None, ReceiptName)))
                        warning = "Предыдущая копия изменилась и сохранена: " + backup;
                    else Directory.Delete(backup, true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WorkflowValidationException)
                { warning = "Предыдущая копия сохранена: " + backup; }
                if (warning is not null) WorkflowServiceFiles.TryHide(backup);
            }
            return new(project.Project.Id, true, false, warning is null ? "Файлы подготовлены" : "Файлы подготовлены. " + warning, Convert.ToHexString(SHA256.HashData(receipt)));
        }
        finally
        {
            if (Directory.Exists(stage))
                try { Directory.Delete(stage, true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Own abandoned staging folder; never a completed destination. */ }
        }
    }

    public static ShopTree DesiredTree(ShopTransferSelection selection)
    {
        var files = selection.Files.Select(f => new ShopTreeFile(f.Name, f.Length, f.Sha256)).ToList(); var directories = new List<string>();
        if (selection.Project.Bending is { } bending)
        {
            directories.Add("ГИБКА"); directories.AddRange(bending.Directories.Select(d => Path.Combine("ГИБКА", d)));
            files.AddRange(bending.Files.Select(f => f with { RelativePath = Path.Combine("ГИБКА", f.RelativePath) }));
        }
        return new(files.ToArray(), directories.ToArray());
    }

    private static async Task VerifySourcesAsync(ShopTransferSelection selection, CancellationToken token)
    {
        var folder = Path.GetDirectoryName(selection.Project.Project.NativePath!)!;
        if (selection.Project.CandidateExtensions is { } extensions && !ShopFileSystem.CandidateNames(folder, extensions).SequenceEqual(selection.Project.Files.Select(f => f.Name).Order(StringComparer.Ordinal)) ||
            selection.Project.NativeNames is { } names && !ShopFileSystem.Names(folder, Path.GetExtension(selection.Project.Project.NativePath!)).SequenceEqual(names))
            throw new WorkflowValidationException("Состав рабочей папки изменился после подбора. Обновите подбор перед копированием.");
        // Readable excluded candidates also support the automatic decision. Unreadable files
        // require explicit manual exclusion; their names remain part of the folder snapshot.
        foreach (var file in selection.Project.Files.Where(f => f.IsReadable))
        {
            var current = await ShopFileSystem.FingerprintAsync(file.SourcePath, token);
            if (current.Hash != file.Sha256 || current.Length != file.Length)
                throw new WorkflowValidationException($"Изменился файл «{file.Name}». Обновите подбор перед копированием.");
        }
        if (selection.Project.Bending is { } bending && !ShopFileSystem.SameTree(bending,
            await ShopFileSystem.TreeAsync(Path.Combine(Path.GetDirectoryName(selection.Project.Project.NativePath!)!, "ГИБКА"), token)))
            throw new WorkflowValidationException("Содержимое ГИБКИ изменилось. Обновите подбор перед копированием.");
    }

    private static async Task CopyFileAsync(string source, string output, ShopTreeFile expected, CancellationToken token)
    {
        ShopFileSystem.RequireOrdinaryPath(source);
        await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (var target = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
        { await input.CopyToAsync(target, token); await target.FlushAsync(token); }
        var actual = await ShopFileSystem.FingerprintAsync(output, token);
        if (actual.Hash != expected.Sha256 || actual.Length != expected.Length)
            throw new WorkflowValidationException($"Файл «{expected.RelativePath}» изменился при копировании. Обновите подбор.");
    }
}

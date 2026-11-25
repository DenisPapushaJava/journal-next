using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jornal.Workflow.Core;

namespace Jornal.Workflow.Infrastructure;

public sealed record ShopTransferResult(Guid ProjectId, bool Success, bool Unchanged, string Message, string? DestinationRevision)
{
    public string? BundleKey { get; init; }
    public int PdfExportVersion { get; init; }
}
public sealed record ShopDestinationInspection(string? Revision, ShopTree? Contents)
{
    public string? BundleKey { get; init; }
    public string? InputKey { get; init; }
    public int PdfExportVersion { get; init; }
}

public static partial class WorkflowShopTransfer
{
    private const string ReceiptName = ".journal-next-task.json";
    private sealed record Receipt(int Schema, string SourceId, Guid OrderId, Guid ProjectId, ShopTree Contents)
    {
        public string? BundleKey { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? InputKey { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int PdfExportVersion { get; init; }
    }
    private sealed record Target(string Revision, Receipt Receipt);

    public static async Task<string?> DestinationRevisionAsync(ShopProjectFiles project, string sourceId, Guid orderId, CancellationToken token = default) =>
        (await ReadTargetAsync(project, sourceId, orderId, token))?.Revision;

    public sealed class DestinationVerification : IDisposable
    {
        private readonly ShopTransferSelection _selection;
        private readonly string _sourceId;
        private readonly Guid _orderId;
        private readonly List<FileStream>? _held = OperatingSystem.IsWindows() ? [] : null;
        private ShopTree? _tree;
        internal DestinationVerification(ShopTransferSelection selection, string sourceId, Guid orderId)
        {
            _selection = selection;
            _sourceId = sourceId;
            _orderId = orderId;
        }

        internal async Task VerifyAsync(CancellationToken token)
        {
            var target = await ReadTargetAsync(_selection.Project, _sourceId, _orderId, token, held: _held);
            if (target is null || target.Revision != _selection.DestinationRevision) throw DestinationChanged(_selection);
            _tree = target.Receipt.Contents;
        }

        public async Task RecheckAsync(CancellationToken token)
        {
            if (_held is null)
            {
                // Unix file sharing is advisory. Check the bytes again after the round trips to the server.
                await VerifyAsync(token);
                return;
            }
            var entries = ShopFileSystem.TreeEntries(_selection.Project.Destination, token, ReceiptName)
                .Select(entry => (entry.Relative, entry.Directory)).ToHashSet();
            if (_tree is null || !entries.SetEquals(_tree.Files.Select(f => (f.RelativePath, false))
                .Concat(_tree.Directories.Select(d => (d, true)))))
                throw DestinationChanged(_selection);
        }

        public void Dispose()
        {
            if (_held is null) return;
            foreach (var file in _held) file.Dispose();
            _held.Clear();
        }
    }

    public static async Task<DestinationVerification> VerifyDestinationForRecordingAsync(ShopTransferSelection selection, string sourceId,
        Guid orderId, CancellationToken token)
    {
        var verification = new DestinationVerification(selection, sourceId, orderId);
        try
        {
            await verification.VerifyAsync(token);
            return verification;
        }
        catch { verification.Dispose(); throw; }
    }

    private static WorkflowValidationException DestinationChanged(ShopTransferSelection selection) =>
        new($"Папка задания для «{selection.Project.Project.Name}» изменилась. Обновите подбор перед записью отметки.");

    public static async Task<string?> PublicationRevisionAsync(ShopTransferSelection selection, string sourceId, Guid orderId, CancellationToken token)
    {
        if (selection.ArchivedFolder is not { } archive || selection.DestinationRevision is not null)
            return selection.DestinationRevision;
        var destination = selection.Project.Destination;
        ShopFileSystem.RequireOrdinaryPath(destination);
        if (Directory.Exists(destination) || File.Exists(destination) || ShopFileSystem.Under(archive, destination) ||
            ShopFileSystem.Under(destination, archive))
            throw new WorkflowValidationException("Укажите папку перенесённого комплекта. Путь нового задания должен быть свободен.");
        return (await ReadTargetAsync(selection.Project with { Destination = archive }, sourceId, orderId, token))?.Revision
            ?? throw new WorkflowValidationException("Архивная папка недоступна. Выберите папку прежнего задания со служебной записью.");
    }

    public static async Task<ShopDestinationInspection> InspectDestinationAsync(ShopProjectFiles project, string sourceId, Guid orderId, CancellationToken token = default,
        IProgress<WorkflowShopProgress>? details = null)
    {
        var target = await ReadTargetAsync(project, sourceId, orderId, token, details);
        return new(target?.Revision, target?.Receipt.Contents)
        {
            BundleKey = target?.Receipt.BundleKey,
            InputKey = target?.Receipt.InputKey,
            PdfExportVersion = target?.Receipt.PdfExportVersion ?? 0
        };
    }
    public static bool MatchesDestination(ShopTransferSelection selection, ShopTree contents, string? receiptBundleKey = null,
        int pdfExportVersion = 0, string? inputKey = null) =>
        selection.InputKey is not null && selection.InputKey == inputKey ||
        (BundleKey(selection, out _) is { } key
            ? key == receiptBundleKey && (selection.MapBundle != ShopMapBundle.Pdf || pdfExportVersion >= WorkflowMapBundlePdf.ExportVersion)
            : receiptBundleKey is null && ShopFileSystem.SameTree(DesiredTree(selection), contents));

    private static bool MatchesDestination(ShopTransferSelection selection, Receipt receipt) =>
        MatchesDestination(selection, receipt.Contents, receipt.BundleKey, receipt.PdfExportVersion, receipt.InputKey);

    public static string InputKey(ShopTransferSelection original, string caption)
    {
        var tree = WithSeparator(DesiredTree(original), '/');
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            Caption = caption,
            original.MapBundle,
            PdfExport = original.MapBundle == ShopMapBundle.Pdf ? WorkflowMapBundlePdf.ExportVersion : 0,
            Files = tree.Files.OrderBy(f => f.RelativePath, StringComparer.Ordinal),
            Directories = tree.Directories.Order(StringComparer.Ordinal)
        })));
    }

    public static string PublicationPath(string root, string destination) => Path.GetRelativePath(root, destination).Replace('\\', '/');
    public static async Task<bool> HasOwnedDestinationAsync(ShopProjectFiles project, string sourceId, Guid orderId, CancellationToken token = default) =>
        await ReadReceiptAsync(project, sourceId, orderId, token) is not null;

    private static async Task<Target?> ReadTargetAsync(ShopProjectFiles project, string sourceId, Guid orderId, CancellationToken token,
        IProgress<WorkflowShopProgress>? details = null, WorkflowShopStage stage = WorkflowShopStage.Checking, List<FileStream>? held = null)
    {
        var target = await ReadReceiptAsync(project, sourceId, orderId, token, held);
        if (target is null) return null;
        var actual = await ShopFileSystem.TreeAsync(project.Destination, token, ReceiptName,
            name => details?.Report(new(stage, project.Project.Name, name, Activity: "Проверяем ранее переданные файлы")), held: held);
        if (!ShopFileSystem.SameTree(target.Receipt.Contents, actual))
            throw new WorkflowValidationException("В готовой папке файлы добавлены или изменены вне программы. Разберите их вручную; автоматическая замена остановлена.");
        return target;
    }

    private static async Task<Target?> ReadReceiptAsync(ShopProjectFiles project, string sourceId, Guid orderId, CancellationToken token,
        List<FileStream>? held = null)
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
        var stream = new FileStream(receiptPath, FileMode.Open, FileAccess.Read, held is null ? FileShare.Read | FileShare.Delete : FileShare.Read,
            held is null ? 4096 : 1, FileOptions.Asynchronous);
        held?.Add(stream);
        try
        {
            if (stream.Length > 8 * 1024 * 1024) throw new WorkflowValidationException("Служебная запись задания слишком велика. Существующие файлы не заменены.");
            bytes = new byte[checked((int)stream.Length)];
            await stream.ReadExactlyAsync(bytes, token);
        }
        finally { if (held is null) await stream.DisposeAsync(); }
        Receipt receipt;
        try { receipt = JsonSerializer.Deserialize<Receipt>(bytes) ?? throw new JsonException(); }
        catch (JsonException) { throw new WorkflowValidationException("Служебная запись задания повреждена. Существующие файлы не заменены."); }
        if (receipt.Schema != 1 || receipt.SourceId != sourceId || receipt.OrderId != orderId || receipt.ProjectId != project.Project.Id ||
            receipt.Contents?.Files is null || receipt.Contents.Directories is null ||
            receipt.Contents.Files.Any(f => string.IsNullOrEmpty(f?.RelativePath)) || receipt.Contents.Directories.Any(string.IsNullOrEmpty))
            throw new WorkflowValidationException("Эта папка относится к другому проекту или хранилищу. Существующие файлы не заменены.");
        return new(Convert.ToHexString(SHA256.HashData(bytes)),
            receipt with { Contents = WithSeparator(receipt.Contents, Path.DirectorySeparatorChar) });
    }

    private static ShopTree WithSeparator(ShopTree tree, char separator)
    {
        string Normalize(string path) => path.Replace('\\', separator).Replace('/', separator);
        return new(tree.Files.Select(file => file with { RelativePath = Normalize(file.RelativePath) }).ToArray(),
            tree.Directories.Select(Normalize).ToArray());
    }

    public static Task<ShopTransferResult[]> CopyAsync(string root, string sourceId, Guid orderId, ShopTransferSelection[] selections,
        bool allowReplacement, Func<CancellationToken, Task> validateOrder, IProgress<string>? progress = null, CancellationToken token = default,
        IWorkflowWordMapEditor? word = null, IProgress<WorkflowShopProgress>? details = null,
        Func<ShopTransferSelection, CancellationToken, Task>? validateProjectSource = null,
        IWorkflowShopPublication? publication = null) => Task.Run(async () =>
    {
        root = WorkflowShopFolderPaths.NormalizeRoot(root) ?? throw new WorkflowValidationException("Выберите папку заданий в цех.");
        var failures = await PreflightCoreAsync(root, sourceId, orderId, selections, allowReplacement, validateOrder, progress, token, word, details, WorkflowShopStage.Copying);
        if (failures.Length > 0) return failures;
        var results = new List<ShopTransferResult>();
        var completed = 0;
        var total = selections.Sum(s => s.Files.Length + (s.Project.Bending?.Files.Length ?? 0));
        foreach (var selection in selections)
        {
            if (token.IsCancellationRequested) break;
            progress?.Report("Подготовка: " + selection.Project.Project.Name);
            try
            {
                var result = await CopyProjectAsync(root, sourceId, orderId, selection, allowReplacement, validateOrder, token, word, details,
                    completed, total, validateProjectSource, publication);
                results.Add(result);
                if (result.Success) completed += selection.Files.Length + (selection.Project.Bending?.Files.Length ?? 0);
                details?.Report(new(WorkflowShopStage.Copying, selection.Project.Project.Name, Completed: completed, Total: total,
                    Activity: result.Unchanged ? "Комплект уже актуален" : "Комплект проекта подготовлен"));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WorkflowValidationException or WorkflowConflictException)
            {
                results.Add(Failure(selection, ex));
                // A failed project does not silently send the rest of the order.
                break;
            }
        }
        return results.ToArray();
    }, CancellationToken.None);

    public static Task<ShopTransferResult[]> PreflightAsync(string root, string sourceId, Guid orderId, ShopTransferSelection[] selections,
        bool allowReplacement, Func<CancellationToken, Task> validateOrder, IProgress<string>? progress = null, CancellationToken token = default,
        IWorkflowWordMapEditor? word = null, IProgress<WorkflowShopProgress>? details = null) => Task.Run(() =>
        PreflightCoreAsync(WorkflowShopFolderPaths.NormalizeRoot(root) ?? throw new WorkflowValidationException("Выберите папку заданий в цех."),
            sourceId, orderId, selections, allowReplacement, validateOrder, progress, token, word, details), token);

    private static async Task<ShopTransferResult[]> PreflightCoreAsync(string root, string sourceId, Guid orderId, ShopTransferSelection[] selections,
        bool allowReplacement, Func<CancellationToken, Task> validateOrder, IProgress<string>? progress, CancellationToken token, IWorkflowWordMapEditor? word,
        IProgress<WorkflowShopProgress>? details, WorkflowShopStage stage = WorkflowShopStage.Checking)
    {
        token.ThrowIfCancellationRequested();
        ValidateSelections(root, selections);
        await validateOrder(token);
        // Discover known problems across the whole order before creating any output folders.
        foreach (var selection in selections)
        {
            token.ThrowIfCancellationRequested(); progress?.Report("Проверка: " + selection.Project.Project.Name);
            details?.Report(new(stage, selection.Project.Project.Name, Activity: "Проверяем комплект перед подготовкой"));
            try
            {
                var target = await ReadTargetAsync(selection.Project, sourceId, orderId, token, details, stage);
                if (target?.Revision != selection.DestinationRevision) throw new WorkflowValidationException("Папка задания изменилась после подбора. Обновите подбор.");
                if (target is not null && !MatchesDestination(selection, target.Receipt) && !allowReplacement)
                    throw new WorkflowValidationException("Комплект уже передавался и теперь изменился. Подтвердите замену после проверки файлов.");
                await VerifySourcesAsync(selection, token, word, details, stage);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WorkflowValidationException)
            { return new[] { Failure(selection, ex) }; }
        }
        return [];
    }

    private static ShopTransferResult Failure(ShopTransferSelection selection, Exception ex) => new(selection.Project.Project.Id, false, false,
        $"Проект «{selection.Project.Project.Name}»: " + (ex is WorkflowValidationException or WorkflowConflictException ? ex.Message :
        "Не удалось подготовить файлы. Проверьте доступ к рабочей папке и папке цеха."), selection.DestinationRevision);

    private static void ValidateSelections(string root, ShopTransferSelection[] selections)
    {
        if (selections.Length == 0 || selections.Select(s => s.Project.Destination).Distinct(ShopFileSystem.PathComparer).Count() != selections.Length)
            throw new WorkflowValidationException("Проверьте состав задания.");
        foreach (var s in selections)
        {
            if (s.Project.Problem is not null) throw new WorkflowValidationException(s.Project.Problem);
            if ((s.Project.NeedsReview || !s.Files.OrderBy(f => f.Name).SequenceEqual(s.Project.Files.Where(f => f.Suggested).OrderBy(f => f.Name))) && !s.Reviewed)
                throw new WorkflowValidationException($"Проект «{s.Project.Project.Name}»: " +
                    (s.Project.ReviewReason ?? "Выбор файлов изменён.") + " Проверьте отмеченные УП и карты и подтвердите состав этого проекта.");
            if (s.Files.FirstOrDefault(f => !f.IsReadable) is { } unavailable)
                throw new WorkflowValidationException($"Файл «{unavailable.Name}» недоступен. Закройте его и обновите подбор или исключите из комплекта.");
            if (!s.Files.Any(f => f.Kind == ShopFileKind.Program) || !s.Files.Any(f => f.Kind == ShopFileKind.Map) || s.Files.Any(f => f.Length == 0))
                throw new WorkflowValidationException($"Проект «{s.Project.Project.Name}»: выберите непустые УП и карту раскроя.");
            if (s.Files.Any(f => !s.Project.Files.Contains(f)) || s.Files.Select(f => f.Name).Distinct(ShopFileSystem.PathComparer).Count() != s.Files.Length)
                throw new WorkflowValidationException("Состав изменился. Повторите подбор.");
            var sources = selections.Select(p => Path.GetDirectoryName(p.Project.Project.NativePath!)!);
            if (!ShopFileSystem.Under(s.Project.Destination, root) || ShopFileSystem.PathComparer.Equals(s.Project.Destination, root) ||
                sources.Any(source => ShopFileSystem.Under(s.Project.Destination, source) || ShopFileSystem.Under(source, s.Project.Destination)))
                throw new WorkflowValidationException("Рабочая папка и папка задания пересекаются. Проверьте путь в настройках.");
        }
    }

    private static async Task<ShopTransferResult> CopyProjectAsync(string root, string sourceId, Guid orderId, ShopTransferSelection selection,
        bool allowReplacement, Func<CancellationToken, Task> validateOrder, CancellationToken token, IWorkflowWordMapEditor? word,
        IProgress<WorkflowShopProgress>? details, int completed, int total, Func<ShopTransferSelection, CancellationToken, Task>? validateProjectSource,
        IWorkflowShopPublication? publication)
    {
        var project = selection.Project; var destination = project.Destination; var parent = Path.GetDirectoryName(destination)!;
        ShopFileSystem.RequireOrdinaryPath(destination);
        var locks = Path.Combine(root, ".journal-next-transfer-locks");
        ShopFileSystem.RequireOrdinaryPath(locks); Directory.CreateDirectory(locks); WorkflowServiceFiles.TryHide(locks);
        var relativePath = PublicationPath(root, destination);
        var lockName = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(relativePath.ToUpperInvariant()))) + ".lock";
        var lockPath = Path.Combine(locks, lockName); ShopFileSystem.RequireOrdinaryPath(lockPath);
        // Keep the lock file: deleting it after release can split concurrent clients across different file handles.
        await using var lease = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var target = await ReadTargetAsync(project, sourceId, orderId, token, details, WorkflowShopStage.Copying);
        if (target is not null) WorkflowServiceFiles.TryHide(Path.Combine(destination, ReceiptName));
        if (target?.Revision != selection.DestinationRevision) throw new WorkflowValidationException("Папка задания изменилась после предпросмотра. Обновите подбор.");
        var bundleKey = BundleKey(selection, out var bundleName);
        await VerifySourcesAsync(selection, token, word, details, WorkflowShopStage.Copying);
        if (target is not null && MatchesDestination(selection, target.Receipt))
        {
            if (validateProjectSource is not null) await validateProjectSource(selection, token);
            if (bundleName is not null)
            {
                var file = target.Receipt.Contents.Files.SingleOrDefault(f => f.RelativePath == bundleName)
                    ?? throw new WorkflowValidationException("В ранее переданном комплекте отсутствует объединённая карта. Проверьте папку цеха.");
                await SaveProjectBundleAsync(project.Project, Path.Combine(destination, file.RelativePath), file, token);
            }
            return new(project.Project.Id, true, true, "Комплект уже подготовлен, изменений нет", target.Revision)
                { BundleKey = bundleKey, PdfExportVersion = target.Receipt.PdfExportVersion };
        }
        if (target is not null && !allowReplacement)
            throw new WorkflowValidationException("Комплект изменился. Разрешите замену ранее подготовленных комплектов и повторите.");
        details?.Report(new(WorkflowShopStage.Copying, project.Project.Name, Activity: "Создаём папки комплекта"));
        ShopFileSystem.RequireOrdinaryPath(parent); Directory.CreateDirectory(parent); ShopFileSystem.RequireOrdinaryPath(parent);
        var staging = Path.Combine(parent, ".journal-next-new-" + Guid.NewGuid().ToString("N"));
        var stage = Path.Combine(staging, "task");
        var backup = Path.Combine(staging, "previous");
        Directory.CreateDirectory(stage);
        WorkflowServiceFiles.TryHide(staging);
        ShopPublicationChange? reservation = null;
        var promotionStarted = false;
        try
        {
            var tree = DesiredTree(selection);
            ShopTreeFile? bundle = null;
            var bundledMapCount = 0;
            if (bundleName is not null)
            {
                details?.Report(new(WorkflowShopStage.Copying, project.Project.Name, bundleName, Activity: "Готовим файл карт раскроя HansLaser"));
                bundle = await BuildMapBundleAsync(selection.Files.Where(f => f.Kind == ShopFileKind.Map), selection.MapBundle,
                    Path.Combine(stage, bundleName), word, token);
                var mapNames = selection.Files.Where(f => f.Kind == ShopFileKind.Map).Select(f => f.Name).ToHashSet(ShopFileSystem.PathComparer);
                bundledMapCount = mapNames.Count;
                tree = tree with { Files = [.. tree.Files.Where(f => !mapNames.Contains(f.RelativePath)), bundle] };
            }
            foreach (var directory in tree.Directories) Directory.CreateDirectory(Path.Combine(stage, directory));
            foreach (var file in tree.Files)
            {
                if (file == bundle) { completed += bundledMapCount; continue; }
                token.ThrowIfCancellationRequested();
                var source = file.RelativePath.StartsWith("ГИБКА" + Path.DirectorySeparatorChar, StringComparison.Ordinal) ?
                    Path.Combine(Path.GetDirectoryName(project.Project.NativePath!)!, file.RelativePath) : selection.Files.Single(f => f.Name == file.RelativePath).SourcePath;
                var output = Path.Combine(stage, file.RelativePath);
                details?.Report(new(WorkflowShopStage.Copying, project.Project.Name, file.RelativePath, completed, total, "Копируем и проверяем файл"));
                await CopyFileAsync(source, output, file, token);
                completed++;
                details?.Report(new(WorkflowShopStage.Copying, project.Project.Name, file.RelativePath, completed, total, "Файл скопирован и проверен"));
            }
            var pdfExportVersion = bundle is not null && selection.MapBundle == ShopMapBundle.Pdf ? WorkflowMapBundlePdf.ExportVersion : 0;
            var receipt = JsonSerializer.SerializeToUtf8Bytes(new Receipt(1, sourceId, orderId, project.Project.Id, WithSeparator(tree, '\\'))
                { BundleKey = bundleKey, PdfExportVersion = pdfExportVersion, InputKey = selection.InputKey });
            var revision = Convert.ToHexString(SHA256.HashData(receipt));
            if (receipt.Length > 8 * 1024 * 1024) throw new WorkflowValidationException("Состав комплекта слишком большой для служебной записи. Готовая папка не изменена.");
            await File.WriteAllBytesAsync(Path.Combine(stage, ReceiptName), receipt, token);
            WorkflowServiceFiles.TryHide(Path.Combine(stage, ReceiptName));
            details?.Report(new(WorkflowShopStage.Verifying, project.Project.Name, Activity: "Проверяем комплект перед публикацией"));
            await VerifySourcesAsync(selection, token, word, details, WorkflowShopStage.Verifying);
            if (!ShopFileSystem.SameTree(tree, await ShopFileSystem.TreeAsync(stage, token, ReceiptName,
                name => details?.Report(new(WorkflowShopStage.Verifying, project.Project.Name, name, Activity: "Проверяем копию файла")))))
                throw new WorkflowValidationException("Не удалось проверить скопированные файлы. Готовая папка не изменена.");
            await validateOrder(token);
            if (validateProjectSource is not null) await validateProjectSource(selection, token);
            if ((await ReadTargetAsync(project, sourceId, orderId, token, details, WorkflowShopStage.Verifying))?.Revision != target?.Revision)
                throw new WorkflowValidationException("Папка задания изменилась во время подготовки. Обновите подбор.");
            if (bundle is not null)
                await SaveProjectBundleAsync(project.Project, Path.Combine(stage, bundle.RelativePath), bundle, token);
            if (publication is not null)
            {
                var previousRevision = await PublicationRevisionAsync(selection, sourceId, orderId, token);
                var change = new ShopPublicationChange(ShopPublicationStep.Reserve, project.Project.Id, relativePath, previousRevision, revision)
                { ReservationId = publication.SupportsRollback ? Guid.NewGuid() : null };
                await publication.UpdateAsync(change, token);
                if (change.ReservationId is not null) reservation = change;
            }
            ShopFileSystem.RequireOrdinaryPath(destination); token.ThrowIfCancellationRequested();
            // No cancellation between moves: finish promotion or restore the previous complete folder.
            promotionStarted = true;
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
            }
            var message = bundle is null ? "Файлы подготовлены" : "Файлы подготовлены · " + bundle.RelativePath;
            return new(project.Project.Id, true, false, warning is null ? message : message + ". " + warning, revision)
                { BundleKey = bundleKey, PdfExportVersion = pdfExportVersion };
        }
        catch (Exception ex) when (!promotionStarted && reservation is not null && publication is not null &&
            ex is OperationCanceledException or IOException or UnauthorizedAccessException or WorkflowValidationException)
        {
            try
            {
                await publication.UpdateAsync(reservation with { Step = ShopPublicationStep.Rollback }, CancellationToken.None);
            }
            catch (Exception rollback) when (rollback is IOException or UnauthorizedAccessException or WorkflowValidationException or OperationCanceledException)
            {
                throw new WorkflowValidationException("Папка задания ещё не опубликована, но снять резерв не удалось. " +
                    "Проверьте доступ к общей базе и повторите с этого компьютера. " + rollback.Message);
            }
            throw;
        }
        finally
        {
            try
            {
                if (Directory.Exists(stage)) Directory.Delete(stage, true);
                if (!Directory.Exists(backup)) Directory.Delete(staging);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Keep the hidden staging or saved previous task. */ }
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

    private static async Task VerifySourcesAsync(ShopTransferSelection selection, CancellationToken token, IWorkflowWordMapEditor? word,
        IProgress<WorkflowShopProgress>? details = null, WorkflowShopStage stage = WorkflowShopStage.Checking)
    {
        var folder = Path.GetDirectoryName(selection.Project.Project.NativePath!)!;
        var hans = selection.Project.Machine is { } selectedMachine && WorkflowShopDiscovery.MachineRule(selectedMachine) == WorkflowShopDiscovery.Rule.Hans;
        var changed = selection.Project.CandidateExtensions is { } extensions &&
            !ShopFileSystem.CandidateNames(folder, extensions, hans).SequenceEqual(selection.Project.Files.Select(f => f.Name).Order(StringComparer.Ordinal));
        changed |= selection.Project.NativeNames is { } names &&
            !ShopFileSystem.Names(folder, Path.GetExtension(selection.Project.Project.NativePath!)).SequenceEqual(names);
        if (selection.Project.NativeProof is { } proof)
            foreach (var file in proof)
            {
                details?.Report(new(stage, selection.Project.Project.Name, file.RelativePath, Activity: "Проверяем рабочий проект"));
                var now = await ShopFileSystem.FingerprintAsync(Path.Combine(folder, file.RelativePath), token);
                if (now.Length != file.Length || now.Hash != file.Sha256)
                    throw new WorkflowValidationException($"Изменился DSP «{file.RelativePath}», использованный для проверки одинаковых копий. Обновите подбор.");
            }
        // Selected bytes are always strict. A manual selection does not depend on excluded files.
        foreach (var file in selection.Files)
        {
            details?.Report(new(stage, selection.Project.Project.Name, file.Name, Activity: "Проверяем исходный файл"));
            (long Length, string Hash) current;
            try { current = await ShopFileSystem.FingerprintAsync(file.SourcePath, token); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { throw new WorkflowValidationException($"Файл «{file.Name}» недоступен для чтения. Закройте его или проверьте доступ, затем обновите подбор."); }
            if (current.Hash != file.Sha256 || current.Length != file.Length)
                throw new WorkflowValidationException($"Изменился файл «{file.Name}». Обновите подбор перед копированием.");
        }
        if (!selection.Reviewed)
            foreach (var file in selection.Project.Files.Where(f => f.Kind == ShopFileKind.Map && !selection.Files.Contains(f)))
            {
                try
                {
                    var current = await ShopFileSystem.FingerprintAsync(file.SourcePath, token);
                    changed |= current.Hash != file.Sha256 || current.Length != file.Length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { changed = true; }
            }
        if (changed)
        {
            details?.Report(new(stage, selection.Project.Project.Name, Activity: "Повторно проверяем принадлежность карт и программ"));
            if (selection.Project.Machine is not { } machine)
                throw new WorkflowValidationException("Состав рабочей папки изменился. Обновите подбор перед копированием.");
            var current = await WorkflowShopDiscovery.DiscoverProjectAsync(selection.Project.Project, machine, selection.Project.Destination,
                selection.Project.Bending is not null, token, word);
            if (current.Problem is { } problem) throw new WorkflowValidationException(problem);
            if (machine.ImportProfile == "hans-pdf/v1" && current.WordMatch is not null)
            {
                var foreign = selection.Files.FirstOrDefault(old => !old.Ignored && current.Files.Any(f => ShopFileSystem.PathComparer.Equals(f.Name, old.Name) && f.Ignored));
                if (foreign is not null)
                    throw new WorkflowValidationException($"Файл «{foreign.Name}» теперь связан с другим DSP. Обновите подбор и проверьте выбранный комплект.");
                var newProjects = current.WordMatch.OtherProjects.Select(WorkflowWordMapRules.ProjectKey)
                    .Except((selection.Project.WordMatch?.OtherProjects ?? []).Select(WorkflowWordMapRules.ProjectKey)).Any();
                if (selection.Reviewed && newProjects && selection.Files.Any(old => current.Files.Any(f =>
                        ShopFileSystem.PathComparer.Equals(f.Name, old.Name) && !f.ConfirmedProjectLink)))
                    throw new WorkflowValidationException("В папке появился другой DSP, а принадлежность выбранных файлов не подтверждена. Обновите подбор и проверьте эти карты и GNC.");
            }
            var added = current.Files.Where(f => !selection.Project.Files.Any(old => ShopFileSystem.PathComparer.Equals(old.Name, f.Name))).ToArray();
            var competingNative = (current.NativeNames ?? []).Except(selection.Project.NativeNames ?? [], ShopFileSystem.PathComparer)
                .Where(name => machine.ImportProfile != "hans-pdf/v1" && WorkflowShopDiscovery.Key(name) == WorkflowShopDiscovery.Key(Path.GetFileName(selection.Project.Project.NativePath!))).ToArray();
            if (competingNative.Length > 0)
                throw new WorkflowValidationException("Добавлен другой рабочий проект: " + string.Join(", ", competingNative) + ". Проверьте принадлежность карт и УП.");
            if (selection.Reviewed)
            {
                var relevant = added.Where(f => !f.Ignored).ToArray();
                if (relevant.Length > 0)
                    throw new WorkflowValidationException("В папке появились новые возможные УП или карты: " + string.Join(", ", relevant.Select(f => f.Name)) + ". Обновите подбор и проверьте, нужны ли они в задании.");
            }
            else if (current.NeedsReview || !current.Files.Where(f => f.Suggested).Select(f => f.Name).Order(StringComparer.Ordinal)
                .SequenceEqual(selection.Files.Select(f => f.Name).Order(StringComparer.Ordinal)))
                throw new WorkflowValidationException("Состав рабочей папки изменился и влияет на комплект. " +
                    (current.ReviewReason ?? string.Join(", ", added.Select(f => f.Name))) + " Обновите подбор.");
        }
        if (selection.Project.Bending is { } bending && !ShopFileSystem.SameTree(bending,
            await ShopFileSystem.TreeAsync(Path.Combine(Path.GetDirectoryName(selection.Project.Project.NativePath!)!, "ГИБКА"), token,
                onFile: name => details?.Report(new(stage, selection.Project.Project.Name, "ГИБКА/" + name, Activity: "Проверяем файлы гибки")))))
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

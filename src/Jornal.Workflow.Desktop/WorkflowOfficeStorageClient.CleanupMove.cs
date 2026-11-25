using System.Text;
using System.Text.Json;
using Jornal.Workflow.Core;
using Jornal.Workflow.Infrastructure;

namespace Jornal.Workflow.Desktop;

internal sealed partial class WorkflowOfficeStorageClient
{
    private const string CleanupMoveSuffix = ".cleanup.json";
    private sealed record CleanupMove(string Folder, Guid Operation, bool WorkingFilesRemoved = false, bool InContainer = false);

    private static string CleanupContainer(CleanupMove move) =>
        Path.Combine(Path.GetDirectoryName(move.Folder)!, ".office-cleanup-" + move.Operation.ToString("N"));

    private static string DetachedCleanupFolder(CleanupMove move) =>
        move.InContainer ? Path.Combine(CleanupContainer(move), "copy") : CleanupContainer(move);

    private static void SaveCleanupMove(string original, CleanupMove move) =>
        WriteLocalState(original + CleanupMoveSuffix, JsonSerializer.SerializeToUtf8Bytes(move, JsonOptions));

    // The caller holds the same project lock as download and upload.
    private void RecoverInterruptedCleanup(string original)
    {
        var journal = original + CleanupMoveSuffix;
        if (!File.Exists(journal)) return;
        WorkflowOfficeProjectPaths.RequireOrdinaryCopyPath(journal);
        CleanupMove? move;
        using (var input = File.OpenRead(journal))
            move = JsonSerializer.Deserialize<CleanupMove>(input, JsonOptions);
        if (move is null || move.Operation == Guid.Empty || string.IsNullOrEmpty(move.Folder) ||
            move.Folder.Any(char.IsControl) || !Path.IsPathFullyQualified(move.Folder) ||
            Path.GetDirectoryName(move.Folder) is not { } parent)
            throw new WorkflowValidationException("Повреждена запись прерванной очистки. Проверьте локальную папку проекта.");

        var location = original + ".location";
        WorkflowOfficeProjectPaths.RequireOrdinaryCopyPath(location);
        var indexed = ReadCopyLocation(location);
        var detached = DetachedCleanupFolder(move);
        if (PathsEqual(move.Folder, detached))
            throw new WorkflowValidationException("Повреждена запись прерванной очистки. Проверьте локальную папку проекта.");
        WorkflowOfficeProjectPaths.RequireOrdinaryCopyPath(move.Folder);
        WorkflowOfficeProjectPaths.RequireOrdinaryCopyPath(detached);
        var sourceExists = Directory.Exists(move.Folder);
        var stagedExists = Directory.Exists(detached);
        var locationMatches = PathsEqual(move.Folder, indexed ?? original);
        var finished = move.WorkingFilesRemoved && !sourceExists && !stagedExists &&
            !File.Exists(move.Folder) && !File.Exists(detached) && Directory.Exists(parent);
        if (!locationMatches && !(finished && indexed is null))
            throw new WorkflowValidationException($"Расположение копии изменилось. Проверьте папки «{move.Folder}» и «{detached}».");

        if (stagedExists)
        {
            if (sourceExists || File.Exists(move.Folder))
                throw new WorkflowValidationException($"Не удалось вернуть копию в «{move.Folder}»: это место занято. Файлы сохранены в «{detached}».");
            if (move.WorkingFilesRemoved) RestoreCleanupMetadata(detached, original);
            RequireCleanupOwner(detached, original);
            try { Directory.Move(detached, move.Folder); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new WorkflowValidationException($"Копия сохранена в «{detached}». Закройте файлы, проверьте доступ и откройте проект ещё раз.");
            }
        }
        else if (sourceExists) RequireCleanupOwner(move.Folder, original);
        else if (finished) File.Delete(location);
        else throw new WorkflowValidationException($"Не удалось найти копию в «{move.Folder}» или «{detached}». Проверьте доступ к локальной папке.");
        if (move.InContainer)
        {
            try { Directory.Delete(CleanupContainer(move)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* A remaining container is hidden. */ }
        }
        File.Delete(journal);
    }

    private void RestoreCleanupMetadata(string folder, string original)
    {
        var owner = Path.Combine(folder, CopyOwnerName);
        var snapshot = Path.Combine(folder, SnapshotName);
        WorkflowOfficeProjectPaths.RequireOrdinaryCopyPath(owner);
        WorkflowOfficeProjectPaths.RequireOrdinaryCopyPath(snapshot);
        var expectedOwner = CopyOwner(original);
        if (!File.Exists(owner)) WriteLocalState(owner, Encoding.UTF8.GetBytes(expectedOwner));
        else if (File.ReadAllText(owner) != expectedOwner)
            throw new WorkflowValidationException($"Не удалось подтвердить принадлежность временной папки «{folder}».");
        // All previously verified files were removed before the journal entered this state.
        if (!File.Exists(snapshot)) WriteLocalState(snapshot, "{}"u8);
    }

    private static void WriteLocalState(string path, ReadOnlySpan<byte> bytes)
    {
        WorkflowOfficeProjectPaths.RequireOrdinaryCopyPath(path);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = File.Open(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                WorkflowServiceFiles.TryHide(temporary);
                output.Write(bytes);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }
}

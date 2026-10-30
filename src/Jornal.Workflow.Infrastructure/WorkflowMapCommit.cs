using System.Security.Cryptography;
using Jornal.Workflow.Core;

namespace Jornal.Workflow.Infrastructure;

internal sealed record MapCommitResult(string BackupPath, string? Problem = null)
{
    internal bool NeedsVerification => Problem is not null;
}

// The commit boundary is shared by PDF and DOC. Keep the replacement and the
// post-write observation separate so an I/O failure never claims that nothing changed.
internal class WorkflowMapCommit
{
    internal static WorkflowMapCommit Default { get; } = new();
    internal virtual void Replace(string stage, string path, string backup) => File.Replace(stage, path, backup);
    internal virtual Task<(long Length, string Hash)> FingerprintAsync(string path) => ShopFileSystem.FingerprintAsync(path, CancellationToken.None);

    internal async Task<MapCommitResult> SaveAsync(string path, byte[] original, byte[] result,
        Func<CancellationToken, Task> validate, CancellationToken token)
    {
        var folder = Path.Combine(Path.GetDirectoryName(path)!, WorkflowMapSigning.BackupFolder);
        ShopFileSystem.RequireOrdinaryPath(folder); Directory.CreateDirectory(folder); WorkflowServiceFiles.TryHide(folder);
        var lockPath = Path.Combine(folder, "signing.lock"); ShopFileSystem.RequireOrdinaryPath(lockPath);
        var stamp = Guid.NewGuid().ToString("N"); var stage = Path.Combine(folder, stamp + ".tmp");
        var revision = Path.Combine(folder, stamp); var backup = Path.Combine(revision, Path.GetFileName(path));
        var attempted = false; var replaced = false;
        try
        {
            await using var gate = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            WorkflowServiceFiles.TryHide(lockPath);
            await using (var output = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            { await output.WriteAsync(result, token); output.Flush(flushToDisk: true); }
            await validate(token);
            ShopFileSystem.RequireOrdinaryPath(path);
            await using var lease = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 65536, FileOptions.Asynchronous);
            var expected = SHA256.HashData(original);
            var actual = await SHA256.HashDataAsync(lease, token);
            if (!expected.AsSpan().SequenceEqual(actual))
                throw new WorkflowValidationException("Карта изменена после начала подбора. Файл не перезаписан; повторите подписание.");
            token.ThrowIfCancellationRequested(); Directory.CreateDirectory(revision);
            attempted = true; Replace(stage, path, backup); replaced = true;
            // An external rename can bypass the lease. Preserve both versions for review;
            // another replacement here could overwrite a newer external edit.
            var saved = await FingerprintAsync(backup);
            if (saved.Hash != Convert.ToHexString(expected))
                return new(backup, "Карта изменялась другой программой во время сохранения. Подписание остановлено; проверьте карту и резервную копию вручную.");
            return new(backup);
        }
        catch (Exception ex) when (attempted && ex is not OutOfMemoryException)
        {
            return new(backup, (replaced ? "Карта заменена, но проверка не завершена." : "Не удалось подтвердить замену карты.") +
                " Подписание остановлено; проверьте карту и резервную копию вручную перед следующим подписанием.");
        }
        finally
        {
            try { if (File.Exists(stage)) File.Delete(stage); if (Directory.Exists(revision) && !Directory.EnumerateFileSystemEntries(revision).Any()) Directory.Delete(revision); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Keep service files; never mask the commit outcome. */ }
        }
    }
}

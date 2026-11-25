using System.Text.Json;
using System.Text.Json.Serialization;
using Jornal.Workflow.Core;

namespace Jornal.Workflow.Infrastructure;

public enum ShopPublicationStep { Check, Reserve, Confirm, Rollback }
public sealed record ShopPublicationChange(ShopPublicationStep Step, Guid ProjectId, string RelativePath, string? CurrentRevision, string? PreparedRevision = null)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? ReservationId { get; init; }
}

public interface IWorkflowShopPublication : IDisposable
{
    bool SupportsRollback => false;
    Task UpdateAsync(ShopPublicationChange change, CancellationToken token);
}

public sealed class WorkflowShopPublicationLock : IWorkflowShopPublication
{
    public const string LeaseHeader = "X-Journal-Shop-Lease";
    public const string RollbackHeader = "X-Journal-Shop-Rollback";
    public bool SupportsRollback => true;
    private readonly FileStream _lock;
    private readonly string _statePath;
    private readonly Guid _owner;
    private bool _disposed;
    private sealed record Entry(Guid ProjectId, string RelativePath, string Revision, string? PreviousRevision, Guid? PendingOwner);
    private sealed record Reservation(Guid Id, Entry Pending, Entry? Before);
    private readonly Dictionary<string, Reservation> _reservations = new(StringComparer.OrdinalIgnoreCase);

    private WorkflowShopPublicationLock(FileStream lease, Guid owner)
    {
        _lock = lease;
        _statePath = Path.ChangeExtension(lease.Name, ".json");
        _owner = owner;
    }

    public static WorkflowShopPublicationLock Acquire(string databasePath, Guid databaseId, Guid orderId, Guid owner = default)
    {
        var directory = Path.Combine(Path.GetDirectoryName(databasePath)!, ".workflow-shop-transfers");
        try
        {
            ShopFileSystem.RequireOrdinaryPath(directory);
            Directory.CreateDirectory(directory);
            WorkflowServiceFiles.TryHide(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WorkflowValidationException)
        {
            throw new WorkflowValidationException($"Не удалось открыть служебную папку «{directory}». Проверьте, что папку можно создать и в неё разрешена запись.");
        }

        var path = Path.Combine(directory, $"{databaseId:N}-{orderId:N}.lock");
        try
        {
            ShopFileSystem.RequireOrdinaryPath(path);
            return new(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None), owner);
        }
        catch (IOException ex) when (ex.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021) ||
            OperatingSystem.IsLinux() && ex.HResult == 11)
        {
            throw new WorkflowValidationException("С заявкой уже выполняется операция передачи в цех. Дождитесь её окончания и повторите действие.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WorkflowValidationException)
        {
            throw new WorkflowValidationException($"Не удалось открыть служебный файл «{path}». Проверьте доступ на запись в папку «{directory}».");
        }
    }

    public Task UpdateAsync(ShopPublicationChange change, CancellationToken token) =>
        Task.Run(() => Update(change, token), token);

    private void Update(ShopPublicationChange change, CancellationToken token)
    {
        lock (_lock)
        {
            token.ThrowIfCancellationRequested();
            if (_disposed || _owner == Guid.Empty) throw new WorkflowValidationException("Блокировка передачи закрыта. Обновите подбор.");
            if (!Enum.IsDefined(change.Step) || change.ProjectId == Guid.Empty || !ValidPath(change.RelativePath) ||
                !ValidRevision(change.CurrentRevision) || !ValidRevision(change.PreparedRevision) ||
                change.Step == ShopPublicationStep.Reserve && change.PreparedRevision is null ||
                change.Step == ShopPublicationStep.Confirm && change.CurrentRevision is null ||
                change.Step == ShopPublicationStep.Rollback && (change.ReservationId is null || change.PreparedRevision is null) ||
                change.ReservationId == Guid.Empty)
                throw new WorkflowValidationException("Не удалось проверить сведения о папке задания.");
            try { UpdateEntries(change); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { throw new WorkflowValidationException($"Не удалось прочитать или обновить служебную запись «{_statePath}». Проверьте доступ к папке рядом с MDB."); }
        }
    }

    private void UpdateEntries(ShopPublicationChange change)
    {
        var path = change.RelativePath;
        _reservations.TryGetValue(path, out var reservation);
        if (change.Step == ShopPublicationStep.Rollback && reservation?.Id != change.ReservationId) return;
        var entries = ReadEntries();
        var current = entries.FindIndex(entry => entry.RelativePath.Equals(path, StringComparison.OrdinalIgnoreCase));
        var entry = current < 0 ? null : entries[current];
        if (change.Step == ShopPublicationStep.Rollback)
        {
            if (reservation is null || entry is null || entry != reservation.Pending || entry.ProjectId != change.ProjectId ||
                entry.Revision != change.PreparedRevision || entry.PreviousRevision != change.CurrentRevision)
                throw new WorkflowValidationException("Резерв перед отменой изменился. Проверьте готовый комплект перед повтором.");
            if (reservation.Before != entry)
            {
                if (reservation.Before is { } before) entries[current] = before;
                else entries.RemoveAt(current);
                SaveEntries(entries);
            }
            _reservations.Remove(path);
            return;
        }

        if (entry is not null && (entry.ProjectId != change.ProjectId ||
            entry.Revision != change.CurrentRevision && !(change.Step != ShopPublicationStep.Confirm &&
                entry.PendingOwner == _owner && entry.PreviousRevision == change.CurrentRevision)))
            throw new WorkflowValidationException($"Папка «{path}» ещё не совпадает с записью о передаче. " +
                "Проверьте синхронизацию или продолжите на компьютере, где начали отправку.");
        if (entries.Any(other => other.ProjectId == change.ProjectId && other.PendingOwner.HasValue && other != entry))
            throw new WorkflowValidationException("У проекта есть незавершённая передача по прежнему пути. Проверьте её папку перед новой отправкой.");
        if (change.Step == ShopPublicationStep.Check) return;
        var next = change.Step == ShopPublicationStep.Reserve ?
            new Entry(change.ProjectId, path, change.PreparedRevision!, change.CurrentRevision, _owner) :
            new Entry(change.ProjectId, path, change.CurrentRevision!, null, null);
        Reservation? undo = null;
        if (change.Step == ShopPublicationStep.Reserve && change.ReservationId is { } id)
        {
            if (reservation?.Id == id)
            {
                if (reservation.Pending != next || entry != next)
                    throw new WorkflowValidationException("Резерв передачи изменился. Обновите подбор.");
                return;
            }
            // Keep the previous entry only on this lease. An interrupted or unknown operation remains durable.
            undo = new(id, next, entry);
        }
        if (next != entry)
        {
            if (current < 0) entries.Add(next); else entries[current] = next;
            SaveEntries(entries);
        }
        if (undo is not null) _reservations[path] = undo;
        else _reservations.Remove(path);
    }

    private List<Entry> ReadEntries()
    {
        ShopFileSystem.RequireOrdinaryPath(_statePath);
        try
        {
            using var stream = File.OpenRead(_statePath);
            if (stream.Length > 8 * 1024 * 1024) throw new JsonException();
            var entries = JsonSerializer.Deserialize<List<Entry>>(stream) ?? throw new JsonException();
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (entries.Any(entry => entry is null || entry.ProjectId == Guid.Empty || !ValidPath(entry.RelativePath) ||
                entry.Revision is null || !ValidRevision(entry.Revision) || !ValidRevision(entry.PreviousRevision) ||
                entry.PendingOwner == Guid.Empty || !paths.Add(entry.RelativePath)))
                throw new JsonException();
            return entries;
        }
        catch (FileNotFoundException) { return []; }
        catch (JsonException) { throw new WorkflowValidationException($"Служебная запись передачи в цех «{_statePath}» повреждена. Проверьте её в общей папке базы."); }
    }

    private void SaveEntries(List<Entry> entries)
    {
        var temporary = _statePath + ".tmp";
        ShopFileSystem.RequireOrdinaryPath(temporary);
        try
        {
            using (var stream = File.Create(temporary))
            {
                JsonSerializer.Serialize(stream, entries);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, _statePath, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    private static bool ValidRevision(string? revision) => revision is null || revision.Length == 64 && revision.All(char.IsAsciiHexDigit);
    private static bool ValidPath(string? path) => path is { Length: > 0 and <= 32760 } && !path.Contains('\\') &&
        path.Split('/').All(WorkflowWindowsFolderName.IsValid);

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _lock.Dispose();
        }
    }
}

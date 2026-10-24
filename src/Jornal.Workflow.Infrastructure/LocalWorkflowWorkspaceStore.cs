using System.Text.Json;
using Jornal.Workflow.Core;

namespace Jornal.Workflow.Infrastructure;

// Только локальная разработка. Не является реализацией общей сетевой БД.
public sealed class LocalWorkflowWorkspaceStore(string path)
{
    private const long MaximumSize = 16 * 1024 * 1024;
    private readonly string _path = ValidatePath(path);
    public string FilePath => _path;
    private static string ValidatePath(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
            throw new ArgumentException("Локальное хранилище разработки нельзя размещать по сетевому пути.", nameof(path));
        return Path.GetFullPath(path);
    }

    public Task<WorkflowWorkspace> ReadAsync(CancellationToken token = default) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        using var lease = new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        return Read();
    }, token);

    public Task<WorkflowWorkspace> WriteAsync(long expectedVersion, Func<WorkflowWorkspace, WorkflowWorkspace> change,
        CancellationToken token = default, bool backupBeforeWrite = false) =>
        WriteCoreAsync(expectedVersion, change, token, backupBeforeWrite);

    public Task<WorkflowWorkspace> SaveEmployeeProfileAsync(Employee? expected, string name, uint color, CancellationToken token = default) =>
        WriteCoreAsync(null, book => WorkflowChanges.SaveEmployeeProfile(book, expected, name, color), token, false);

    public async Task<(WorkflowWorkspace Workspace, Guid OrderId)> CreateOrderFromNamesAsync(string manager, string customer,
        Guid actor, DateTimeOffset at, CancellationToken token = default)
    {
        var id = Guid.Empty;
        var workspace = await WriteCoreAsync(null, book => WorkflowChanges.CreateOrderFromNames(book, manager, customer, actor, at, out id), token, false).ConfigureAwait(false);
        return (workspace, id);
    }

    public Task<WorkflowWorkspace> WriteOrderAsync(Guid orderId, long expectedOrderVersion,
        Func<WorkflowWorkspace, WorkflowWorkspace> change, CancellationToken token = default, bool allowTrashed = false) =>
        WriteCoreAsync(null, book =>
        {
            var order = book.Orders.SingleOrDefault(o => o.Id == orderId);
            if (order?.IsDeleted == true && !allowTrashed) throw new WorkflowValidationException("Заявка находится в корзине. Восстановите её перед изменением; введённые правки сохранены в форме.");
            if (order?.Version != expectedOrderVersion) throw new WorkflowConflictException();
            return change(book);
        }, token, false);

    public Task<WorkflowWorkspace> WriteProjectRootAsync(string? expectedRoot, string? root, CancellationToken token = default) =>
        WriteCoreAsync(null, book =>
        {
            var next = WorkflowProjects.SetRoot(book, root);
            if (book.ProjectRoot != expectedRoot && book.ProjectRoot != next.ProjectRoot) throw new WorkflowConflictException();
            return next;
        }, token, false);

    private Task<WorkflowWorkspace> WriteCoreAsync(long? expectedVersion, Func<WorkflowWorkspace, WorkflowWorkspace> change,
        CancellationToken token, bool backupBeforeWrite) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        using var lease = new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var current = Read();
        if (expectedVersion is { } expected && current.Version != expected) throw new WorkflowConflictException();
        var next = change(current);
        if (ReferenceEquals(current, next)) return current;
        next = next with { Version = current.Version + 1 }; next.Validate();
        if (backupBeforeWrite) Backup(allowMissing: true);
        Save(next);
        return next;
    }, token);

    private void Save(WorkflowWorkspace value)
    {
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value, new JsonSerializerOptions { WriteIndented = true });
                if (stream.Length > MaximumSize) throw new IOException("Локальные данные разработки превышают допустимый размер.");
                stream.Flush(true);
            }
            // После начала фиксации отмена не выдаёт подтверждённую запись за неудачную.
            File.Move(temporary, _path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private void Backup(bool allowMissing = false)
    {
        try { File.Copy(_path, _path + $".{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.bak", overwrite: false); }
        catch (FileNotFoundException) when (allowMissing) { /* A new workspace has nothing to back up. */ }
    }

    private WorkflowWorkspace Read()
    {
        WorkflowWorkspace value;
        FileStream source;
        try { source = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read); }
        catch (FileNotFoundException) { return WorkflowWorkspace.Empty; }
        using (source)
        {
            if (source.Length > MaximumSize) throw new IOException("Локальные данные разработки превышают допустимый размер.");
            value = JsonSerializer.Deserialize<WorkflowWorkspace>(source) ?? throw new WorkflowValidationException("Файл локальных данных пуст.");
        }
        if (value.SchemaVersion is 1 or 2 or 3 or 4 or 5 or 6)
        {
            value = WorkflowWorkspaceMigration.Upgrade(value);
            Backup(); Save(value);
        }
        value.Validate(); return value;
    }
}

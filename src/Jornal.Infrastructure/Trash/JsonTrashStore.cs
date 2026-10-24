using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Jornal.Infrastructure.Trash;

public sealed class JsonTrashStore(string root) : ITrashStore
{
    public Task<ITrashLease> OpenAsync(TrashSource source, CancellationToken cancellationToken = default) => Task.Run<ITrashLease>(() =>
    {
        source.Validate();
        var directory = Path.Combine(Path.GetFullPath(root), Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source.Key))));
        if (OperatingSystem.IsLinux()) Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        else Directory.CreateDirectory(directory);
        var timer = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return new Lease(directory, source, OpenPrivate(Path.Combine(directory, "archive.lock"), FileMode.OpenOrCreate)); }
            catch (IOException) when (timer.Elapsed < TimeSpan.FromSeconds(3))
            { if (cancellationToken.WaitHandle.WaitOne(25)) cancellationToken.ThrowIfCancellationRequested(); }
        }
    }, cancellationToken);

    private static FileStream OpenPrivate(string path, FileMode mode)
    {
        var options = new FileStreamOptions { Mode = mode, Access = FileAccess.ReadWrite, Share = FileShare.None };
        if (OperatingSystem.IsLinux()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new(path, options);
    }

    private sealed class Lease(string directory, TrashSource source, FileStream gate) : ITrashLease
    {
        private string EntryPath(Guid id) => Path.Combine(directory, id.ToString("N") + ".journal-trash.json");
        public Task<IReadOnlyList<TrashEntry>> ReadAsync() => Task.Run<IReadOnlyList<TrashEntry>>(() =>
        {
            var entries = new List<TrashEntry>();
            foreach (var path in Directory.EnumerateFiles(directory, "*.journal-trash.json"))
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length > 256 * 1024) throw new InvalidDataException("Запись корзины слишком большая. Архив не изменён.");
                TrashEntry entry;
                try { entry = JsonSerializer.Deserialize<TrashEntry>(stream) ?? throw new JsonException(); }
                catch (JsonException exception) { throw new InvalidDataException("Не удалось прочитать архивную копию. Автоочистка остановлена.", exception); }
                entry.Validate();
                if (entry.Source.Key != source.Key || Path.GetFileName(path) != Path.GetFileName(EntryPath(entry.OperationId)))
                    throw new InvalidDataException("Архив относится к другому источнику или имеет неверный идентификатор.");
                entries.Add(entry);
            }
            return entries;
        });

        public Task SaveAsync(TrashEntry entry) => Task.Run(() =>
        {
            entry.Validate();
            if (entry.Source.Key != source.Key) throw new InvalidDataException("Архив относится к другой базе.");
            var destination = EntryPath(entry.OperationId);
            var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = OpenPrivate(temporary, FileMode.CreateNew))
                {
                    JsonSerializer.Serialize(stream, entry);
                    stream.Flush(flushToDisk: true);
                }
                // Проверяем возможность прочитать подготовленный снимок до удаления из MDB.
                using (var verify = File.OpenRead(temporary))
                    if (JsonSerializer.Deserialize<TrashEntry>(verify) != entry) throw new InvalidDataException("Архивная копия не прошла проверку.");
                File.Move(temporary, destination, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        });

        public Task RemoveAsync(Guid id) => Task.Run(() => File.Delete(EntryPath(id)));
        public void Dispose() => gate.Dispose(); // lock-файл не удаляется: общий inode нужен другим процессам.
    }
}

public sealed class MemoryTrashStore : ITrashStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, TrashEntry> _entries = [];
    public async Task<ITrashLease> OpenAsync(TrashSource source, CancellationToken cancellationToken = default)
    { await _gate.WaitAsync(cancellationToken); return new Lease(this, source); }
    private sealed class Lease(MemoryTrashStore owner, TrashSource source) : ITrashLease
    {
        public Task<IReadOnlyList<TrashEntry>> ReadAsync() => Task.FromResult<IReadOnlyList<TrashEntry>>(owner._entries.Values.Where(entry => entry.Source.Key == source.Key).ToArray());
        public Task SaveAsync(TrashEntry entry)
        { entry.Validate(); if (entry.Source.Key != source.Key) throw new InvalidDataException(); owner._entries[entry.OperationId] = entry; return Task.CompletedTask; }
        public Task RemoveAsync(Guid id) { owner._entries.Remove(id); return Task.CompletedTask; }
        public void Dispose() => owner._gate.Release();
    }
}

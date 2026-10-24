namespace Jornal.Desktop.DataSources;

public static class JournalFileWatcher
{
    // Подключение к недоступной SMB-папке не должно блокировать UI-поток.
    public static Task<IDisposable> SubscribeAsync(string path, Action changed, Action error) => Task.Run<IDisposable>(() =>
    {
        var watcher = new FileSystemWatcher(Path.GetDirectoryName(Path.GetFullPath(path))!, Path.GetFileName(path))
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            InternalBufferSize = 8192
        };
        try
        {
            watcher.Changed += (_, _) => changed();
            watcher.Created += (_, _) => changed();
            watcher.Deleted += (_, _) => changed();
            watcher.Renamed += (_, _) => changed();
            watcher.Error += (_, _) => error();
            watcher.EnableRaisingEvents = true;
            return watcher;
        }
        catch { watcher.Dispose(); throw; }
    });
}

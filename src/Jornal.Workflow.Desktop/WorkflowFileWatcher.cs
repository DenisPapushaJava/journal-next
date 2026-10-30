namespace Jornal.Workflow.Desktop;

internal static class WorkflowFileWatcher
{
    public static Task<IDisposable> SubscribeAsync(string path, Action<bool> changed, Action failed) => Task.Run<IDisposable>(() =>
    {
        var watcher = new FileSystemWatcher(Path.GetDirectoryName(path)!, Path.GetFileName(path))
        { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName };
        try
        {
            watcher.Changed += (_, _) => changed(false);
            watcher.Created += (_, _) => changed(true);
            watcher.Deleted += (_, _) => changed(true);
            watcher.Renamed += (_, _) => changed(true);
            watcher.Error += (_, _) => failed();
            watcher.EnableRaisingEvents = true;
            return watcher;
        }
        catch { watcher.Dispose(); throw; }
    });
}

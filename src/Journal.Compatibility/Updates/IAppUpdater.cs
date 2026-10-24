namespace Jornal.Desktop.Updates;

public sealed record AppUpdate(string Version, string Notes, long Size);

public interface IAppUpdater
{
    string CurrentVersion { get; }
    bool IsInstalled { get; }
    Task<AppUpdate?> CheckAsync(bool preview, CancellationToken cancellationToken);
    Task DownloadAsync(AppUpdate update, IProgress<int> progress, CancellationToken cancellationToken);
    void ScheduleRestart(AppUpdate update);
}

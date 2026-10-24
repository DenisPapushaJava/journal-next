using Jornal.Infrastructure.Diagnostics;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Velopack;
using Velopack.Sources;

namespace Jornal.Desktop.Updates;

public sealed class VelopackAppUpdater : IAppUpdater
{
    public const string RepositoryUrl = "https://github.com/DenisPapushaJava/journal-v2.0";
    public const string ReleasesUrl = RepositoryUrl + "/releases";
    private readonly Func<bool, UpdateManager> _managerFactory;
    private UpdateManager? _manager;
    private UpdateInfo? _candidate;
    private AppUpdate? _offered;
    private bool _downloaded;

    public VelopackAppUpdater(Func<bool, UpdateManager>? managerFactory = null)
    {
        _managerFactory = managerFactory ?? (preview => new UpdateManager(
            new JournalGithubSource(preview),
            new UpdateOptions { ExplicitChannel = RuntimeChannel, AllowVersionDowngrade = false }));
    }

    public static string RuntimeChannel =>
        (OperatingSystem.IsWindows() ? "win" : "linux") + "-" + RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
    public string CurrentVersion => _manager?.CurrentVersion?.ToString() ??
        typeof(VelopackAppUpdater).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "неизвестна";
    public bool IsInstalled
    {
        get
        {
            try { return (_manager ??= _managerFactory(false)).IsInstalled; }
            catch (Exception exception) { AppDiagnostics.Capture(DiagnosticOperation.Update, exception); return false; }
        }
    }

    public async Task<AppUpdate?> CheckAsync(bool preview, CancellationToken cancellationToken)
    {
        var manager = _managerFactory(preview);
        var candidate = await DiagnosticStages.RunAsync(DiagnosticStage.CheckUpdates, () => Task.Run(() => manager.CheckForUpdatesAsync(), cancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(30), cancellationToken));
        cancellationToken.ThrowIfCancellationRequested();
        if (candidate is not null && !string.Equals(candidate.TargetFullRelease.PackageId, manager.AppId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Пакет предназначен для другого приложения.");
        if (!preview && candidate?.TargetFullRelease.Version.IsPrerelease == true)
            throw new InvalidDataException("Тестовый пакет попал в стабильный канал.");
        _manager = manager; _candidate = null; _offered = null; _downloaded = false;
        if (candidate is null) return null;
        _candidate = candidate;
        return _offered = new(candidate.TargetFullRelease.Version.ToString(), candidate.TargetFullRelease.NotesMarkdown ?? "", candidate.TargetFullRelease.Size);
    }

    public async Task DownloadAsync(AppUpdate update, IProgress<int> progress, CancellationToken cancellationToken)
    {
        EnsureCandidate(update);
        await DiagnosticStages.RunAsync(DiagnosticStage.DownloadUpdate, () => Task.Run(() => _manager!.DownloadUpdatesAsync(_candidate!, progress.Report, cancellationToken), cancellationToken));
        cancellationToken.ThrowIfCancellationRequested();
        _downloaded = true;
    }

    public void ScheduleRestart(AppUpdate update)
    {
        EnsureCandidate(update);
        if (!_downloaded) throw new InvalidOperationException("Обновление ещё не загружено.");
        // Другой экземпляр этой же Windows-установки может сохранять MDB.
        if (OperatingSystem.IsWindows() && Environment.ProcessPath is { } executable)
            foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable)))
                using (process)
                    if (process.Id != Environment.ProcessId && string.Equals(process.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Закройте другие окна Journal перед установкой обновления.");
        // Приложение закроется обычным путём после сохранения состояния; Environment.Exit не обходит OnClosed.
        DiagnosticStages.Run(DiagnosticStage.ApplyUpdate, () => _manager!.WaitExitThenApplyUpdates(_candidate!.TargetFullRelease, silent: false, restart: true, restartArgs: []));
    }

    private void EnsureCandidate(AppUpdate update)
    {
        if (!ReferenceEquals(update, _offered) || _candidate is null || _manager is null)
            throw new InvalidOperationException("Проверьте обновления заново.");
    }
}

using Jornal.Infrastructure.Diagnostics;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jornal.Desktop.Updates;

namespace Jornal.Desktop.ViewModels;

public partial class AppUpdatesViewModel : ObservableObject
{
    public static readonly TimeSpan AutomaticCheckInterval = TimeSpan.FromHours(6);
    private readonly IAppUpdater _updater;
    private readonly TimeProvider _timeProvider;
    private readonly IAppUpdatePreferencesStore? _preferencesStore;

    public AppUpdatesViewModel(IAppUpdater updater, TimeProvider? timeProvider = null, IAppUpdatePreferencesStore? preferencesStore = null)
    {
        _updater = updater;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _preferencesStore = preferencesStore;
        var loaded = preferencesStore?.Load();
        _preferencesWarning = loaded?.Error;
        _checkAutomatically = loaded?.Preferences?.CheckAutomatically ?? (preferencesStore is not null && loaded?.Error is null);
        // Явный выбор важнее установленной версии. Первый запуск alpha/beta продолжает её канал.
        _includePreview = loaded?.Preferences?.IncludePreview ?? (loaded?.Error is null && updater.IsInstalled &&
            Velopack.SemanticVersion.TryParse(updater.CurrentVersion, out var version) && version.IsPrerelease);
        if (preferencesStore is not null && loaded is { Preferences: null, Error: null }) SavePreferences();
    }
    private AppUpdate? _update;
    private readonly object _progressLock = new();
    private int _downloadOperation;
    private bool _automaticChecksStarted;
    private CancellationTokenSource? _automaticChecksWake;
    private long? _lastCheckCompleted;
    private Func<bool> _canRestart = () => false;
    private Action _prepareRestart = () => { };
    private Action _closeApplication = () => { };
    [ObservableProperty] private bool _checkAutomatically;
    public string CurrentVersion => _updater.CurrentVersion;
    public bool IsInstalled => _updater.IsInstalled;
    public string InstallationHint => OperatingSystem.IsWindows()
        ? "Для обновлений установите приложение через OfflineSetup.exe."
        : "Для обновлений запустите приложение из AppImage.";
    [ObservableProperty] private string _status = "Проверка ещё не выполнялась.";
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string? _preferencesWarning;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isApplying;
    [ObservableProperty] private bool _restartScheduled;
    [ObservableProperty] private bool _includePreview;
    [ObservableProperty] private bool _hasUpdate;
    [ObservableProperty] private bool _isDownloaded;
    [ObservableProperty] private int _downloadProgress;
    [ObservableProperty] private string _availableVersion = "";
    [ObservableProperty] private string _releaseNotes = "";
    public string NotificationText => HasUpdate ? $"Доступна новая версия Journal {AvailableVersion}" : "Обновления Journal";
    public bool HasError => Error is not null;
    public bool HasPreferencesWarning => PreferencesWarning is not null;
    public bool CanCheck => IsInstalled && !IsBusy;
    public bool CanDownload => HasUpdate && !IsDownloaded && !IsBusy;
    public bool CanInstall => HasUpdate && IsDownloaded && !IsBusy;
    [ObservableProperty] private bool _isDownloading;
    public void ConfigureRestart(Func<bool> canRestart, Action prepare, Action close)
    { _canRestart = canRestart; _prepareRestart = prepare; _closeApplication = close; }
    public void ClearRestart()
    { _canRestart = static () => false; _prepareRestart = static () => { }; _closeApplication = static () => { }; }
    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(HasError));
    partial void OnPreferencesWarningChanged(string? value) => OnPropertyChanged(nameof(HasPreferencesWarning));
    partial void OnIsBusyChanged(bool value) => NotifyCommands();
    partial void OnHasUpdateChanged(bool value) { OnPropertyChanged(nameof(NotificationText)); NotifyCommands(); }
    partial void OnAvailableVersionChanged(string value) => OnPropertyChanged(nameof(NotificationText));
    partial void OnIsDownloadedChanged(bool value) => NotifyCommands();
    partial void OnIncludePreviewChanged(bool value)
    {
        _update = null; HasUpdate = false; IsDownloaded = false; ReleaseNotes = ""; AvailableVersion = ""; Error = null;
        Status = "Канал обновлений изменён.";
        SavePreferences();
    }
    partial void OnCheckAutomaticallyChanged(bool value)
    {
        SavePreferences();
        _lastCheckCompleted = null;
        _automaticChecksWake?.Cancel();
        if (!IsBusy) Status = value ? "Автопроверка включена." : "Автопроверка отключена.";
    }
    private void SavePreferences()
    {
        if (_preferencesStore is null) return;
        try { _preferencesStore.Save(new(IncludePreview: IncludePreview, CheckAutomatically: CheckAutomatically)); PreferencesWarning = null; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppDiagnostics.Capture(DiagnosticOperation.Update, exception);
            PreferencesWarning = "Не удалось сохранить настройки обновлений. Выбор действует до закрытия программы. Проверьте доступ к папке профиля.";
        }
    }
    private void NotifyCommands()
    {
        foreach (var name in new[] { nameof(CanCheck), nameof(CanDownload), nameof(CanInstall) }) OnPropertyChanged(name);
        CheckCommand.NotifyCanExecuteChanged(); DownloadCommand.NotifyCanExecuteChanged(); InstallCommand.NotifyCanExecuteChanged();
    }

    public async Task RunAutomaticChecksAsync(CancellationToken cancellationToken)
    {
        if (_automaticChecksStarted || !IsInstalled || cancellationToken.IsCancellationRequested) return;
        _automaticChecksStarted = true;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                using var wake = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _automaticChecksWake = wake;
                try
                {
                    if (!CheckAutomatically)
                    {
                        // Ожидание настройки без таймеров и сетевых запросов.
                        await Task.Delay(Timeout.InfiniteTimeSpan, wake.Token);
                        continue;
                    }
                    var remaining = _lastCheckCompleted is { } completed
                        ? AutomaticCheckInterval - _timeProvider.GetElapsedTime(completed)
                        : TimeSpan.Zero;
                    if (remaining <= TimeSpan.Zero)
                    {
                        // Все проверки разделяют IsBusy. Найденный пакет сохраняем до решения пользователя.
                        if (CanCheck && !HasUpdate) await CheckForUpdatesAsync(quiet: true, wake.Token);
                        remaining = AutomaticCheckInterval;
                    }
                    // Один таймер без накопления пропущенных попыток после сна или отсутствия сети.
                    await Task.Delay(remaining, _timeProvider, wake.Token);
                }
                catch (OperationCanceledException) when (wake.IsCancellationRequested) { }
                finally { _automaticChecksWake = null; }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    [RelayCommand(CanExecute = nameof(CanCheck), IncludeCancelCommand = true)]
    private Task CheckAsync(CancellationToken cancellationToken) => CheckForUpdatesAsync(quiet: false, cancellationToken);

    private async Task CheckForUpdatesAsync(bool quiet, CancellationToken cancellationToken)
    {
        if (!CanCheck || cancellationToken.IsCancellationRequested) return;
        IsBusy = true;
        if (!quiet) { Error = null; Status = "Проверяем выпуски на GitHub…"; }
        try
        {
            var update = await _updater.CheckAsync(IncludePreview, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _update = update;
            Error = null;
            IsDownloaded = false;
            HasUpdate = _update is not null;
            AvailableVersion = _update?.Version ?? "";
            ReleaseNotes = string.IsNullOrWhiteSpace(_update?.Notes) ? "Описание изменений не добавлено." : _update.Notes;
            Status = HasUpdate ? $"Доступна версия {AvailableVersion}." : "Обновлений нет.";
        }
        catch (OperationCanceledException) { if (!quiet) Status = "Проверка отменена."; }
        catch (Exception exception)
        {
            if (quiet) AppDiagnostics.Capture(DiagnosticOperation.Update, exception);
            else Fail(exception, "Не удалось проверить обновления. Проверьте интернет и повторите попытку. Можно продолжать работу с журналом.");
        }
        finally
        {
            // Отменённая фоновая проверка не откладывает возобновление после переключения настройки.
            if (!quiet || !cancellationToken.IsCancellationRequested) _lastCheckCompleted = _timeProvider.GetTimestamp();
            IsBusy = false;
            OnPropertyChanged(nameof(CurrentVersion));
        }
    }

    [RelayCommand(CanExecute = nameof(CanDownload), IncludeCancelCommand = true)]
    private async Task DownloadAsync(CancellationToken cancellationToken)
    {
        if (!CanDownload || _update is null) return;
        IsBusy = true; Error = null; Status = "Загружаем обновление…";
        int operation;
        lock (_progressLock) { operation = ++_downloadOperation; IsDownloading = true; DownloadProgress = 0; }
        try
        {
            await _updater.DownloadAsync(_update, new Progress<int>(value =>
            {
                lock (_progressLock)
                    if (operation == _downloadOperation && IsDownloading && !cancellationToken.IsCancellationRequested)
                        DownloadProgress = Math.Max(DownloadProgress, Math.Clamp(value, 0, 100));
            }), cancellationToken);
            lock (_progressLock) { IsDownloaded = true; DownloadProgress = 100; }
            Status = "Обновление загружено.";
        }
        catch (OperationCanceledException) { Status = "Загрузка отменена."; }
        catch (Exception exception) { Fail(exception, "Не удалось загрузить или проверить пакет. Текущая версия продолжает работать. Повторите загрузку."); }
        finally { lock (_progressLock) { ++_downloadOperation; IsDownloading = false; } IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private void Install()
    {
        if (!CanInstall || _update is null) return;
        Error = null;
        if (!_canRestart()) { Error = "Завершите операции с журналом и закройте другие формы перед установкой."; return; }
        IsBusy = true; IsApplying = true;
        try
        {
            _prepareRestart();
            _updater.ScheduleRestart(_update);
            RestartScheduled = true;
            _closeApplication();
        }
        catch (Exception exception) { Fail(exception, "Не удалось подготовить обновление. Journal остаётся открыт. " + (exception is InvalidOperationException ? exception.Message : "Проверьте доступ к папке приложения и профилю, затем повторите попытку.")); }
        finally { IsApplying = false; IsBusy = false; }
    }
    private void Fail(Exception exception, string message) { Error = AppDiagnostics.WithCode(message, AppDiagnostics.Capture(DiagnosticOperation.Update, exception)); Status = "Обновление не выполнено."; }
}

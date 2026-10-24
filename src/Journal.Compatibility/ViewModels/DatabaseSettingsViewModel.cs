using Jornal.Infrastructure.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jornal.Infrastructure.Local;
using Jornal.Core.Orders;

namespace Jornal.Desktop.ViewModels;

public partial class DatabaseSettingsViewModel(string? currentPath, Func<string?, CancellationToken, Task<string?>> connect) : ObservableObject
{
    [ObservableProperty] private string _databasePath = LocalJournalStore.IsLocalCopyPath(currentPath) ? "" : currentPath ?? "";
    [ObservableProperty] private string _localCopyPath = LocalJournalStore.IsLocalCopyPath(currentPath) ? currentPath! : "";
    private int _sourceKind = currentPath is null ? 0 : LocalJournalStore.IsLocalCopyPath(currentPath) ? 2 : 1;
    public bool UseDatabase { get => _sourceKind == 1; set { if (value) SelectSource(1); } }
    public bool UseDemo { get => _sourceKind == 0; set { if (value) SelectSource(0); } }
    public bool UseLocalCopy { get => _sourceKind == 2; set { if (value && CanUseLocalCopy) SelectSource(2); } }
    public bool CanUseLocalCopy => OperatingSystem.IsLinux();
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanChangeSource))] private bool _isImporting;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanChangeSource))] private bool _isConnecting;
    public bool CanChangeSource => !IsImporting && !IsConnecting;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError))] private string? _error;
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private string _currentSource = currentPath ?? "Демонстрационный журнал";
    public bool HasError => !string.IsNullOrEmpty(Error);
    public AccessHostHelpViewModel AccessHostHelp { get; } = new();
    partial void OnErrorChanged(string? value) => AccessHostHelp.ShowFor(null);
    public string DatabaseCapabilities => OperatingSystem.IsWindows()
        ? "Для первой подготовки закройте журнал на остальных ПК. Резервная копия создаётся автоматически."
        : "MDB доступна только для чтения. Для изменений используйте локальную тестовую копию.";
    public event EventHandler? Connected;
    partial void OnDatabasePathChanged(string value) { Error = null; IsConnected = false; }
    partial void OnLocalCopyPathChanged(string value) { Error = null; IsConnected = false; }
    partial void OnIsImportingChanged(bool value) => ConnectCommand.NotifyCanExecuteChanged();

    private void SelectSource(int kind)
    {
        if (_sourceKind == kind) return;
        _sourceKind = kind;
        IsConnected = false;
        foreach (var name in new[] { nameof(UseDatabase), nameof(UseDemo), nameof(UseLocalCopy) }) OnPropertyChanged(name);
        Error = null;
    }

    public async Task ImportLocalCopyAsync(string source, string destination, CancellationToken cancellationToken = default)
    {
        if (!CanUseLocalCopy || !CanChangeSource) return;
        Error = null;
        IsImporting = true;
        try
        {
            await new LocalJournalImporter().ImportAsync(source, destination, cancellationToken);
            LocalCopyPath = Path.GetFullPath(destination);
            UseLocalCopy = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Error = AppDiagnostics.Describe(DiagnosticOperation.OrderRead, exception, "Не удалось создать локальную копию.");
        }
        finally { IsImporting = false; }
    }

    private bool CanConnect() => !IsImporting;

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        Error = null;
        string? path = null;
        if (UseDatabase)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(DatabasePath) || !Path.IsPathFullyQualified(DatabasePath.Trim()) ||
                    !string.Equals(Path.GetExtension(DatabasePath.Trim()), ".mdb", StringComparison.OrdinalIgnoreCase))
                { Error = "Укажите полный путь к существующему файлу .mdb или выберите его кнопкой «Обзор»."; return; }
                path = Path.GetFullPath(DatabasePath.Trim());
            }
            catch (ArgumentException) { Error = "Путь к файлу содержит недопустимые символы."; return; }
        }
        else if (UseLocalCopy)
        {
            try
            {
                if (!Path.IsPathFullyQualified(LocalCopyPath.Trim()) || !LocalJournalStore.IsLocalCopyPath(LocalCopyPath.Trim()))
                { Error = "Выберите существующую локальную копию " + LocalJournalStore.Extension + "."; return; }
                path = Path.GetFullPath(LocalCopyPath.Trim());
            }
            catch (ArgumentException) { Error = "Путь к локальной копии содержит недопустимые символы."; return; }
        }
        IsConnecting = true;
        try
        {
            Error = await connect(path, cancellationToken);
            if (Error is not null || cancellationToken.IsCancellationRequested) return;
            IsConnected = true;
            Connected?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (OrderReadException exception)
        {
            Error = AppDiagnostics.Describe(DiagnosticOperation.OrderRead, exception, "Не удалось подключить базу.");
            AccessHostHelp.ShowFor(exception);
        }
        finally { IsConnecting = false; }
    }
}

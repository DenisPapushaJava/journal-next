using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Jornal.Workflow.Core;

namespace Jornal.Workflow.Desktop;

public partial class WorkflowViewModel
{
    private WorkflowPreferencesStore _preferences;
    private WorkflowPreferences _localPreferences = new();
    private bool _loadingLocalPreferences;
    [ObservableProperty] private bool _isDarkTheme;
    [ObservableProperty] private string? _themeError;
    partial void OnIsDarkThemeChanged(bool value)
    {
        if (_loadingLocalPreferences) return;
        if (Error == ThemeError) Error = null;
        ThemeError = null;
        try
        {
            _preferences.Update(current => current with { DarkTheme = value });
            _localPreferences = _localPreferences with { DarkTheme = value };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
        { Error = ThemeError = "Не удалось сохранить тему. Выбор действует до закрытия."; }
    }
    private Employee? ActiveEmployee => CurrentEmployee is { } current ? _workspace.Employees.SingleOrDefault(e => e.Id == current.Id) : null;
    public string ProfileName => ActiveEmployee?.Name ?? "Профиль не выбран";
    public IBrush ProfileBrush => new SolidColorBrush(Color.FromUInt32(ActiveEmployee?.ColorArgb ?? 0xFF628DB2));
    public string ProfileHint => ProfileName + " · изменить: Настройки → Общие → Технолог";
    partial void OnCurrentEmployeeChanged(Employee? value) => NotifyProfile();
    private void NotifyProfile()
    { OnPropertyChanged(nameof(ProfileName)); OnPropertyChanged(nameof(ProfileBrush)); OnPropertyChanged(nameof(ProfileHint)); }

    public void LoadLocalPreferences(WorkflowPreferencesStore store)
    {
        var value = store.Load(); _preferences = store; _localPreferences = value;
        _loadingLocalPreferences = true;
        try { IsDarkTheme = value.DarkTheme; } finally { _loadingLocalPreferences = false; }
        ThemeError = null; NotifyProfile(); NotifyEntrySuggestions();
    }
    public void UseLocalPreferencesStore(WorkflowPreferencesStore store) => _preferences = store;
    private WorkflowProfileDraft CaptureProfile() => ActiveEmployee is { } employee ?
        new(employee.Id, employee.Name, employee.ColorArgb ?? 0xFF628DB2) : new(Guid.Empty, "");

    private void RefreshEmployees(Guid? actorId)
    {
        var manager = Manager?.Id; var technologist = Technologist?.Id;
        Employees.Clear(); foreach (var e in _workspace.Employees) Employees.Add(e);
        CurrentEmployee = Employees.SingleOrDefault(e => e.Id == actorId);
        Manager = Employees.SingleOrDefault(e => e.Id == manager);
        Technologist = Employees.SingleOrDefault(e => e.Id == technologist);
        NotifyProfile();
    }
    private async Task<string?> SaveProfileAsync(Employee? expectedEmployee, WorkflowProfileDraft profile, CancellationToken token)
    {
        if (!CanUse) return "Дождитесь завершения текущей операции.";
        var saved = false;
        await RunAsync(async () =>
        {
            var actor = CurrentEmployee?.Id;
            _workspace = await _store.SaveEmployeeProfileAsync(expectedEmployee, profile.Name, profile.ColorArgb, token);
            var employee = _workspace.Employees.Single(e => e.Name.Equals(profile.Name.Trim(), StringComparison.OrdinalIgnoreCase));
            RefreshEmployees(actor); RefreshOrders();
            var expectedId = _localPreferences.EmployeeId;
            try
            {
                var savedPreferences = await Task.Run(() => _preferences.Update(current =>
                {
                    if (current.EmployeeId != expectedId && current.EmployeeId != employee.Id)
                        throw new WorkflowValidationException("На этом ПК выбран другой сотрудник. Откройте настройки заново; текущий ввод сохранён.");
                    return current with { EmployeeId = employee.Id };
                }), token);
                _localPreferences = _localPreferences with { EmployeeId = savedPreferences.EmployeeId };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException or WorkflowValidationException or OperationCanceledException)
            {
                throw new WorkflowValidationException("Сотрудник и цвет сохранены в БД. Выбор технолога на этом компьютере не сохранён: " +
                    (ex is WorkflowValidationException ? ex.Message : ex is OperationCanceledException ? "локальное сохранение отменено." :
                        "проверьте доступ к локальным настройкам и повторите сохранение."));
            }
            CurrentEmployee = employee; NotifyProfile(); NotifyEntrySuggestions(); saved = true;
        });
        if (!saved && Error is null) token.ThrowIfCancellationRequested();
        return saved ? null : Error ?? "Профиль не сохранён.";
    }
}

using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Jornal.Workflow.Core;

namespace Jornal.Workflow.Desktop;

public partial class WorkflowViewModel
{
    private WorkflowPreferencesStore _preferences;
    private WorkflowPreferences _localPreferences = new();
    private bool _loadingLocalPreferences;
    private bool _startupPreferencesPrepared;
    private bool _initialProfileReadFinished;
    private WorkflowStartupProfile? _startupProfile;
    [ObservableProperty] private bool _isDarkTheme;
    [ObservableProperty] private string? _themeError;
    public bool HasThemeError => !string.IsNullOrEmpty(ThemeError);
    partial void OnThemeErrorChanged(string? value) => OnPropertyChanged(nameof(HasThemeError));
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
    private Employee? ActiveEmployee => CurrentEmployee is { } current ? _workspace.Employees.SingleOrDefault(e => e.Id == current.Id && e.CanWork) : null;
    private WorkflowStartupProfile? PendingStartupProfile => !_hasLoadedOrders && _startupProfile?.Matches(_connection, _localPreferences) == true ? _startupProfile : null;
    public string ProfileName => ActiveEmployee?.Name ?? PendingStartupProfile?.Name ??
        (!_hasLoadedOrders && _connection.EmployeeId is not null ? _initialProfileReadFinished ? "Профиль недоступен" : "Загрузка профиля…" : "Профиль не выбран");
    public IBrush ProfileBrush => new SolidColorBrush(Color.FromUInt32(ActiveEmployee?.ColorArgb ?? PendingStartupProfile?.ColorArgb ?? 0xFF628DB2));
    public string ProfileHint => ProfileName + (PendingStartupProfile is not null ? " · сохранён на этом ПК, ожидает подтверждения из БД" : "") +
        " · изменить: Настройки → Общие → Технолог";
    partial void OnCurrentEmployeeChanged(Employee? value)
    { _connection = _connection with { EmployeeId = value?.Id }; NotifyProfile(); }
    private void NotifyProfile()
    { OnPropertyChanged(nameof(ProfileName)); OnPropertyChanged(nameof(ProfileBrush)); OnPropertyChanged(nameof(ProfileHint)); }

    public void LoadLocalPreferences(WorkflowPreferencesStore store)
    {
        ApplyLocalPreferences(store, store.Load());
    }
    private void ApplyLocalPreferences(WorkflowPreferencesStore store, WorkflowPreferences value)
    {
        _preferences = store; _localPreferences = value;
        NotifyShopHandoffColor();
        _loadingLocalPreferences = true;
        try { IsDarkTheme = value.DarkTheme; } finally { _loadingLocalPreferences = false; }
        ThemeError = null; NotifyEntrySuggestions();
        _connection.RequireMatchingSource(value);
        _connection = _connection with { EmployeeId = value.EmployeeId };
        NotifyProfile();
    }
    public void UseLocalPreferencesStore(WorkflowPreferencesStore store)
    {
        if (ReferenceEquals(_preferences, store)) return;
        _preferences = store; _startupPreferencesPrepared = false;
        _startupProfile = new WorkflowStartupProfileStore(store.ProfileCachePath).Load();
    }
    private async Task RememberStartupProfileAsync()
    {
        var source = _connection; var preferences = _preferences;
        var employee = ActiveEmployee;
        var snapshot = employee is null ? null : new WorkflowStartupProfile(1, _store.FilePath, _store.DatabaseId, employee.Id, employee.Name, employee.ColorArgb);
        if (snapshot == _startupProfile) return;
        var previous = _startupProfile;
        try
        {
            await Task.Run(() =>
            {
                var current = preferences.Load();
                if (!source.Matches(current) || source.EmployeeId != current.EmployeeId) return;
                var cache = new WorkflowStartupProfileStore(preferences.ProfileCachePath);
                if (snapshot is not null) cache.Save(snapshot);
                else if (previous?.Matches(source, current) == true) cache.Remove(previous);
            });
            if (_connection == source) _startupProfile = snapshot;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException or ArgumentException)
        { /* Optional local preview cannot turn a successful read/save into a failure. */ }
    }
    private WorkflowProfileDraft CaptureProfile() => ActiveEmployee is { } employee ?
        new(employee.Id, employee.Name, employee.ColorArgb ?? 0xFF628DB2) : new(Guid.Empty, "");

    private void RefreshEmployees(Guid? actorId)
    {
        var technologist = Technologist?.Id;
        Employees.Clear(); foreach (var e in _workspace.Employees) Employees.Add(e);
        CurrentEmployee = Employees.SingleOrDefault(e => e.Id == actorId && e.CanWork);
        // Remember the observed selection even if that employee was archived by a colleague.
        // This allows an explicit replacement without confusing it with another local selection.
        _connection = _connection with { EmployeeId = actorId };
        Technologist = Employees.SingleOrDefault(e => e.Id == technologist);
        NotifyProfile();
    }
    public async Task<string?> ArchiveTechnologistAsync(Employee expected, CancellationToken token = default)
    {
        if (!CanUse) return "Дождитесь завершения текущей операции.";
        var saved = false;
        await RunAsync(async () =>
        {
            var actor = ActorId();
            _workspace = await _store.ArchiveTechnologistAsync(expected, actor, token);
            RefreshEmployees(actor); RefreshOrders(preserveRows: true); NotifyOrder(); saved = true;
        }, "Не удалось подтвердить удаление из списка. Закройте настройки и обновите журнал перед повтором.");
        if (!saved && Error is null) token.ThrowIfCancellationRequested();
        return saved ? null : Error ?? "Не удалось убрать технолога из списка. Обновите журнал перед повтором.";
    }
    private async Task<string?> SaveProfileAsync(Employee? expectedEmployee, WorkflowProfileDraft profile, CancellationToken token)
    {
        if (!CanUse) return "Дождитесь завершения текущей операции.";
        var saved = false;
        await RunAsync(async () =>
        {
            _connection.RequireMatchingSource(_preferences.Load());
            var expectedId = _connection.EmployeeId;
            _workspace = await _store.SaveEmployeeProfileAsync(expectedEmployee, profile.Name, profile.ColorArgb, token);
            var employee = _workspace.Employees.Single(e => e.Name.Equals(profile.Name.Trim(), StringComparison.OrdinalIgnoreCase));
            RefreshEmployees(expectedId); RefreshOrders();
            try
            {
                var savedPreferences = await Task.Run(() => _preferences.Update(current =>
                {
                    _connection.RequireMatchingSource(current);
                    if (current.EmployeeId != expectedId && current.EmployeeId != employee.Id)
                        throw new WorkflowValidationException("На этом ПК выбран другой сотрудник. Откройте настройки заново; текущий ввод сохранён.");
                    if (_store.DatabaseId is null && current.DatabaseEmployees is null) return current with { EmployeeId = employee.Id };
                    var people = new Dictionary<string, Guid?>(current.DatabaseEmployees ?? []);
                    people[SourceKey] = employee.Id;
                    return current with { EmployeeId = employee.Id, DatabaseEmployees = people };
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
            await RememberStartupProfileAsync();
        });
        if (!saved && Error is null) token.ThrowIfCancellationRequested();
        return saved ? null : Error ?? "Профиль не сохранён.";
    }
}

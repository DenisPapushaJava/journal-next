using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jornal.Workflow.Core;
using Jornal.Desktop.ViewModels;
using System.ComponentModel;

namespace Jornal.Workflow.Desktop;

public partial class WorkflowSettingsViewModel : ObservableObject, IDisposable
{
    private readonly Func<WorkflowWorkspace> _snapshot;
    private readonly Func<string?, string?, CancellationToken, Task<string?>> _saveRoot;
    private readonly Func<WorkflowProfileDraft> _profile;
    private readonly Func<Employee?, WorkflowProfileDraft, CancellationToken, Task<string?>> _saveProfile;
    private readonly Func<WorkflowEntrySuggestions?> _suggestions;
    private readonly Func<WorkflowEntrySuggestions?, WorkflowEntrySuggestions, CancellationToken, Task<string?>> _saveSuggestions;
    private Employee[] _employees = [];
    private string? _expectedRoot;
    private string _originalRoot = "";
    private WorkflowProfileDraft _originalProfile;
    private bool _loading;
    private HsvColor _spectrum = new(Color.FromUInt32(0xFF628DB2));
    public string WorkspacePath => Journal.WorkspacePath;
    public string DatabaseDescription => Journal.DatabaseDescription;
    public bool CanChooseDatabase => CanEdit && Journal.CanConnectDatabase;
    public string DatabasePlatformHint => Journal.CanConnectDatabase ? "Создание новой базы не переносит тестовые заявки прототипа. Прежняя MDB подходит только для импорта цен." :
        "Работа с MDB доступна в Windows через модуль Access x86. Microsoft Access устанавливать не требуется.";
    public WorkflowViewModel Journal { get; }
    public AppUpdatesViewModel Updates => Journal.Updates;
    public bool CanClose => !IsBusy && !IsExporting && (!Updates.IsApplying || Updates.RestartScheduled);
    public bool CanRestart => !HasChanges && !IsBusy && !IsExporting;
    public IReadOnlyList<string> EmployeeSuggestions => _employees.Where(e => e.CanWork).Select(e => e.Name).Order().ToArray();
    [ObservableProperty] private string _profileName = "";
    [ObservableProperty] private string _projectRoot = "";
    [ObservableProperty] private int _selectedSectionIndex;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string? _status;
    public HsvColor ProfileSpectrumColor
    {
        get => _spectrum;
        set
        {
            if (!SetProperty(ref _spectrum, new HsvColor(1, value.H, value.S, value.V))) return;
            OnPropertyChanged(nameof(ProfileBrush)); OnPropertyChanged(nameof(ProfileColorHex)); OnPropertyChanged(nameof(ProfileColorName)); InputChanged();
            if (_loading) NotifyColorPicker();
        }
    }
    public uint ProfileColorArgb => _spectrum.ToRgb().ToUInt32();
    public IReadOnlyList<WorkflowProfileColorOption> FrequentColors { get; } = WorkflowProfileColors.Frequent();
    public IReadOnlyList<WorkflowProfileColorOption> OtherColors { get; } = WorkflowProfileColors.Other();
    public IBrush ProfileBrush => new SolidColorBrush(Color.FromUInt32(ProfileColorArgb));
    public string ProfileColorHex => "#" + (ProfileColorArgb & 0xFFFFFF).ToString("X6", System.Globalization.CultureInfo.InvariantCulture);
    public string ProfileColorName => FrequentColors.Concat(OtherColors).FirstOrDefault(c => c.Argb == ProfileColorArgb)?.Name ?? "Свой цвет";
    public bool HasRootChanges => ProjectRoot != _originalRoot;
    public bool HasProfileChanges => ProfileName != _originalProfile.Name || ProfileColorArgb != _originalProfile.ColorArgb ||
        (!_employees.Any(e => e.Id == _originalProfile.EmployeeId) && !string.IsNullOrWhiteSpace(ProfileName));
    public bool HasChanges => HasRootChanges || HasProfileChanges || HasShopRootChanges;
    public bool CanEdit => !IsBusy && !IsExporting && !Updates.IsApplying;
    public string CloseLabel => HasChanges ? "Отмена" : "Закрыть";
    public string SaveLabel => IsProfile ? "Сохранить профиль" : "Сохранить";
    public bool IsSaveVisible => IsProfile || IsStorage;
    public bool IsProfile => SelectedSectionIndex == 0;
    public bool IsStorage => SelectedSectionIndex == 1;
    public bool IsUpdates => SelectedSectionIndex == 2;
    public bool IsDiagnostics => SelectedSectionIndex == 3;
    public bool IsAbout => SelectedSectionIndex == 4;
    public string SectionTitle => SelectedSectionIndex switch
    { 0 => "Общие", 1 => "Хранилище", 2 => "Обновления", 3 => "Диагностика", _ => "О программе" };
    public bool HasError => !string.IsNullOrEmpty(Error);
    public bool HasStatus => !string.IsNullOrEmpty(Status);
    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(HasError));
    partial void OnStatusChanged(string? value) => OnPropertyChanged(nameof(HasStatus));
    public string EntrySuggestionsSummary
    {
        get { var value = _suggestions(); return $"Менеджеры: {value?.Managers.Length ?? 0} · Заказчики: {value?.Customers.Length ?? 0}"; }
    }
    public WorkflowEntrySuggestionsViewModel CreateEntrySuggestionsEditor() => new(_suggestions(), _saveSuggestions);
    public void RefreshEntrySuggestionsSummary() => OnPropertyChanged(nameof(EntrySuggestionsSummary));
    public Employee? FindTechnologist(string? name)
    {
        var matches = _employees.Where(e => e.CanWork && e.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }
    public bool CanRemoveTechnologist(string? name) => CanEdit && FindTechnologist(name) is { } employee &&
        employee.Id != Journal.CurrentEmployee?.Id && Journal.CurrentEmployee is not null;
    public async Task RemoveTechnologistAsync(Employee expected, CancellationToken token = default)
    {
        if (!CanRemoveTechnologist(expected.Name)) return;
        IsBusy = true; Error = null; Status = null;
        try
        {
            Error = await Journal.ArchiveTechnologistAsync(expected, token);
            if (Error is null)
            {
                _employees = _snapshot().Employees;
                if (ProfileName.Trim().Equals(expected.Name, StringComparison.OrdinalIgnoreCase))
                {
                    _loading = true; ProfileName = _originalProfile.Name;
                    ProfileSpectrumColor = new(Color.FromUInt32(_originalProfile.ColorArgb)); _loading = false;
                }
                OnPropertyChanged(nameof(EmployeeSuggestions)); Changed();
                Status = "Технолог убран из списка. Заявки и расчёты сохранены.";
            }
        }
        catch (OperationCanceledException) { Status = "Операция отменена. Обновите журнал перед повтором."; }
        finally { IsBusy = false; }
    }
    private bool CanSave => CanEdit && (IsProfile ? HasProfileChanges : IsStorage && (HasRootChanges || HasShopRootChanges));

    public WorkflowSettingsViewModel(string workspacePath, Func<WorkflowWorkspace> snapshot,
        Func<string?, string?, CancellationToken, Task<string?>> saveRoot, Func<WorkflowProfileDraft> profile,
        Func<Employee?, WorkflowProfileDraft, CancellationToken, Task<string?>> saveProfile,
        Func<WorkflowEntrySuggestions?> suggestions,
        Func<WorkflowEntrySuggestions?, WorkflowEntrySuggestions, CancellationToken, Task<string?>> saveSuggestions, WorkflowViewModel journal)
    {
        _snapshot = snapshot; _saveRoot = saveRoot; _profile = profile; _saveProfile = saveProfile;
        _suggestions = suggestions; _saveSuggestions = saveSuggestions;
        Journal = journal; Updates.PropertyChanged += OnUpdatesChanged; Journal.PropertyChanged += OnJournalAvailabilityChanged;
        _originalProfile = profile(); ReloadRoot(); ReloadProfile(); ReloadShopRoot(); ReloadResultPricing();
    }
    private void ReloadRoot()
    {
        _loading = true; _expectedRoot = _snapshot().ProjectRoot; ProjectRoot = _originalRoot = _expectedRoot ?? "";
        _loading = false; Changed();
    }
    private void ReloadProfile()
    {
        _loading = true; _employees = _snapshot().Employees; _originalProfile = _profile(); ProfileName = _originalProfile.Name;
        ProfileSpectrumColor = new(Color.FromUInt32(_originalProfile.ColorArgb)); _loading = false; Changed();
        OnPropertyChanged(nameof(EmployeeSuggestions));
    }
    partial void OnProjectRootChanged(string value) => InputChanged();
    partial void OnProfileNameChanged(string? oldValue, string newValue)
    {
        if (!_loading)
        {
            var matches = _employees.Where(e => e.Name.Equals(newValue?.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length == 1) ProfileSpectrumColor = new(Color.FromUInt32(matches[0].ColorArgb ?? 0xFF628DB2));
        }
        InputChanged();
    }
    partial void OnSelectedSectionIndexChanged(int value)
    {
        NormalizeResultPriceInput();
        foreach (var name in new[] { nameof(SaveLabel), nameof(IsSaveVisible), nameof(IsProfile), nameof(IsStorage), nameof(IsUpdates), nameof(IsDiagnostics), nameof(IsAbout), nameof(SectionTitle) }) OnPropertyChanged(name);
        SaveCommand.NotifyCanExecuteChanged(); Error = null; Status = null;
    }
    private void InputChanged() { if (!_loading) { Status = null; Error = null; Changed(); } }
    private void Changed()
    {
        OnPropertyChanged(nameof(HasChanges)); OnPropertyChanged(nameof(HasShopRootChanges)); OnPropertyChanged(nameof(HasRootChanges)); OnPropertyChanged(nameof(HasProfileChanges));
        OnPropertyChanged(nameof(CloseLabel)); SaveCommand.NotifyCanExecuteChanged();
        NotifyColorPicker();
    }
    [RelayCommand] private void SelectProfileColor(WorkflowProfileColorOption? color)
    { if (CanEdit && color is not null) ProfileSpectrumColor = new(Color.FromUInt32(color.Argb)); }
    private void NotifyAvailability()
    { OnPropertyChanged(nameof(CanChooseDatabase)); OnPropertyChanged(nameof(CanEdit)); OnPropertyChanged(nameof(CanClose)); OnPropertyChanged(nameof(CanRestart)); OnPropertyChanged(nameof(CanExportDiagnostics)); SaveCommand.NotifyCanExecuteChanged(); }
    public async Task ChooseDatabaseAsync(string path, bool create, CancellationToken token = default)
    {
        if (!CanChooseDatabase) return;
        if (HasChanges) { Error = "Сохраните изменения настроек перед сменой базы или закройте настройки кнопкой «Отмена»."; return; }
        IsBusy = true; Error = null; Status = null;
        try
        {
            Error = await Journal.ConnectDatabaseAsync(path, create, token);
            if (Error is null)
            {
                ReloadRoot(); ReloadProfile();
                OnPropertyChanged(nameof(WorkspacePath)); OnPropertyChanged(nameof(DatabaseDescription));
                Status = (create ? "База создана и подключена." : "База подключена.") +
                    (Journal.CurrentEmployee is null ? " Выберите себя в разделе «Общие → Технолог»." : "");
            }
        }
        finally { IsBusy = false; }
    }
    private void OnUpdatesChanged(object? sender, PropertyChangedEventArgs e)
    { if (e.PropertyName is nameof(AppUpdatesViewModel.IsApplying) or nameof(AppUpdatesViewModel.RestartScheduled)) NotifyAvailability(); }
    partial void OnIsBusyChanged(bool value) => NotifyAvailability();
    private void OnJournalAvailabilityChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkflowViewModel.CanUse)) NotifyAvailability();
        if (e.PropertyName is nameof(WorkflowViewModel.OrderTitle) or nameof(WorkflowViewModel.WorkspacePath)) OnPropertyChanged(nameof(ShopFolderPreview));
    }
    public void Dispose() { Updates.PropertyChanged -= OnUpdatesChanged; Journal.PropertyChanged -= OnJournalAvailabilityChanged; }
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync(CancellationToken token)
    {
        IsBusy = true; Error = null;
        try
        {
            if (IsProfile)
            {
                var matches = _employees.Where(e => e.Name.Equals(ProfileName?.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
                Error = await _saveProfile(matches.Length == 1 ? matches[0] : null, new(_originalProfile.EmployeeId, ProfileName, ProfileColorArgb), token);
                if (Error is null) { ReloadProfile(); Status = "Цвет сохранён в БД. Технолог выбран на этом компьютере."; }
            }
            else if (IsStorage)
            {
                await SaveStorageAsync(token);
            }
        }
        catch (OperationCanceledException) { Status = "Сохранение отменено."; }
        catch (Exception ex) when (ex is ArgumentException or WorkflowValidationException or NotSupportedException or PathTooLongException)
        { Error = ex is WorkflowValidationException || IsProfile && ex is ArgumentException ? ex.Message : "Проверьте полные пути к папкам."; }
        finally { IsBusy = false; }
    }
}

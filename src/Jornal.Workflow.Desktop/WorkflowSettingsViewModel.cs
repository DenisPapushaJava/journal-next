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
    public string WorkspacePath { get; }
    public WorkflowViewModel Journal { get; }
    public AppUpdatesViewModel Updates => Journal.Updates;
    public bool CanClose => !IsBusy && !IsExporting && (!Updates.IsApplying || Updates.RestartScheduled);
    public bool CanRestart => !HasChanges && !IsBusy && !IsExporting;
    public string PriceSourcePath => _snapshot().PriceSource?.Path ?? "Источник ещё не выбран";
    public string PriceSourceStatus => _snapshot().PriceSource is { } source ?
        $"Цены загружены {source.LoadedAt.ToLocalTime():dd.MM.yyyy HH:mm}" : "Прежние цены ещё не загружены";
    public IReadOnlyList<string> EmployeeSuggestions => _employees.Select(e => e.Name).Order().ToArray();
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
    public bool HasChanges => HasRootChanges || HasProfileChanges;
    public bool CanEdit => !IsBusy && !IsExporting && !Updates.IsApplying;
    public string CloseLabel => HasChanges ? "Отмена" : "Закрыть";
    public string SaveLabel => IsProfile ? "Сохранить профиль" : "Сохранить папку";
    public bool IsSaveVisible => !IsProgram;
    public bool IsProfile => SelectedSectionIndex == 0;
    public bool IsStorage => SelectedSectionIndex == 1;
    public bool IsProgram => SelectedSectionIndex == 2;
    public string SectionTitle => IsProfile ? "Общие" : IsStorage ? "Хранилище" : "Программа";
    public string EntrySuggestionsSummary
    {
        get { var value = _suggestions(); return $"Менеджеры: {value?.Managers.Length ?? 0} · Заказчики: {value?.Customers.Length ?? 0}"; }
    }
    public WorkflowEntrySuggestionsViewModel CreateEntrySuggestionsEditor() => new(_suggestions(), _saveSuggestions);
    public void RefreshEntrySuggestionsSummary() => OnPropertyChanged(nameof(EntrySuggestionsSummary));
    private bool CanSave => CanEdit && (IsProfile ? HasProfileChanges : IsStorage && HasRootChanges);

    public WorkflowSettingsViewModel(string workspacePath, Func<WorkflowWorkspace> snapshot,
        Func<string?, string?, CancellationToken, Task<string?>> saveRoot, Func<WorkflowProfileDraft> profile,
        Func<Employee?, WorkflowProfileDraft, CancellationToken, Task<string?>> saveProfile,
        Func<WorkflowEntrySuggestions?> suggestions,
        Func<WorkflowEntrySuggestions?, WorkflowEntrySuggestions, CancellationToken, Task<string?>> saveSuggestions, WorkflowViewModel journal)
    {
        WorkspacePath = workspacePath; _snapshot = snapshot; _saveRoot = saveRoot; _profile = profile; _saveProfile = saveProfile;
        _suggestions = suggestions; _saveSuggestions = saveSuggestions;
        Journal = journal; Updates.PropertyChanged += OnUpdatesChanged;
        _originalProfile = profile(); ReloadRoot(); ReloadProfile();
    }
    private void ReloadRoot()
    {
        _loading = true; _expectedRoot = _snapshot().ProjectRoot; ProjectRoot = _originalRoot = _expectedRoot ?? "";
        _loading = false; Changed();
        OnPropertyChanged(nameof(PriceSourcePath)); OnPropertyChanged(nameof(PriceSourceStatus));
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
        foreach (var name in new[] { nameof(SaveLabel), nameof(IsSaveVisible), nameof(IsProfile), nameof(IsStorage), nameof(IsProgram), nameof(SectionTitle) }) OnPropertyChanged(name);
        SaveCommand.NotifyCanExecuteChanged(); Error = null; Status = null;
    }
    private void InputChanged() { if (!_loading) { Status = null; Error = null; Changed(); } }
    private void Changed()
    {
        OnPropertyChanged(nameof(HasChanges)); OnPropertyChanged(nameof(HasRootChanges)); OnPropertyChanged(nameof(HasProfileChanges));
        OnPropertyChanged(nameof(CloseLabel)); SaveCommand.NotifyCanExecuteChanged();
        foreach (var color in FrequentColors.Concat(OtherColors)) color.IsSelected = color.Argb == ProfileColorArgb;
    }
    [RelayCommand] private void SelectProfileColor(WorkflowProfileColorOption? color)
    { if (CanEdit && color is not null) ProfileSpectrumColor = new(Color.FromUInt32(color.Argb)); }
    private void NotifyAvailability()
    { OnPropertyChanged(nameof(CanEdit)); OnPropertyChanged(nameof(CanClose)); OnPropertyChanged(nameof(CanRestart)); OnPropertyChanged(nameof(CanExportDiagnostics)); SaveCommand.NotifyCanExecuteChanged(); }
    private void OnUpdatesChanged(object? sender, PropertyChangedEventArgs e)
    { if (e.PropertyName is nameof(AppUpdatesViewModel.IsApplying) or nameof(AppUpdatesViewModel.RestartScheduled)) NotifyAvailability(); }
    partial void OnIsBusyChanged(bool value) => NotifyAvailability();
    public void Dispose() => Updates.PropertyChanged -= OnUpdatesChanged;
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
                Error = await _saveRoot(_expectedRoot, ProjectRoot, token);
                if (Error is null) { ReloadRoot(); Status = "Папка проектов сохранена."; }
            }
        }
        catch (OperationCanceledException) { Status = "Сохранение отменено."; }
        catch (ArgumentException ex) { Error = ex.Message; }
        finally { IsBusy = false; }
    }
}

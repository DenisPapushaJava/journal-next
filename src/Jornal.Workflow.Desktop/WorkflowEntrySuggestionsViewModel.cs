using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Jornal.Workflow.Desktop;

public partial class WorkflowEntrySuggestionsViewModel : ObservableObject
{
    private readonly Func<WorkflowEntrySuggestions?, WorkflowEntrySuggestions, CancellationToken, Task<string?>> _save;
    private WorkflowEntrySuggestions? _expected;
    private string _originalManagers = "", _originalCustomers = "", _originalMaterials = "";
    [ObservableProperty] private string _managersText = "";
    [ObservableProperty] private string _customersText = "";
    [ObservableProperty] private string _materialsText = "";
    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _isBusy;
    public bool CanEdit => !IsBusy;
    public bool HasChanges => ManagersText != _originalManagers || CustomersText != _originalCustomers || MaterialsText != _originalMaterials;
    public bool WasSaved { get; private set; }
    public event EventHandler? Saved;
    private bool CanSave => CanEdit && HasChanges;
    public WorkflowEntrySuggestionsViewModel(WorkflowEntrySuggestions? current,
        Func<WorkflowEntrySuggestions?, WorkflowEntrySuggestions, CancellationToken, Task<string?>> save)
    { _save = save; Load(current); }
    private void Load(WorkflowEntrySuggestions? value)
    {
        _expected = value?.Normalize();
        _originalManagers = string.Join(Environment.NewLine, _expected?.Managers ?? []);
        _originalCustomers = string.Join(Environment.NewLine, _expected?.Customers ?? []);
        _originalMaterials = string.Join(Environment.NewLine, _expected?.Materials ?? []);
        ManagersText = _originalManagers; CustomersText = _originalCustomers; MaterialsText = _originalMaterials; Changed();
    }
    private void Changed()
    { Error = null; OnPropertyChanged(nameof(HasChanges)); SaveCommand.NotifyCanExecuteChanged(); }
    partial void OnManagersTextChanged(string value) => Changed();
    partial void OnCustomersTextChanged(string value) => Changed();
    partial void OnMaterialsTextChanged(string value) => Changed();
    partial void OnIsBusyChanged(bool value)
    { OnPropertyChanged(nameof(CanEdit)); SaveCommand.NotifyCanExecuteChanged(); }
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync(CancellationToken token)
    {
        IsBusy = true; Error = null; var saved = false;
        try
        {
            var value = WorkflowEntrySuggestions.FromText(ManagersText, CustomersText, MaterialsText);
            Error = await _save(_expected, value, token);
            if (Error is null) { Load(value); WasSaved = true; saved = true; }
        }
        catch (ArgumentException ex) { Error = ex.Message; }
        catch (OperationCanceledException) { Error = "Сохранение отменено. Ваш ввод остался в полях."; }
        finally { IsBusy = false; }
        if (saved) Saved?.Invoke(this, EventArgs.Empty);
    }
}

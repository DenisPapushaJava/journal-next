using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jornal.Core.Orders;
using Jornal.Core.Pricing;
using Jornal.Infrastructure.Diagnostics;

namespace Jornal.Desktop.ViewModels;

public sealed partial class MachineImportViewModel : ObservableObject
{
    private readonly IMachineImportEditor _editor;
    private readonly IMachineCatalogReader _catalog;
    private CuttingMachine _expected;
    private MachineImportChange? _attempt;
    public MachineImportViewModel(CuttingMachine machine, IMachineImportEditor editor, IMachineCatalogReader catalog)
    {
        machine.Validate(); _expected = machine; _editor = editor; _catalog = catalog;
        _profile = Profiles.Single(choice => choice.Id == machine.SpecificationProfileId);
    }
    public string Name => _expected.Name;
    public IReadOnlyList<MachineProfileChoice> Profiles => MachineProfileChoice.Available;
    [ObservableProperty] private MachineProfileChoice _profile;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError))] private string? _error;
    [ObservableProperty] private bool _isSaving;
    [ObservableProperty] private bool _isReading;
    [ObservableProperty] private bool _requiresReload;
    [ObservableProperty] private bool _confirmDiscard;
    public bool HasError => !string.IsNullOrEmpty(Error);
    public bool IsBusy => IsSaving || IsReading;
    public bool CanEdit => !IsBusy && !RequiresReload && SavedMachine is null;
    public bool HasChanges => Profile is not null && Profile.Id != _expected.SpecificationProfileId;
    public bool CanSave => CanEdit && HasChanges && Profiles.Contains(Profile);
    public CuttingMachine? SavedMachine { get; private set; }
    public event EventHandler? Saved;
    public event EventHandler? SavingChanged;
    partial void OnProfileChanged(MachineProfileChoice value) { Error = null; Notify(); }
    partial void OnIsSavingChanged(bool value) { Notify(); SavingChanged?.Invoke(this, EventArgs.Empty); }
    partial void OnIsReadingChanged(bool value) => Notify();
    partial void OnRequiresReloadChanged(bool value) => Notify();
    private void Notify()
    {
        foreach (var property in new[] { nameof(IsBusy), nameof(CanEdit), nameof(HasChanges), nameof(CanSave) }) OnPropertyChanged(property);
        SaveCommand.NotifyCanExecuteChanged(); ReloadCommand.NotifyCanExecuteChanged();
    }
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (!CanSave) return;
        Error = null; IsSaving = true;
        try
        {
            _attempt = new MachineImportChange(_expected, Profile.Id).Snapshot();
            var saved = await _editor.UpdateMachineImportAsync(_attempt);
            if (saved != _attempt.Values) throw new OrderWriteException(OrderWriteFailure.OutcomeUnknown, "Сохранение не подтверждено. Обновите станок.");
            SavedMachine = saved;
        }
        catch (Exception exception)
        {
            RequiresReload = exception is OrderWriteException { Failure: OrderWriteFailure.Conflict or OrderWriteFailure.OutcomeUnknown };
            Error = AppDiagnostics.Describe(DiagnosticOperation.AccessHost, exception, "Не удалось изменить импорт спецификаций.");
        }
        finally { IsSaving = false; }
        if (SavedMachine is not null) Saved?.Invoke(this, EventArgs.Empty);
    }
    private bool CanReload => !IsBusy && SavedMachine is null;
    [RelayCommand(CanExecute = nameof(CanReload))]
    private async Task ReloadAsync(CancellationToken cancellationToken)
    {
        Error = null; IsReading = true;
        try
        {
            var machines = CuttingMachine.Snapshot(await _catalog.ReadMachinesAsync(cancellationToken));
            var current = machines.SingleOrDefault(machine => machine.TableName == _expected.TableName);
            if (current is null || current.Name != _expected.Name) throw new PriceReadException("Станок недоступен. Закройте окно и обновите прайсы.");
            if (_attempt is { } attempt && current == attempt.Values)
            { SavedMachine = current; Saved?.Invoke(this, EventArgs.Empty); return; }
            _expected = current; RequiresReload = false; Notify();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) { Error = AppDiagnostics.Describe(DiagnosticOperation.PriceRead, exception, "Не удалось обновить станок."); }
        finally { IsReading = false; }
    }
}

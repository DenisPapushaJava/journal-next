using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jornal.Core.Orders;
using Jornal.Core.Pricing;
using Jornal.Infrastructure.Diagnostics;

namespace Jornal.Desktop.ViewModels;

public sealed record MachineProfileChoice(string Name, string? Id)
{
    public static IReadOnlyList<MachineProfileChoice> Available { get; } = Array.AsReadOnly<MachineProfileChoice>(
        [new("Без импорта", null), new("Vintech · HTML", CuttingMachine.VintechProfile),
         new("HansLaser · PDF", CuttingMachine.HansProfile), new("Гидроабразив AWJC · PDF", CuttingMachine.WaterjetProfile)]);
}
public sealed partial class NewMachineViewModel(IMachineCreator creator, IMachineCatalogReader catalog,
    IReadOnlyList<CuttingMachine> machines, PriceStorageKind storage) : ObservableObject
{
    private readonly string _tableName = "PriceMachine_" + Guid.NewGuid().ToString("N");
    private CuttingMachine[] _expected = CuttingMachine.Snapshot(machines);
    private MachineCreation? _attempt;
    public ObservableCollection<PriceRowViewModel> Rates { get; } = [];
    public IReadOnlyList<MachineProfileChoice> Profiles => MachineProfileChoice.Available;
    [ObservableProperty] private MachineProfileChoice _profile = MachineProfileChoice.Available[0];
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _thickness = "";
    [ObservableProperty] private string _rate = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError))] private string? _error;
    [ObservableProperty] private bool _isSaving;
    [ObservableProperty] private bool _isReading;
    [ObservableProperty] private bool _requiresReload;
    [ObservableProperty] private bool _confirmDiscard;
    public bool HasError => !string.IsNullOrEmpty(Error);
    public bool IsBusy => IsSaving || IsReading;
    public bool CanEdit => !IsBusy && !RequiresReload;
    public bool ShowDatabaseNotice => storage == PriceStorageKind.SharedDatabase;
    public bool HasChanges => Name.Length > 0 || Rates.Count > 0 || Thickness.Length > 0 || Rate.Length > 0 || Profile != Profiles[0];
    public bool CanSave => CreatedMachine is null && CanEdit && Profiles.Contains(Profile) && !string.IsNullOrWhiteSpace(Name) && Rates.Count > 0 && Rates.All(row => !row.HasError) && Thickness.Length == 0 && Rate.Length == 0;
    public CuttingMachine? CreatedMachine { get; private set; }
    public event EventHandler? Saved;
    public event EventHandler? SavingChanged;
    partial void OnNameChanged(string value) { Error = null; Notify(); }
    partial void OnProfileChanged(MachineProfileChoice value) { Error = null; Notify(); }
    partial void OnThicknessChanged(string value) { Error = null; Notify(); }
    partial void OnRateChanged(string value) { Error = null; Notify(); }
    partial void OnIsSavingChanged(bool value) { Notify(); SavingChanged?.Invoke(this, EventArgs.Empty); }
    partial void OnIsReadingChanged(bool value) => Notify();
    partial void OnRequiresReloadChanged(bool value) => Notify();
    private void Notify()
    {
        foreach (var name in new[] { nameof(IsBusy), nameof(CanEdit), nameof(CanSave), nameof(HasChanges) }) OnPropertyChanged(name);
        AddRateCommand.NotifyCanExecuteChanged(); SaveCommand.NotifyCanExecuteChanged(); ReloadCommand.NotifyCanExecuteChanged();
    }
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void AddRate()
    {
        if (!decimal.TryParse(Thickness.Trim().Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var thickness) || thickness <= 0 ||
            !int.TryParse(Rate.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var rate))
        { Error = "Укажите положительную толщину и целую неотрицательную цену."; return; }
        if (Rates.Count >= 254) { Error = "В одном прайсе может быть не больше 254 толщин."; return; }
        if (Rates.Any(row => row.Original.ThicknessMillimeters == thickness)) { Error = "Такая толщина уже есть."; return; }
        var row = new PriceRowViewModel(new(thickness, PriceTableChange.ColumnName(thickness), rate), true);
        row.PropertyChanged += (_, _) => Notify();
        var index = Rates.TakeWhile(value => value.Original.ThicknessMillimeters < thickness).Count(); Rates.Insert(index, row);
        Thickness = ""; Rate = ""; Error = null; Notify();
    }
    public void RemoveRate(PriceRowViewModel row) { if (CanEdit) { Rates.Remove(row); Notify(); } }
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (!CanSave) return;
        Error = null;
        try
        {
            var machine = new CuttingMachine(Name.Trim(), _tableName, Profile.Id);
            _attempt = new MachineCreation(machine, Rates.Select(row => row.Original with { RublesPerMeter = row.TryGetValue(out var value) ? value : null }).ToArray(), _expected).Snapshot();
            IsSaving = true;
            var created = await creator.CreateMachineAsync(_attempt);
            if (created != machine) throw new OrderWriteException(OrderWriteFailure.OutcomeUnknown, "Станок не подтверждён. Обновите каталог.");
            CreatedMachine = created;
        }
        catch (Exception exception)
        {
            RequiresReload = exception is OrderWriteException { Failure: OrderWriteFailure.Conflict or OrderWriteFailure.OutcomeUnknown };
            Error = AppDiagnostics.Describe(DiagnosticOperation.AccessHost, exception, "Не удалось добавить станок.");
        }
        finally { IsSaving = false; }
        if (CreatedMachine is not null) Saved?.Invoke(this, EventArgs.Empty);
    }
    private bool CanReload => !IsBusy;
    [RelayCommand(CanExecute = nameof(CanReload))]
    private async Task ReloadAsync(CancellationToken cancellationToken)
    {
        IsReading = true; Error = null;
        try
        {
            var current = CuttingMachine.Snapshot(await catalog.ReadMachinesAsync(cancellationToken));
            if (_attempt is { } attempt && current.SingleOrDefault(machine => machine.TableName == _tableName) is { } found)
            {
                if (found != attempt.Machine) throw new PriceReadException("Привязка станка отличается от введённой. Повторное создание остановлено.");
                if (catalog is not IPriceReader prices || !PriceTableChange.Matches(await prices.ReadAsync(found.TableName, cancellationToken), new(found.TableName, true, attempt.Rates)))
                    throw new PriceReadException("Тарифы отличаются от введённых. Проверьте прайс; повторное создание остановлено.");
                CreatedMachine = found; Saved?.Invoke(this, EventArgs.Empty); return;
            }
            _expected = current; RequiresReload = false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) { Error = AppDiagnostics.Describe(DiagnosticOperation.PriceRead, exception, "Не удалось обновить каталог станков."); }
        finally { IsReading = false; }
    }
}

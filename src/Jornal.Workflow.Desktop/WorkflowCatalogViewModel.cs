using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jornal.Workflow.Core;

namespace Jornal.Workflow.Desktop;

public sealed record WorkflowImportOption(string Title, string? Profile)
{
    public static IReadOnlyList<WorkflowImportOption> All { get; } =
    [new("Ручной ввод", null), new("Vintech · HTML", "block-html/v1"), new("HansLaser · PDF", "hans-pdf/v1"), new("Гидроабразив · AWJC PDF", "waterjet-awjc/v1")];
}

public partial class WorkflowRateEditor : ObservableObject
{
    private readonly Action _changed;
    private readonly (string Group, string Thickness, string Meter, string Pierce)? _original;
    public Guid Id { get; }
    [ObservableProperty] private string _materialGroup;
    [ObservableProperty] private string _thickness;
    [ObservableProperty] private string _pricePerMeter;
    [ObservableProperty] private string _pricePerPierce;
    [ObservableProperty] private bool _usesMaterialCategories;
    public bool HasChanges => _original != (MaterialGroup, Thickness, PricePerMeter, PricePerPierce);
    public WorkflowRateEditor(CuttingRate? rate, Action changed, bool usesMaterialCategories = false)
    {
        _changed = changed; Id = rate?.Id ?? Guid.NewGuid();
        _usesMaterialCategories = usesMaterialCategories;
        _materialGroup = rate?.MaterialGroup ?? "Базовая"; _thickness = rate is null ? "" : SpecificationDraft.N(rate.ThicknessMm);
        _pricePerMeter = rate is null ? "" : SpecificationDraft.N(rate.PricePerMeter);
        _pricePerPierce = rate is null ? "1" : rate.PricePerPierce is { } p ? SpecificationDraft.N(p) : "";
        if (rate is not null) _original = (_materialGroup, _thickness, _pricePerMeter, _pricePerPierce);
    }
    protected override void OnPropertyChanged(PropertyChangedEventArgs e) { base.OnPropertyChanged(e); _changed(); }
    public CuttingRate ToRate(WorkflowMachine machine) => new(Id, machine.Name, MaterialGroup?.Trim() ?? "",
        SpecificationDraft.Decimal(Thickness, "толщину"), SpecificationDraft.Decimal(PricePerMeter, "цену метра"),
        SpecificationDraft.Decimal(PricePerPierce, "цену пробивки")) { MachineId = machine.Id };
}

public partial class WorkflowMachineEditor : ObservableObject
{
    private readonly WorkflowMachine _original;
    private readonly Guid[] _originalRates;
    private readonly Action _changed;
    public Guid Id => _original.Id;
    public bool IsNew { get; }
    public ObservableCollection<WorkflowRateEditor> Rates { get; } = [];
    [ObservableProperty] private string _name;
    [ObservableProperty] private WorkflowImportOption _import;
    [ObservableProperty] private string _projectExtension;
    public bool HasChanges => IsNew || Name != _original.Name || Import?.Profile != _original.ImportProfile ||
        ProjectExtension != WorkflowProjectNames.Extension(_original) ||
        !_originalRates.SequenceEqual(Rates.Select(r => r.Id)) || Rates.Any(r => r.HasChanges);
    public WorkflowMachineEditor(WorkflowMachine machine, IEnumerable<CuttingRate> rates, Action changed, bool isNew = false)
    {
        _original = machine; _name = machine.Name; IsNew = isNew; _changed = changed;
        _import = WorkflowImportOption.All.Single(p => p.Profile == machine.ImportProfile);
        _projectExtension = WorkflowProjectNames.Extension(machine);
        foreach (var rate in rates.OrderBy(r => r.MaterialGroup).ThenBy(r => r.ThicknessMm)) Rates.Add(new(rate, changed, machine.ImportProfile == "hans-pdf/v1"));
        _originalRates = Rates.Select(r => r.Id).ToArray();
        Rates.CollectionChanged += (_, _) => _changed();
    }
    protected override void OnPropertyChanged(PropertyChangedEventArgs e) { base.OnPropertyChanged(e); _changed(); }
    partial void OnImportChanged(WorkflowImportOption? oldValue, WorkflowImportOption newValue)
    {
        if (oldValue is null) return;
        foreach (var rate in Rates) rate.UsesMaterialCategories = newValue.Profile == "hans-pdf/v1";
        if (ProjectExtension == WorkflowProjectNames.Extension(_original with { ImportProfile = oldValue.Profile, ProjectExtension = null }))
            ProjectExtension = WorkflowProjectNames.Extension(_original with { ImportProfile = newValue.Profile, ProjectExtension = null });
    }
    public WorkflowMachine ToMachine() => _original with { Name = Name?.Trim() ?? "", ImportProfile = Import?.Profile,
        ProjectExtension = ProjectExtension?.Trim() };
}

public partial class WorkflowCatalogViewModel : ObservableObject
{
    private readonly Func<WorkflowWorkspace> _snapshot;
    private readonly Func<long, WorkflowMachine[], CuttingRate[], CancellationToken, Task<string?>> _save;
    private long _version;
    private bool _loading;
    private Guid[] _originalMachineIds = [];
    private readonly Func<Guid, string?> _removalReason;
    public ObservableCollection<WorkflowMachineEditor> Machines { get; } = [];
    [ObservableProperty] private WorkflowMachineEditor? _selectedMachine;
    [ObservableProperty] private WorkflowRateEditor? _selectedRate;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string? _status;
    public IReadOnlyList<WorkflowImportOption> Imports => WorkflowImportOption.All;
    public ObservableCollection<WorkflowRateEditor>? Rates => SelectedMachine?.Rates;
    public bool HasChanges => !_originalMachineIds.SequenceEqual(Machines.Select(m => m.Id)) || Machines.Any(m => m.HasChanges);
    public string? RemovalReason => SelectedMachine is { } machine ? _removalReason(machine.Id) : null;
    public bool HasRemovalReason => RemovalReason is not null;
    private bool CanRemoveMachine => CanEdit && SelectedMachine is not null && RemovalReason is null;
    public string CloseLabel => HasChanges ? "Отмена" : "Закрыть";
    public bool CanEdit => !IsBusy;
    private bool CanSave => !IsBusy && HasChanges;
    private bool CanAddRate => !IsBusy && SelectedMachine is not null;
    private bool CanRemoveRate => CanAddRate && SelectedRate is not null;

    public WorkflowCatalogViewModel(Func<WorkflowWorkspace> snapshot,
        Func<long, WorkflowMachine[], CuttingRate[], CancellationToken, Task<string?>> save, Func<Guid, string?>? removalReason = null)
    { _snapshot = snapshot; _save = save; _removalReason = removalReason ?? (id => WorkflowChanges.MachineRemovalReason(snapshot(), id)); Reload(); }

    public void Reload()
    {
        if (IsBusy) return;
        var selected = SelectedMachine?.Id; var book = _snapshot(); _version = book.Version; _loading = true;
        Machines.Clear();
        foreach (var machine in book.Machines) Machines.Add(new(machine, book.Rates.Where(r => r.MachineId == machine.Id), Changed));
        _originalMachineIds = Machines.Select(m => m.Id).ToArray();
        SelectedMachine = Machines.SingleOrDefault(m => m.Id == selected) ?? Machines.FirstOrDefault();
        _loading = false; Changed();
    }
    public void SelectTariff(Guid machineId, string group, decimal thickness)
    {
        SelectedMachine = Machines.SingleOrDefault(m => m.Id == machineId);
        if (SelectedMachine is null) return;
        SelectedRate = SelectedMachine.Rates.FirstOrDefault(r => r.MaterialGroup == group &&
            decimal.TryParse(r.Thickness.Replace(',', '.'), System.Globalization.CultureInfo.InvariantCulture, out var value) && value == thickness);
        if (SelectedRate is not null) return;
        AddRate(); SelectedRate!.MaterialGroup = group; SelectedRate.Thickness = SpecificationDraft.N(thickness);
    }
    private void Changed()
    {
        if (_loading) return;
        OnPropertyChanged(nameof(HasChanges)); OnPropertyChanged(nameof(CloseLabel)); SaveCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(RemovalReason)); OnPropertyChanged(nameof(HasRemovalReason)); RemoveMachineCommand.NotifyCanExecuteChanged();
        if (HasChanges) Status = null;
    }
    partial void OnSelectedMachineChanged(WorkflowMachineEditor? value)
    { OnPropertyChanged(nameof(Rates)); SelectedRate = value?.Rates.FirstOrDefault(); AddRateCommand.NotifyCanExecuteChanged(); RemoveRateCommand.NotifyCanExecuteChanged(); Changed(); }
    partial void OnSelectedRateChanged(WorkflowRateEditor? value) => RemoveRateCommand.NotifyCanExecuteChanged();
    partial void OnIsBusyChanged(bool value)
    { OnPropertyChanged(nameof(CanEdit)); SaveCommand.NotifyCanExecuteChanged(); AddRateCommand.NotifyCanExecuteChanged(); RemoveRateCommand.NotifyCanExecuteChanged(); RemoveMachineCommand.NotifyCanExecuteChanged(); }

    [RelayCommand(CanExecute = nameof(CanRemoveMachine))] private void RemoveMachine()
    {
        if (!CanRemoveMachine || SelectedMachine is not { } machine) return;
        Machines.Remove(machine); SelectedMachine = Machines.FirstOrDefault(); Changed(); Error = null;
        Status = machine.IsNew ? "Несохранённый станок убран." : "Станок «" + machine.Name + "» будет удалён после сохранения.";
    }

    [RelayCommand] private void AddMachine()
    {
        if (IsBusy) return;
        var name = "Новый станок"; var index = 2;
        while (Machines.Any(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase))) name = "Новый станок " + index++;
        var machine = new WorkflowMachineEditor(new(name, null) { Id = Guid.NewGuid() }, [], Changed, true);
        Machines.Add(machine); SelectedMachine = machine; Changed(); Error = null;
    }
    [RelayCommand(CanExecute = nameof(CanAddRate))] private void AddRate()
    {
        if (SelectedMachine is not { } machine) return;
        var rate = new WorkflowRateEditor(null, Changed, machine.Import.Profile == "hans-pdf/v1"); machine.Rates.Add(rate); SelectedRate = rate; Error = null;
    }
    [RelayCommand(CanExecute = nameof(CanRemoveRate))] private void RemoveRate()
    {
        if (SelectedMachine is not { } machine || SelectedRate is not { } rate) return;
        machine.Rates.Remove(rate); SelectedRate = machine.Rates.FirstOrDefault(); Error = null;
    }
    [RelayCommand(CanExecute = nameof(CanSave))] private async Task SaveAsync(CancellationToken token)
    {
        IsBusy = true; Error = null;
        try
        {
            var machines = Machines.Select(m => m.ToMachine()).ToArray();
            var rates = Machines.SelectMany(m => m.Rates.Select(r => r.ToRate(m.ToMachine()))).ToArray();
            Error = await _save(_version, machines, rates, token);
            if (Error is null) { IsBusy = false; Reload(); Status = "Станки и цены сохранены."; }
        }
        catch (OperationCanceledException) { Status = "Сохранение отменено."; }
        catch (WorkflowValidationException ex) { Error = ex.Message; }
        finally { IsBusy = false; }
    }
}

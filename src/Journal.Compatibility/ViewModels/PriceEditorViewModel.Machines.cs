using Jornal.Core.Pricing;

namespace Jornal.Desktop.ViewModels;

public sealed partial class PriceEditorViewModel
{
    private CuttingMachine[] _machines = [];
    private bool _catalogLoaded;
    public bool IsCustomMachine => _machines.Any(machine => machine.TableName == TableName);
    public bool CanEditMachineImport => _catalogLoaded && IsCustomMachine && !IsEditing && !IsBusy && !RequiresReload && editor is IMachineImportEditor && reader is IMachineCatalogReader;
    public MachineImportViewModel? CreateMachineImportForm()
    {
        if (!CanEditMachineImport) return null;
        var form = new MachineImportViewModel(_machines.Single(machine => machine.TableName == TableName), (IMachineImportEditor)editor!, (IMachineCatalogReader)reader);
        form.SavingChanged += (_, _) => IsSaving = form.IsSaving;
        return form;
    }
    public bool CanAddMachine => _catalogLoaded && !IsEditing && !IsBusy && !RequiresReload && editor is IMachineCreator && reader is IMachineCatalogReader;
    private async Task LoadMachineCatalogAsync(CancellationToken cancellationToken)
    {
        if (reader is not IMachineCatalogReader catalog) return;
        var machines = CuttingMachine.Snapshot(await catalog.ReadMachinesAsync(cancellationToken));
        var groups = PriceCatalog.Groups.Take(4).Concat(machines.Select(machine =>
            Groups.FirstOrDefault(group => group.Profiles.Count == 1 && group.Profiles[0].TableName == machine.TableName) ??
            new PriceGroup(machine.Name, [new PriceProfile(machine.TableName, "Основной", machine.Name)])))
            .Concat(PriceCatalog.Groups.Skip(4)).ToArray();
        // Сохраняем ссылки выбранных групп: обновление каталога не сбрасывает прайс/ввод.
        for (var index = 0; index < groups.Length; index++)
        {
            if (index < Groups.Count && ReferenceEquals(Groups[index], groups[index])) continue;
            var old = Groups.IndexOf(groups[index]);
            if (old >= 0) Groups.Move(old, index); else Groups.Insert(index, groups[index]);
        }
        while (Groups.Count > groups.Length) Groups.RemoveAt(Groups.Count - 1);
        _machines = machines; _catalogLoaded = true; OnPropertyChanged(nameof(SelectedGroup));
    }
    public NewMachineViewModel? CreateMachineForm()
    {
        if (!CanAddMachine) return null;
        var form = new NewMachineViewModel((IMachineCreator)editor!, (IMachineCatalogReader)reader, _machines, storage);
        form.SavingChanged += (_, _) => IsSaving = form.IsSaving;
        return form;
    }
    public async Task ShowCreatedMachineAsync(CuttingMachine machine)
    {
        await RefreshCommand.ExecuteAsync(null);
        if (Groups.FirstOrDefault(group => group.Profiles.Any(profile => profile.TableName == machine.TableName)) is { } group) SelectedGroup = group;
    }
}

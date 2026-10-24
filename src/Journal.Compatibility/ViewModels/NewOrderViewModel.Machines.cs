using Jornal.Core.Pricing;
using Jornal.Infrastructure.Diagnostics;

namespace Jornal.Desktop.ViewModels;

public partial class NewOrderViewModel
{
    private IReadOnlyList<CuttingMachine> _machines = [];
    private CuttingMachine? SelectedMachine => _machines.SingleOrDefault(machine => machine.Name == CuttingType.Value);
    internal void SetMachines(IReadOnlyList<CuttingMachine> machines)
    {
        _machines = CuttingMachine.Snapshot(machines);
        CuttingType.Choices = [.. CuttingMachine.LegacyNames, .. _machines.Select(machine => machine.Name)];
        foreach (var item in BatchItems) item.Form.SetMachines(machines);
        OnPropertyChanged(nameof(HasPricing)); CalculatePriceCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsHansLaser));
        ThicknessOptions.SourceChanged();
    }
    public async Task LoadMachinesAsync(CancellationToken cancellationToken = default)
    {
        if (_creator is not IMachineCatalogReader catalog) return;
        try { SetMachines(await catalog.ReadMachinesAsync(cancellationToken)); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) { ImportError = AppDiagnostics.Describe(DiagnosticOperation.PriceRead, exception, "Не удалось загрузить каталог станков."); }
    }
}

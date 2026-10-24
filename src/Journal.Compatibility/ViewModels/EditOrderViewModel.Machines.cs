using Jornal.Core.Pricing;
using Jornal.Infrastructure.Diagnostics;

namespace Jornal.Desktop.ViewModels;

public partial class EditOrderViewModel
{
    private IReadOnlyList<CuttingMachine> _machines = [];
    private CuttingMachine? SelectedMachine => _machines.SingleOrDefault(machine => machine.Name == CuttingType.Value);
    public async Task LoadMachinesAsync(CancellationToken cancellationToken = default)
    {
        if (_store is not IMachineCatalogReader catalog) return;
        try
        {
            _machines = CuttingMachine.Snapshot(await catalog.ReadMachinesAsync(cancellationToken));
            CuttingType.Choices = CuttingMachine.LegacyNames.Concat(_machines.Select(machine => machine.Name))
                .Concat(string.IsNullOrWhiteSpace(CuttingType.Value) ? [] : new[] { CuttingType.Value }).Distinct().ToArray();
            OnPropertyChanged(nameof(IsHansLaser));
            ThicknessOptions.SourceChanged();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) { Error = AppDiagnostics.Describe(DiagnosticOperation.PriceRead, exception, "Не удалось загрузить каталог станков."); }
    }
}

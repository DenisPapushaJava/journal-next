namespace Jornal.Desktop.ViewModels;

public sealed partial class PriceEditorViewModel
{
    private bool _exportDialogOpen;
    public bool CanExport => CanNavigate && !RequiresReload && !_exportDialogOpen &&
        (reader is not Jornal.Core.Pricing.IMachineCatalogReader || _catalogLoaded);
    public bool IsExportDialogOpen => _exportDialogOpen;
    public PriceExportViewModel? CreateExportForm() => CanExport ? new(reader, Groups) : null;
    public void SetExportDialogOpen(bool value)
    { _exportDialogOpen = value; NotifyAvailability(); OnPropertyChanged(nameof(IsExportDialogOpen)); }
}

using Jornal.Infrastructure.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jornal.Desktop.Import;
using Jornal.Parsing.Documents;

namespace Jornal.Desktop.ViewModels;

public partial class NewOrderViewModel
{
    private CancellationTokenSource? _importReadCancellation;
    private string? _importSourcePath;
    public ISpecificationImportService ImportReader { get; set; } = new SpecificationImportService();
    public void CancelImportRead() { _importReadCancellation?.Cancel(); foreach (var item in BatchItems) item.CancelRead(); }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasImport)), NotifyPropertyChangedFor(nameof(HasImportDiagnostics)), NotifyPropertyChangedFor(nameof(ImportDiagnostics))]
    private SpecificationImportPreview? _importPreview;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasImportError))] private string? _importError;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsBusy))] private bool _isImporting;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasPendingImport)), NotifyPropertyChangedFor(nameof(PendingImportName))]
    private string? _pendingImportPath;
    public bool HasImport => ImportPreview is not null;
    public string ImportDiagnostics => ImportPreview?.DiagnosticsFor(Material.NeedsReview) ?? "";
    public bool HasImportDiagnostics => !string.IsNullOrEmpty(ImportDiagnostics);
    public bool HasImportError => !string.IsNullOrEmpty(ImportError);
    public bool HasPendingImport => PendingImportPath is not null;
    public string PendingImportName => Path.GetFileName(PendingImportPath) ?? "";
    private IReadOnlyDictionary<string, OrderEntryField> ImportFields => new Dictionary<string, OrderEntryField>
    {
        [nameof(OrderDate)] = OrderDate, [nameof(SpecificationNumber)] = SpecificationNumber, [nameof(CuttingType)] = CuttingType,
        [nameof(Material)] = Material, [nameof(Thickness)] = Thickness, [nameof(SheetSize)] = SheetSize, [nameof(SheetCount)] = SheetCount,
        [nameof(Weight)] = Weight, [nameof(CutLength)] = CutLength, [nameof(Coefficient)] = Coefficient,
        [nameof(MaterialId)] = MaterialId, [nameof(Gas)] = Gas, [nameof(CuttingTime)] = CuttingTime
    };

    partial void OnIsImportingChanged(bool value)
    { NotifyBatchState(); }
    partial void OnPendingImportPathChanged(string? value)
    { NotifyBatchState(); }

    public void ApplyImport(SpecificationImportPreview preview, string? sourcePath = null)
    {
        if (IsSaved || SaveCommand.IsRunning || RequiresReload) return;
        preview = SpecificationMachineBinding.Bind(preview, _machines, CuttingType.Value);
        _submitted = false;
        _applyingThicknessImport = true;
        try
        {
            foreach (var (name, field) in ImportFields)
            {
                var value = preview.Fields[name]; field.ApplyImport(value.Value, value.Evidence);
            }
        }
        finally { _applyingThicknessImport = false; }
        Material.NeedsReview = preview.Report.Issues.Any(issue => issue.Code == "MaterialNeedsReview") && Material.Value.Length > 0;
        HasValidationErrors = false; SaveError = null; ImportError = null;
        ImportPreview = preview; _importSourcePath = sourcePath;
        _quote = null; PriceText = "—"; PriceBreakdown = ""; PriceError = null;
        InvalidateLivePrice(); ThicknessOptions.SourceChanged();
    }

    public async Task RequestImportAsync(string path, CancellationToken cancellationToken)
    {
        if (IsBatchMode) { await AddBatchFilesAsync([path], cancellationToken); return; }
        if (IsBusy || IsSaved || RequiresReload) return;
        if (HasCurrentParameters)
        { PendingImportPath = path; return; }
        await ImportFileAsync(path, cancellationToken);
    }

    [RelayCommand]
    private async Task ReplaceImportAsync(CancellationToken cancellationToken)
    {
        if (PendingImportPath is not { } path || IsBusy || IsSaved || RequiresReload) return;
        PendingImportPath = null;
        await ImportFileAsync(path, cancellationToken);
    }
    [RelayCommand] private void CancelImportReplacement() => PendingImportPath = null;

    private async Task ImportFileAsync(string path, CancellationToken cancellationToken)
    {
        AppDiagnostics.Current?.Mark(DiagnosticOperation.SpecificationImport);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _importReadCancellation = cancellation;
        IsImporting = true; ImportError = null;
        try
        {
            await LoadMachinesAsync(cancellation.Token);
            var preview = await ImportReader.ReadAsync(path, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            ApplyImport(preview, path);
            _ = ThicknessOptions.RefreshAsync();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (DocumentRejectedException exception) { ImportError = exception.Message; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { ImportError = AppDiagnostics.Describe(DiagnosticOperation.SpecificationImport, exception, "Не удалось прочитать спецификацию. Прежний ввод сохранён."); }
        finally { _importReadCancellation = null; IsImporting = false; }
    }
}

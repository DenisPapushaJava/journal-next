using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jornal.Core.Orders;
using Jornal.Desktop.Import;
using Jornal.Infrastructure.Diagnostics;
using Jornal.Parsing.Documents;
using Jornal.Parsing.Specifications;

namespace Jornal.Desktop.ViewModels;

public partial class EditOrderViewModel
{
    private CancellationTokenSource? _importCancellation;
    public ISpecificationImportService ImportReader { get; set; } = new SpecificationImportService();
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasImport)), NotifyPropertyChangedFor(nameof(HasImportDiagnostics)), NotifyPropertyChangedFor(nameof(ImportDiagnostics))]
    private SpecificationImportPreview? _importPreview;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasPendingImport))]
    private SpecificationImportPreview? _pendingImport;
    [ObservableProperty] private bool _isImporting;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasImportError))] private string? _importError;
    [ObservableProperty] private bool _isDraggingFiles;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasDropRejection))] private string? _dropRejection;
    public bool HasImport => ImportPreview is not null;
    public string ImportDiagnostics => ImportPreview?.DiagnosticsFor(Material.NeedsReview) ?? "";
    public bool HasImportDiagnostics => !string.IsNullOrEmpty(ImportDiagnostics);
    public bool HasPendingImport => PendingImport is not null;
    public bool HasImportError => ImportError is not null;
    public bool HasDropRejection => DropRejection is not null;
    public bool CanImport => !RequiresReload && !IsBusy && !HasPendingImport && SavedRecord is null;
    public bool CanEditFields => !IsBusy && !HasPendingImport;
    private IReadOnlyDictionary<string, OrderEntryField> ImportFields => new Dictionary<string, OrderEntryField>
    {
        [nameof(OrderDate)] = OrderDate, [nameof(SpecificationNumber)] = SpecificationNumber, [nameof(CuttingType)] = CuttingType,
        [nameof(Material)] = Material, [nameof(Thickness)] = Thickness, [nameof(SheetSize)] = SheetSize, [nameof(SheetCount)] = SheetCount,
        [nameof(Weight)] = Weight, [nameof(CutLength)] = CutLength, [nameof(Coefficient)] = Coefficient,
        [nameof(MaterialId)] = MaterialId, [nameof(Gas)] = Gas, [nameof(CuttingTime)] = CuttingTime
    };
    partial void OnIsImportingChanged(bool value) => NotifyBusy();
    partial void OnPendingImportChanged(SpecificationImportPreview? value) => NotifyBusy();
    public string? CheckFileDrop(IReadOnlyList<string> paths) => !CanImport
        ? RequiresReload ? "Сначала обновите заявку (F5)." : HasPendingImport ? "Завершите замену спецификации." : "Дождитесь завершения операции."
        : SpecificationImportService.CheckFiles(paths);
    public void CancelImportRead() => _importCancellation?.Cancel();

    public async Task RequestImportAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        if (CheckFileDrop(paths) is { } error) { ImportError = error; return; }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _importCancellation = cancellation;
        IsImporting = true; ImportError = null;
        AppDiagnostics.Current?.Mark(DiagnosticOperation.SpecificationImport);
        try
        {
            await LoadMachinesAsync(cancellation.Token);
            var preview = await ImportReader.ReadAsync(paths[0], cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (!RequiresReload && SavedRecord is null) { ImportError = null; PendingImport = preview; }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (DocumentRejectedException exception) { ImportError = exception.Message; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { ImportError = AppDiagnostics.Describe(DiagnosticOperation.SpecificationImport, exception, "Не удалось прочитать спецификацию. Прежний ввод сохранён."); }
        finally { _importCancellation = null; IsImporting = false; }
    }

    [RelayCommand]
    private void CancelImportReplacement() { if (!IsBusy) PendingImport = null; }

    [RelayCommand]
    private void ReplaceImport()
    {
        if (PendingImport is not { } preview || IsBusy || RequiresReload || SavedRecord is not null) return;
        preview = SpecificationMachineBinding.Bind(preview, _machines, CuttingType.Value);
        // Сначала сопоставляем все поля: неполный контракт не должен менять половину формы.
        var mapped = ImportFields.Select(pair => (Field: pair.Value, Value: preview.Fields[pair.Key])).ToArray();
        _populating = true;
        try { foreach (var (field, value) in mapped) field.ApplyImport(value.Value, value.Evidence); }
        finally { _populating = false; }
        Material.NeedsReview = preview.Report.Issues.Any(issue => issue.Code == "MaterialNeedsReview") && Material.Value.Length > 0;
        ImportPreview = preview; PendingImport = null;
        _timeEdited = true; _quote = null; PriceText = "—"; Error = null; ImportError = null;
        OnPropertyChanged(nameof(IsHansLaser)); OnPropertyChanged(nameof(HasMetadata)); OnPropertyChanged(nameof(MetadataText));
        OnPropertyChanged(nameof(HasCuttingTimeNotice)); RefreshChanges();
        InvalidateLivePrice(); ThicknessOptions.SourceChanged();
    }

    private void ValidateImportedParameters()
    {
        if (ImportPreview is null) return;
        foreach (var entry in ImportFields.Values.Where(entry => entry != CuttingTime && entry != MaterialId && entry != Gas))
            if (string.IsNullOrWhiteSpace(entry.Value)) entry.Error = "Заполните поле.";
        if (IsHansLaser && ImportPreview.Report.Profile == HansPdfSpecificationParser.Profile)
        {
            if (MaterialId.Value.Trim() is not ("-1" or "0" or "1" or "2")) MaterialId.Error = "Проверьте ID материала HansLaser.";
            if (string.IsNullOrWhiteSpace(Gas.Value)) Gas.Error = "Проверьте газ по спецификации.";
        }
        if (Fields.Any(entry => entry.HasError)) throw new ArgumentException("Проверьте отмеченные поля.");
    }

    private void AddImportedCountChanges(List<OrderFieldChange> changes)
    {
        if (ImportPreview is not { } preview) return;
        var original = _expected.ToSummary();
        foreach (var (label, before, after) in new[] { ("Детали", original.PartCount, preview.PartCount), ("Пробивки", original.PunchCount, preview.PunchCount) })
            if (before != after) changes.Add(new(label, before?.ToString() ?? "—", after?.ToString() ?? "—"));
    }
}

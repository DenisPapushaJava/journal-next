using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jornal.Parsing.Documents;
using Jornal.Infrastructure.Diagnostics;
using Jornal.Desktop.Import;

namespace Jornal.Desktop.ViewModels;

public partial class MainWindowViewModel
{
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowSpecificationOverlay)), NotifyPropertyChangedFor(nameof(SpecificationOverlayTitle)),
        NotifyPropertyChangedFor(nameof(ShowSpecificationReading)), NotifyPropertyChangedFor(nameof(ShowSpecificationRejection)),
        NotifyPropertyChangedFor(nameof(SpecificationRejectionText))]
    private bool _isDraggingSpecification;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowSpecificationOverlay)), NotifyPropertyChangedFor(nameof(SpecificationOverlayTitle)),
        NotifyPropertyChangedFor(nameof(ShowSpecificationReading)), NotifyPropertyChangedFor(nameof(ShowSpecificationRejection)),
        NotifyPropertyChangedFor(nameof(SpecificationRejectionText))]
    private bool _isImportingSpecification;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(SpecificationOverlayTitle)),
        NotifyPropertyChangedFor(nameof(ShowSpecificationReading)), NotifyPropertyChangedFor(nameof(ShowSpecificationRejection)),
        NotifyPropertyChangedFor(nameof(SpecificationRejectionText))]
    private string? _specificationDropError;
    [ObservableProperty] private string _specificationOverlayDescription = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowSpecificationOverlay)), NotifyPropertyChangedFor(nameof(ShowSpecificationReading)), NotifyPropertyChangedFor(nameof(ShowSpecificationRejection)),
        NotifyPropertyChangedFor(nameof(HasSpecificationImportError)), NotifyPropertyChangedFor(nameof(SpecificationRejectionText))]
    private string? _specificationImportError;
    public bool HasSpecificationImportError => !string.IsNullOrEmpty(SpecificationImportError);
    public bool ShowSpecificationReading => IsImportingSpecification && !ShowSpecificationRejection;
    public bool ShowSpecificationRejection => IsDraggingSpecification ? SpecificationDropError is not null : HasSpecificationImportError;
    public string? SpecificationRejectionText => IsDraggingSpecification ? SpecificationDropError : SpecificationImportError;
    public bool ShowSpecificationOverlay => IsDraggingSpecification || IsImportingSpecification || HasSpecificationImportError;
    [RelayCommand] private void DismissSpecificationImportError() => SpecificationImportError = null;
    public string SpecificationOverlayTitle => IsImportingSpecification ? "Читаем спецификацию…" :
        SpecificationDropError is null ? "Отпустите спецификацию здесь" : "Этот файл пока не принимается";
    public bool CanImportSpecification => CanCreateOrder && _pendingCreationForm is null;
    public string? ImportAvailabilityError => _pendingCreationForm is not null || RequiresReload
        ? "Сначала обновите список (F5) и проверьте предыдущее добавление. «Новая заявка» восстановит сохранённый ввод."
        : orderCreator is null ? "Источник доступен только для чтения. Для добавления подключите редактируемый журнал."
        : !CanUseJournal ? "Дождитесь завершения текущего действия или завершите настройку экрана." : null;
    partial void OnIsImportingSpecificationChanged(bool value) => NotifyJournalAvailability();

    public async Task<NewOrderViewModel?> ReadSpecificationAsync(IReadOnlyList<string> paths, ISpecificationImportService reader,
        CancellationToken cancellationToken)
    {
        IsDraggingSpecification = false;
        if (IsImportingSpecification) return null;
        SpecificationImportError = null;
        if ((ImportAvailabilityError ?? SpecificationImportService.CheckDropFiles(paths)) is { } error)
        { SpecificationImportError = error; return null; }
        if (paths.Count > 1)
        {
            var batch = CreateOrderForm(forImport: true);
            if (batch is null) return null;
            batch.ImportReader = reader;
            _ = batch.AddBatchFilesAsync(paths, cancellationToken);
            return batch;
        }
        if (CreateOrderForm(forImport: true) is not { } form) return null;
        SpecificationOverlayDescription = Path.GetFileName(paths[0]);
        IsImportingSpecification = true;
        try
        {
            var preview = await reader.ReadAsync(paths[0], cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            // AWJC связывается с каталогом текущей базы до заполнения формы.
            await form.LoadMachinesAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            form.ApplyImport(preview, paths[0]);
            return form;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (DocumentRejectedException exception) { SpecificationImportError = exception.Message; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { SpecificationImportError = AppDiagnostics.Describe(DiagnosticOperation.SpecificationImport, exception, "Не удалось прочитать спецификацию."); }
        finally { IsImportingSpecification = false; }
        return null;
    }
}

using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Jornal.Desktop.ViewModels;

public sealed partial class BatchSpecificationItem : ObservableObject
{
    private readonly NewOrderViewModel _owner;
    private readonly CancellationTokenSource _readCancellation = new();
    public BatchSpecificationItem(NewOrderViewModel owner, NewOrderViewModel form, string fileName, string? path = null)
    {
        _owner = owner; Form = form; FileName = fileName; Path = path;
        RemoveCommand = new RelayCommand(() => owner.RemoveBatchItem(this), () => owner.CanAddFiles);
        foreach (var field in form.Fields) field.PropertyChanged += FieldChanged;
        form.PropertyChanged += FormChanged;
        form.Annotations.PropertyChanged += AnnotationsChanged;
    }
    public string FileName { get; }
    public string PositionLabel => "№ " + (_owner.BatchItems.IndexOf(this) + 1);
    internal string? Path { get; }
    internal bool DuplicateCheckFailed { get; set; }
    internal CancellationToken ReadCancellation => _readCancellation.Token;
    public NewOrderViewModel Form { get; }
    [ObservableProperty] private bool _isIncluded = true;
    [ObservableProperty] private bool _isReading = true;
    [ObservableProperty] private bool _hasParameters;
    [ObservableProperty] private string? _failure;
    [ObservableProperty] private string? _warning;
    private bool _validParameters;
    public double CardOpacity => IsIncluded ? 1 : 0.65;
    public bool HasFailure => Failure is not null;
    public bool HasWarning => Warning is not null;
    internal bool HasValidParameters => !IsReading && HasParameters && !HasFailure && _validParameters && !Form.Material.NeedsReview;
    public bool IsReady => HasValidParameters && !Form.HasPriceError && Form.ThicknessOptions.CanSave;
    public string Status => IsReading ? "Читаем…" : HasFailure ? "Не принят" : Form.ThicknessOptions.IsLoading ? "Загружаем толщины…" : Form.ThicknessOptions.HasUnavailableValue ? "Проверьте толщину" : Form.HasPriceError ? "Проверьте цену" : IsReady ? "Готово" : "Проверьте поля";
    public string? Details => Failure ?? Form.Thickness.ChoiceError ?? Form.PriceError ?? Warning ?? Form.ImportDiagnostics;
    public bool CanEdit => _owner.CanEditBatch;
    public IRelayCommand RemoveCommand { get; }
    partial void OnIsIncludedChanged(bool value) { OnPropertyChanged(nameof(CardOpacity)); _owner.NotifyBatchState(); }
    partial void OnIsReadingChanged(bool value) => Refresh();
    partial void OnHasParametersChanged(bool value) => Refresh();
    partial void OnFailureChanged(string? value) { OnPropertyChanged(nameof(HasFailure)); Refresh(); }
    partial void OnWarningChanged(string? value) { OnPropertyChanged(nameof(HasWarning)); OnPropertyChanged(nameof(Details)); }
    private void FieldChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(OrderEntryField.Value) or nameof(OrderEntryField.NeedsReview) && HasParameters)
        {
            Refresh();
            if (sender == Form.OrderDate || sender == Form.SpecificationNumber || sender == Form.CuttingType) _owner.UpdateDuplicateWarnings();
        }
    }
    private void AnnotationsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (HasParameters && e.PropertyName is nameof(OrderAnnotationsEditor.HasBending) or nameof(OrderAnnotationsEditor.Comment)) Refresh();
    }
    private void FormChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(NewOrderViewModel.PriceError) or nameof(NewOrderViewModel.HasPriceValidationError) or nameof(NewOrderViewModel.IsBusy))) return;
        OnPropertyChanged(nameof(IsReady)); OnPropertyChanged(nameof(Status)); OnPropertyChanged(nameof(Details));
        _owner.NotifyBatchState();
    }
    internal void Refresh()
    {
        _validParameters = HasParameters && Form.ValidateBatchParameters();
        OnPropertyChanged(nameof(IsReady)); OnPropertyChanged(nameof(Status)); OnPropertyChanged(nameof(Details));
        _owner.NotifyBatchState();
    }
    internal void NotifyOwnerState() { OnPropertyChanged(nameof(PositionLabel)); OnPropertyChanged(nameof(CanEdit)); RemoveCommand.NotifyCanExecuteChanged(); }
    internal void CancelRead() { _readCancellation.Cancel(); Form.CalculatePriceCommand.Cancel(); }
    internal void Detach()
    {
        CancelRead();
        Form.StopPricePreview();
        Form.ThicknessOptions.Stop();
        foreach (var field in Form.Fields) field.PropertyChanged -= FieldChanged;
        Form.PropertyChanged -= FormChanged;
        Form.Annotations.PropertyChanged -= AnnotationsChanged;
    }
}

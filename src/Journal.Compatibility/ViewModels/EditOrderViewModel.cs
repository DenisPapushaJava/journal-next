using Jornal.Infrastructure.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jornal.Core.Orders;
using Jornal.Core.Pricing;

namespace Jornal.Desktop.ViewModels;

public partial class EditOrderViewModel : ObservableObject
{
    private readonly IOrderMaintenance _store;
    private readonly Func<CancellationToken, Task<OrderRecord?>> _refresh;
    private OrderRecord _expected;
    private OrderRecord? _current;
    private readonly Dictionary<OrderEntryField, string> _initial = [];
    private CuttingPriceQuote? _quote;
    private bool _timeEdited;
    private bool _populating;
    public int Id => _expected.Id;
    public string WindowTitle => $"Редактировать заявку · код {Id}";
    public string SourceDescription { get; }
    public string SourceName => System.IO.Path.GetFileName(SourceDescription);
    public string StoredPriceText => _expected.Price?.ToString("N2", CultureInfo.GetCultureInfo("ru-RU")) + (_expected.Price is null ? "Неизвестна" : " ₽");
    public string MetadataText
    {
        get
        {
            var original = _expected.ToSummary();
            var parts = ImportPreview is { } imported ? imported.PartCount : original.PartCount;
            var punches = ImportPreview is { } preview ? preview.PunchCount : original.PunchCount;
            return $"Детали: {parts?.ToString() ?? "—"} · пробивки: {punches?.ToString() ?? "—"}";
        }
    }
    public bool HasMetadata => ImportPreview is not null || _expected.ToSummary() is { PartCount: not null } or { PunchCount: not null };
    public AccessHostHelpViewModel AccessHostHelp { get; } = new();
    public OrderRecord? SavedRecord { get; private set; }
    public event EventHandler? Saved;
    public event EventHandler? ReloadRequired;
    public event EventHandler? BusyChanged;

    public EditOrderViewModel(OrderRecord expected, IOrderMaintenance store, string sourceDescription,
        Func<CancellationToken, Task<OrderRecord?>> refresh, TimeProvider? priceTimeProvider = null)
    {
        Fields = Array.AsReadOnly<OrderEntryField>([OrderDate, SpecificationNumber, Manager, Customer, CuttingType, Material,
            Thickness, SheetSize, SheetCount, Weight, CutLength, Coefficient, MaterialId, Gas, CuttingTime]);
        _expected = expected; _store = store; SourceDescription = sourceDescription; _refresh = refresh;
        if (!string.IsNullOrWhiteSpace(expected.CuttingType) && !CuttingMachine.LegacyNames.Contains(expected.CuttingType))
            CuttingType.Choices = [.. CuttingMachine.LegacyNames, expected.CuttingType];
        Populate(expected);
        Annotations.PropertyChanged += (_, _) =>
        {
            if (_populating) return;
            RefreshChanges();
            NotifyBusy();
        };
        foreach (var field in Fields) field.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(OrderEntryField.NeedsReview)) { OnPropertyChanged(nameof(ImportDiagnostics)); OnPropertyChanged(nameof(HasImportDiagnostics)); NotifyBusy(); return; }
            if (e.PropertyName != nameof(OrderEntryField.Value)) return;
            field.Error = null;
            if (field == CuttingTime && !_populating) _timeEdited = true;
            OnPropertyChanged(nameof(CuttingTimeNotice));
            OnPropertyChanged(nameof(HasCuttingTimeNotice));
            if (field == CuttingType || field == Thickness || field == CutLength || field == Coefficient || field == MaterialId)
            { InvalidateLivePrice(); PricePreviewError = null; }
            if (field == CuttingType) OnPropertyChanged(nameof(IsHansLaser));
            if (field == OrderDate || field == SpecificationNumber)
            { DateOnly.TryParseExact(OrderDate.Value.Trim(), "dd.MM.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var identityDate); Annotations.SetIdentity(identityDate, SpecificationNumber.Value.Trim()); }
            RefreshChanges();
        };
        SaveCommand.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(IAsyncRelayCommand.IsRunning)) NotifyBusy(); };
        CalculateCommand.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(IAsyncRelayCommand.IsRunning)) NotifyBusy(); };
        RefreshCommand.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(IAsyncRelayCommand.IsRunning)) NotifyBusy(); };
        InitializeThicknessOptions(); InitializePricePreview(priceTimeProvider);
        foreach (var field in new[] { CuttingType, MaterialId }) field.PropertyChanged += (_, e) =>
        { if (!_populating && e.PropertyName == nameof(OrderEntryField.Value)) ThicknessOptions.SourceChanged(); };
    }

    public OrderEntryField OrderDate { get; } = new("Дата") { IsInputEnabled = false };
    public OrderEntryField SpecificationNumber { get; } = new("№ спецификации");
    public OrderEntryField Manager { get; } = new("Менеджер");
    public OrderEntryField Customer { get; } = new("Заказчик");
    public OrderEntryField CuttingType { get; } = new("Вид резки / станок", choices: ["Лазер", "Плазма", "Газо-кислородная", "HansLaser"]);
    public OrderEntryField Material { get; } = new("Материал") { UseEditableSuggestions = true };
    public OrderEntryField Thickness { get; } = new("Толщина, мм");
    public OrderEntryField SheetSize { get; } = new("Размер листа, мм");
    public OrderEntryField SheetCount { get; } = new("Количество листов");
    public OrderEntryField Weight { get; } = new("Вес, кг");
    public OrderEntryField CutLength { get; } = new("Длина реза, м");
    public OrderAnnotationsEditor Annotations { get; } = new();
    public OrderEntryField CuttingTime { get; } = new("Время резки, ч:мин");
    public bool HasCuttingTimeNotice => !_timeEdited && (_expected.ToSummary().CuttingTimeNeedsReview ||
        _expected.CuttingTime is not null && new[] { OrderDate, SpecificationNumber, CuttingType, Material, Thickness,
            SheetSize, SheetCount, Weight, CutLength, Coefficient, MaterialId, Gas }.Any(entry => entry.Value != _initial[entry]));
    public string CuttingTimeNotice => "Параметры изменились. Проверьте время резки.";
    public OrderEntryField Coefficient { get; } = new("Коэффициент");
    public OrderEntryField MaterialId { get; } = new("ID материала HansLaser");
    public OrderEntryField Gas { get; } = new("Газ HansLaser");
    public IReadOnlyList<OrderEntryField> Fields { get; }
    public bool IsHansLaser => CuttingType.Value == "HansLaser" || SelectedMachine?.SpecificationProfileId == CuttingMachine.HansProfile;
    [ObservableProperty] private string _priceText = "—";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError))] private string? _error;
    [ObservableProperty] private bool _requiresReload;
    [ObservableProperty] private bool _hasCurrentVersion;
    [ObservableProperty] private string _comparison = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasChanges))] private IReadOnlyList<OrderFieldChange> _changes = [];
    public bool HasChanges => Changes.Count > 0;
    public bool HasError => Error is not null;
    public bool IsBusy => SaveCommand.IsRunning || CalculateCommand.IsRunning || RefreshCommand.IsRunning || IsImporting || IsEditingPrices;
    public bool CanCalculate => !RequiresReload && !IsBusy && !HasPendingImport && SavedRecord is null;
    public bool CanSave => HasChanges && !Material.NeedsReview && !Annotations.NeedsReview && !RequiresReload && !IsBusy && !HasPendingImport && SavedRecord is null && (!NeedsPricePreview || ThicknessOptions.CanSave);
    public bool CanRefresh => RequiresReload && !IsBusy && !HasPendingImport;
    partial void OnRequiresReloadChanged(bool value) => NotifyBusy();
    partial void OnChangesChanged(IReadOnlyList<OrderFieldChange> value)
    {
        OnPropertyChanged(nameof(CanSave));
        SaveCommand.NotifyCanExecuteChanged();
    }
    private void NotifyBusy()
    {
        OnPropertyChanged(nameof(IsBusy)); OnPropertyChanged(nameof(CanSave)); OnPropertyChanged(nameof(CanCalculate)); OnPropertyChanged(nameof(CanRefresh));
        SaveCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanImport)); OnPropertyChanged(nameof(CanEditFields));
        BusyChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Populate(OrderRecord row)
    {
        _populating = true;
        var values = FieldValues(row);
        for (var i = 0; i < Fields.Count; i++) { Fields[i].Value = values[i]; _initial[Fields[i]] = values[i]; }
        Annotations.Load(row.Annotations, row.ToSummary());
        _timeEdited = false; _populating = false;
        _quote = null; PriceText = StoredPriceText; PriceBreakdown = ""; _refreshPriceRequested = false; _pricePreview?.Cancel();
        OnPropertyChanged(nameof(HasCuttingTimeNotice)); OnPropertyChanged(nameof(HasMetadata)); OnPropertyChanged(nameof(MetadataText));
        RefreshChanges();
        ThicknessOptions?.SourceChanged();
    }

    private void RefreshChanges()
    {
        if (_populating || _initial.Count != Fields.Count) return;
        var changes = Fields.Where(field => field.Value != _initial[field])
            .Select(field => new OrderFieldChange(field.Label, Display(_initial[field]), Display(field.Value))).ToList();
        if (Annotations.HasBending != (_expected.Annotations?.HasBending ?? false)) changes.Add(new("Гибка", _expected.Annotations?.HasBending == true ? "Да" : "Нет", Annotations.HasBending ? "Да" : "Нет"));
        if (Annotations.Comment != (_expected.Annotations?.Comment ?? "")) changes.Add(new("Комментарий", Display(_expected.Annotations?.Comment), Display(Annotations.Comment)));
        if (_expected.Annotations is { } annotations && !annotations.Matches(_expected.ToSummary()) && !Annotations.NeedsReview &&
            (Annotations.HasBending || Annotations.HasComment))
            changes.Add(new("Гибка и комментарий", "Требуют проверки", "Подтверждены"));
        if (_quote is { } quote && quote.StoredPrice != _expected.Price)
            changes.Add(new("Стоимость", StoredPriceText, PriceText));
        AddImportedCountChanges(changes); Changes = changes;
    }

    private static string Display(string? value) => string.IsNullOrWhiteSpace(value) ? "—" : value;

    private static string[] FieldValues(OrderRecord row)
    {
        var view = row.ToSummary();
        return [row.Date?.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) ?? "", row.Specification ?? "", row.Manager ?? "",
            row.Customer ?? "", row.CuttingType ?? "", row.Material ?? "", view.ThicknessMillimeters?.ToString(CultureInfo.InvariantCulture) ?? "",
            row.SheetSize ?? "", row.SheetCount ?? "", row.Weight ?? "", row.CutLength?.ToString(CultureInfo.InvariantCulture) ?? "",
            view.Coefficient?.ToString(CultureInfo.InvariantCulture) ?? "", view.MaterialId, view.CuttingGas, view.CuttingDurationMinutes is { } minutes ? CuttingDuration.Format(minutes) : ""];
    }

    public OrderEditChange BuildChange()
    {
        if (Material.NeedsReview) throw new ArgumentException("Подтвердите материал.");
        if (Jornal.Desktop.Import.SpecificationMachineBinding.Validate(ImportPreview, CuttingType.Value, SelectedMachine) is { } importError)
        { CuttingType.Error = importError; throw new ArgumentException(importError); }
        foreach (var field in Fields) field.Error = null;
        ValidateImportedParameters();
        string? Text(OrderEntryField field, string? original)
        {
            if (field.Value == _initial[field]) return original;
            if (string.IsNullOrWhiteSpace(field.Value) || field.Value.Length > 255 || field.Value.Any(char.IsControl))
            { field.Error = "Введите значение до 255 символов"; throw new ArgumentException(field.Label); }
            return field.Value.Trim();
        }
        decimal Number(OrderEntryField field)
        {
            if (decimal.TryParse(field.Value.Trim().Replace(',', '.'), NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture, out var number)) return number;
            field.Error = "Введите число"; throw new ArgumentException(field.Label);
        }
        var date = _expected.Date;
        if (OrderDate.Value != _initial[OrderDate])
        {
            if (!DateTime.TryParseExact(OrderDate.Value.Trim(), "dd.MM.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) || parsed.Year < 100)
            { OrderDate.Error = "Укажите дату ДД.ММ.ГГГГ"; throw new ArgumentException("Дата"); }
            date = parsed;
        }
        var thickness = Number(Thickness);
        if (!int.TryParse(CutLength.Value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var length))
        { CutLength.Error = "Целое число метров"; throw new ArgumentException("Длина"); }
        var coefficient = Number(Coefficient);
        var storedCoefficient = _expected.Coefficient;
        if (ImportPreview is not null || Coefficient.Value != _initial[Coefficient] || MaterialId.Value != _initial[MaterialId] || Gas.Value != _initial[Gas])
        {
            if (new[] { MaterialId.Value, Gas.Value }.Any(value => value.Length > 255 || value.Any(c => c == '|' || char.IsControl(c))))
                throw new ArgumentException("Проверьте ID материала и газ: разделитель | не допускается.");
            var original = _expected.Coefficient?.Split('|') ?? [];
            var parts = Enumerable.Range(0, Math.Max(5, original.Length)).Select(index => index < original.Length ? original[index] : "-1").ToArray();
            if (ImportPreview is not null || Coefficient.Value != _initial[Coefficient]) parts[0] = coefficient.ToString(CultureInfo.InvariantCulture);
            if (ImportPreview is not null || MaterialId.Value != _initial[MaterialId]) parts[3] = string.IsNullOrWhiteSpace(MaterialId.Value) ? "-1" : MaterialId.Value.Trim();
            if (ImportPreview is not null || Gas.Value != _initial[Gas]) parts[4] = string.IsNullOrWhiteSpace(Gas.Value) ? "-1" : Gas.Value.Trim();
            if (ImportPreview is { } imported)
            { parts[1] = imported.PartCount?.ToString(CultureInfo.InvariantCulture) ?? "-1"; parts[2] = imported.PunchCount?.ToString(CultureInfo.InvariantCulture) ?? "-1"; }
            storedCoefficient = string.Join('|', parts);
        }
        var material = Text(Material, _expected.Material);
        if (Material.Value != _initial[Material] && SelectedMachine?.SpecificationProfileId != CuttingMachine.WaterjetProfile && material?.Any(char.IsWhiteSpace) == true)
        { Material.Error = "Материал без пробелов"; throw new ArgumentException("Материал"); }
        if (SheetCount.Value != _initial[SheetCount] && !OrderSheetCount.IsValid(SheetCount.Value.Trim()))
        { SheetCount.Error = OrderSheetCount.ValidationMessage; throw new ArgumentException("Количество листов"); }
        var values = _expected with { Date = date, Manager = Text(Manager, _expected.Manager), Specification = Text(SpecificationNumber, _expected.Specification),
            Customer = Text(Customer, _expected.Customer), CuttingType = CuttingType.Value, Material = material, SheetSize = Text(SheetSize, _expected.SheetSize),
            SheetCount = Text(SheetCount, _expected.SheetCount), Weight = Text(Weight, _expected.Weight),
            Thickness = Thickness.Value == _initial[Thickness] ? _expected.Thickness : (double)thickness, CutLength = length, Coefficient = storedCoefficient };
        if (!CuttingDuration.TryParse(CuttingTime.Value, out var minutes))
        { CuttingTime.Error = CuttingDuration.ValidationMessage; throw new ArgumentException(CuttingTime.Error); }
        if (_timeEdited) values = values with { CuttingTime = minutes is { } time ? OrderCuttingTime.For(values.ToSummary(), time) : null };
        values = values with { Annotations = Annotations.Build(values.ToSummary()) };
        var change = new OrderEditChange(_expected, values, _quote) { Machine = SelectedMachine }; change.Validate(); return change;
    }

    [RelayCommand]
    private async Task CalculateAsync(CancellationToken cancellationToken)
    {
        if (RequiresReload || SavedRecord is not null || SaveCommand.IsRunning || RefreshCommand.IsRunning || IsImporting || IsEditingPrices || HasPendingImport) return;
        AppDiagnostics.Current?.Mark(DiagnosticOperation.PriceRead);
        Error = null; PricePreviewError = null; AccessHostHelp.ShowFor(null); _refreshPriceRequested = true;
        InvalidateLivePrice(); RefreshChanges();
        try
        {
            await ThicknessOptions.WaitForLoadAsync(cancellationToken);
            await RefreshThicknessOptionsAsync(true, cancellationToken);
            RefreshLivePrice(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) { PricePreviewError = AppDiagnostics.Describe(DiagnosticOperation.PriceRead, exception, "Не удалось обновить тарифы."); AccessHostHelp.ShowFor(exception); }
        finally
        {
            if (_quote is null && !PriceInputsChanged && ImportPreview is null)
            { _refreshPriceRequested = false; _pricePreview.Cancel(); PriceText = StoredPriceText; RefreshChanges(); NotifyBusy(); }
        }
    }

    private void ShowQuote(CuttingPriceQuote quote)
    {
        _pricePreview.Cancel(); _quote = quote; PricePreviewError = null; PriceBreakdown = FormatPriceBreakdown(quote); PriceText = quote.StoredPrice.ToString("N2", CultureInfo.GetCultureInfo("ru-RU")) + " ₽";
        RefreshChanges();
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (!HasChanges || SaveCommand.IsRunning || RequiresReload || SavedRecord is not null || CalculateCommand.IsRunning || RefreshCommand.IsRunning || IsImporting || HasPendingImport || IsEditingPrices) return;
        AppDiagnostics.Current?.Mark(DiagnosticOperation.OrderEdit);
        Error = null; AccessHostHelp.ShowFor(null);
        try
        {
            await ThicknessOptions.WaitForLoadAsync();
            if (NeedsPricePreview && !ThicknessOptions.CanSave || IsEditingPrices) return;
            FlushLivePrice();
            var change = BuildChange();
            if (change.RequiresPriceCalculation || NeedsPricePreview)
            {
                var result = await new CuttingPriceService(_store).CalculateResultAsync(change.PriceRequest);
                if (result.Quote is not { } quote)
                { _quote = null; PriceText = "—"; PriceBreakdown = ""; Error = result.Error; RefreshChanges(); return; }
                if (change.ExpectedQuote is { } expected && expected != quote)
                { ShowQuote(quote); Error = "Прайс изменился. Проверьте новую цену и повторите сохранение."; return; }
                if (_quote is null)
                {
                    ShowQuote(quote);
                    change = BuildChange();
                }
            }
            SavedRecord = await _store.UpdateAsync(change);
            Saved?.Invoke(this, EventArgs.Empty);
        }
        catch (OrderEditPriceChangedException exception) { ShowQuote(exception.Quote); Error = exception.Message; }
        catch (Exception exception)
        {
            Error = AppDiagnostics.Describe(DiagnosticOperation.OrderEdit, exception, "Не удалось сохранить заявку."); AccessHostHelp.ShowFor(exception);
            if (exception is OrderWriteException { RequiresReload: true }) { RequiresReload = true; ReloadRequired?.Invoke(this, EventArgs.Empty); }
        }
    }

    public void AcceptCurrentSnapshot(OrderRecord? current)
    {
        _quote = null; PriceText = StoredPriceText; PriceBreakdown = ""; _refreshPriceRequested = false; _pricePreview?.Cancel();
        RefreshChanges();
        if (current is null) { Error = "Заявка удалена. Правки сохранены в форме."; RequiresReload = true; return; }
        if (current == _expected) { RequiresReload = false; HasCurrentVersion = false; Error = null; return; }
        _current = current; HasCurrentVersion = true; RequiresReload = true;
        var actual = FieldValues(current);
        var differences = Fields.Select((field, index) => (field, value: actual[index])).Where(pair => pair.value != _initial[pair.field])
            .Select(pair => $"{pair.field.Label}: {Display(_initial[pair.field])} → {Display(pair.value)}" +
                (pair.field.Value != _initial[pair.field] ? $"\nВ форме: {Display(pair.field.Value)}" : "")).ToList();
        if (current.Price != _expected.Price) differences.Add($"Стоимость: {StoredPriceText} → {current.Price?.ToString("N2", CultureInfo.GetCultureInfo("ru-RU")) ?? "—"} ₽");
        if (current.DeliveryNumber != _expected.DeliveryNumber) differences.Add($"БЛЦБ: {Display(_expected.DeliveryNumber)} → {Display(current.DeliveryNumber)}");
        if (current.Color != _expected.Color) differences.Add("Цвет изменён");
        if (current.Annotations != _expected.Annotations)
        {
            if (current.Annotations?.HasBending != _expected.Annotations?.HasBending) differences.Add($"Гибка: {(_expected.Annotations?.HasBending == true ? "Да" : "Нет")} → {(current.Annotations?.HasBending == true ? "Да" : "Нет")}");
            if (current.Annotations?.Comment != _expected.Annotations?.Comment) differences.Add($"Комментарий: {Display(_expected.Annotations?.Comment)} → {Display(current.Annotations?.Comment)}");
        }
        Comparison = differences.Count > 0 ? string.Join("\n\n", differences) : "Обновлены другие данные заявки";
        Error = "Заявка изменена другим пользователем. Проверьте различия.";
    }

    [RelayCommand]
    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (!RequiresReload || SaveCommand.IsRunning || CalculateCommand.IsRunning || IsImporting || HasPendingImport) return;
        try { AcceptCurrentSnapshot(await _refresh(cancellationToken)); }
        catch (Exception exception) { Error = AppDiagnostics.Describe(DiagnosticOperation.OrderRead, exception, "Не удалось обновить заявку."); AccessHostHelp.ShowFor(exception); }
    }

    [RelayCommand]
    private void Rebase()
    {
        if (_current is null || IsBusy || HasPendingImport) return;
        var edits = Fields.Where(field => field.Value != _initial[field] || ImportPreview is not null && ImportFields.Values.Contains(field)).ToDictionary(field => field, field => field.Value);
        var bendingEdited = Annotations.HasBending != (_expected.Annotations?.HasBending ?? false); var bending = Annotations.HasBending;
        var commentEdited = Annotations.Comment != (_expected.Annotations?.Comment ?? ""); var comment = Annotations.Comment;
        _expected = _current; Populate(_current);
        if (bendingEdited) Annotations.HasBending = bending;
        if (commentEdited) Annotations.Comment = comment;
        foreach (var (field, value) in edits) field.Value = value;
        if (ImportPreview is not null) _timeEdited = true;
        OnPropertyChanged(nameof(HasCuttingTimeNotice));
        _current = null; HasCurrentVersion = false; RequiresReload = false; Error = "Проверьте изменения и рассчитайте цену.";
        OnPropertyChanged(nameof(StoredPriceText)); OnPropertyChanged(nameof(MetadataText));
    }
}

public sealed record OrderFieldChange(string Label, string Before, string After);

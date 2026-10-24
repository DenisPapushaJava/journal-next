using Jornal.Infrastructure.Diagnostics;
using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jornal.Core.Orders;
using Jornal.Core.Pricing;

namespace Jornal.Desktop.ViewModels;

public partial class NewOrderViewModel : ObservableObject
{
    private readonly IManualOrderCreator _creator;
    private bool _submitted;
    private CuttingPriceQuote? _quote;

    public NewOrderViewModel(IManualOrderCreator creator, bool isLocalCopy = false, bool isDatabase = false, int initialColor = OrderColors.None, TimeProvider? priceTimeProvider = null)
    {
        Fields = Array.AsReadOnly<OrderEntryField>([OrderDate, SpecificationNumber, Manager, Customer,
            CuttingType, Material, Thickness, SheetSize, SheetCount, Weight, CutLength, Coefficient, MaterialId, Gas, CuttingTime]);
        _creator = creator; _priceTimeProvider = priceTimeProvider ?? TimeProvider.System;
        InitialColor = initialColor;
        Annotations.PropertyChanged += (_, e) =>
        { if (e.PropertyName is nameof(OrderAnnotationsEditor.HasBending) or nameof(OrderAnnotationsEditor.Comment)) { if (_submitted) Validate(); NotifyBatchState(); } };
        IsLocalCopy = isLocalCopy;
        IsDatabase = isDatabase;
        foreach (var field in Fields) field.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(OrderEntryField.NeedsReview)) { OnPropertyChanged(nameof(ImportDiagnostics)); OnPropertyChanged(nameof(HasImportDiagnostics)); SaveCommand.NotifyCanExecuteChanged(); NotifyBatchState(); return; }
            if (e.PropertyName != nameof(OrderEntryField.Value)) return;
            if (field == MaterialId) field.Error = null;
            if (_submitted) Validate();
            if (IsBatchMode && (field == Manager || field == Customer)) SyncBatchSharedFields();
            if (field == CuttingType || field == Thickness || field == CutLength || field == Coefficient || field == MaterialId)
            { InvalidateLivePrice(); }
        };
        SaveCommand.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(IAsyncRelayCommand.IsRunning)) return;
            OnPropertyChanged(nameof(IsBusy)); CalculatePriceCommand.NotifyCanExecuteChanged(); NotifyBatchState();
        };
        CalculatePriceCommand.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(IAsyncRelayCommand.IsRunning)) return;
            OnPropertyChanged(nameof(IsBusy)); SaveCommand.NotifyCanExecuteChanged(); NotifyBatchState();
        };
        CuttingType.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(OrderEntryField.Value)) { OnPropertyChanged(nameof(IsHansLaser)); OnPropertyChanged(nameof(HasPricing)); CalculatePriceCommand.NotifyCanExecuteChanged(); }
        };
        InitializeThicknessOptions(); InitializePricePreview();
        foreach (var field in new[] { CuttingType, MaterialId }) field.PropertyChanged += (_, e) =>
        { if (!_applyingThicknessImport && e.PropertyName == nameof(OrderEntryField.Value)) ThicknessOptions.SourceChanged(); };
    }

    public int InitialColor { get; }
    public bool IsLocalCopy { get; }
    public bool IsDatabase { get; }
    public AccessHostHelpViewModel AccessHostHelp { get; } = new();
    partial void OnPriceErrorChanged(string? value)
    {
        if (value is null) { HasPriceValidationError = false; _priceFailureKind = null; }
        if (value is null && SaveError is null) AccessHostHelp.ShowFor(null);
        SaveCommand.NotifyCanExecuteChanged();
    }
    partial void OnSaveErrorChanged(string? value) { if (value is null && PriceError is null) AccessHostHelp.ShowFor(null); }
    private IPriceReader? PriceReader => _creator as IPriceReader ??
        (SelectedMachine is not null ? (_creator as Jornal.Infrastructure.Demo.DemoOrderStore)?.Prices : null);
    public bool HasPricing => PriceReader is not null;
    public bool IsBusy => SaveCommand.IsRunning || CalculatePriceCommand.IsRunning || IsImporting || IsBatchReading || IsEditingPrices;
    [ObservableProperty] private string _priceText = "—";
    [ObservableProperty] private string _priceBreakdown = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasPriceError))] private string? _priceError;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(SaveCommand))] private bool _hasPriceValidationError;
    public bool HasPriceError => PriceError is not null;
    public string Heading => IsBatchMode ? "Пакетный импорт" : "Новая заявка";
    public string WindowTitle => IsBatchMode ? "Пакетный импорт · " + StorageDescription : IsLocalCopy ? "Новая заявка · Локальная копия" : IsDatabase ? "Новая заявка · MDB" : "Новая заявка · Деморежим";
    public string StorageDescription => IsLocalCopy ? "Локальная тестовая копия" :
        IsDatabase ? "MDB" : "Деморежим · без сохранения";
    public string SaveLabel => IsBatchMode ? "Добавить " + IncludedCount + " " + (IncludedCount % 100 is >= 11 and <= 14 ? "заявок" : IncludedCount % 10 == 1 ? "заявку" : IncludedCount % 10 is >= 2 and <= 4 ? "заявки" : "заявок") : IsLocalCopy || IsDatabase ? "Добавить заявку" : "Добавить в деможурнал";

    public OrderEntryField OrderDate { get; } = new("Дата *") { IsInputEnabled = false };
    public OrderEntryField SpecificationNumber { get; } = new("№ спецификации *");
    public OrderEntryField Manager { get; } = new("Менеджер *");
    public OrderEntryField Customer { get; } = new("Заказчик *");
    public OrderEntryField CuttingType { get; } = new("Вид резки / станок *", choices:
        ["Лазер", "Плазма", "Газо-кислородная", "HansLaser"]);
    public OrderEntryField Material { get; } = new("Материал *") { UseEditableSuggestions = true };
    public OrderEntryField Thickness { get; } = new("Толщина, мм *");
    public OrderEntryField SheetSize { get; } = new("Размер листа, мм *");
    public OrderEntryField SheetCount { get; } = new("Количество листов *");
    public OrderEntryField Weight { get; } = new("Вес, кг *");
    public OrderEntryField CutLength { get; } = new("Длина реза, м *");
    public OrderAnnotationsEditor Annotations { get; } = new();
    public OrderEntryField CuttingTime { get; } = new("Время резки, ч:мин");
    public OrderEntryField Coefficient { get; } = new("Коэффициент *", value: "1");
    public OrderEntryField MaterialId { get; } = new("ID материала HansLaser");
    public OrderEntryField Gas { get; } = new("Газ HansLaser");
    public bool IsHansLaser => CuttingType.Value == "HansLaser" || SelectedMachine?.SpecificationProfileId == CuttingMachine.HansProfile;
    public IReadOnlyList<OrderEntryField> Fields { get; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasSaveError))] private string? _saveError;
    [ObservableProperty] private bool _hasValidationErrors;
    [ObservableProperty] private bool _isSaved;
    public bool HasSaveError => SaveError is not null;
    public OrderSummary? CreatedOrder { get; private set; }
    public IReadOnlyList<OrderSummary> CreatedOrders { get; private set; } = [];
    public event EventHandler? Saved;
    public event EventHandler? ReloadRequired;
    [ObservableProperty] private bool _requiresReload;
    partial void OnRequiresReloadChanged(bool value) { SaveCommand.NotifyCanExecuteChanged(); NotifyBatchState(); }

    public void ResumeAfterReload()
    {
        RequiresReload = false;
        if (IsBatchMode) UpdateDuplicateWarnings();
        SaveError = IsBatchMode ? "Ввод восстановлен. Перед повторным добавлением проверьте список: пакет мог сохраниться." : "Ввод восстановлен. Перед повторным добавлением проверьте список: предыдущая заявка могла сохраниться.";
    }

    private bool CanSave() => (!IsBatchMode ? !Material.NeedsReview && !HasPriceValidationError && ThicknessOptions.CanSave : CanSaveBatch) && !IsSaved && !RequiresReload && !CalculatePriceCommand.IsRunning && !IsImporting && !HasPendingImport && !IsEditingPrices;

    private bool CanCalculatePrice() => !RequiresReload && _thicknessSource.IsAvailable && !IsSaved && !SaveCommand.IsRunning && !IsImporting && !HasPendingImport && !IsEditingPrices;

    [RelayCommand(CanExecute = nameof(CanCalculatePrice))]
    private async Task CalculatePriceAsync(CancellationToken cancellationToken)
    {
        if (!CanCalculatePrice()) return;
        AppDiagnostics.Current?.Mark(DiagnosticOperation.PriceRead);
        PriceError = null; AccessHostHelp.ShowFor(null);
        try
        {
            await WaitForThicknessOptionsAsync(cancellationToken);
            await RefreshThicknessOptionsAsync(true, cancellationToken);
            if (IsBatchMode)
                foreach (var item in BatchItems.Where(item => item.IsIncluded && item.HasParameters)) item.Form.RefreshLivePrice(true);
            else RefreshLivePrice(true);
            if (RequiresImportedHansFields && MaterialId.Value.Trim() is not ("-1" or "0" or "1" or "2"))
                MaterialId.Error = PriceError = "Проверьте ID материала HansLaser: без него нельзя выбрать прайс.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        { PriceError = AppDiagnostics.Describe(DiagnosticOperation.PriceRead, exception, "Не удалось обновить тарифы."); AccessHostHelp.ShowFor(exception); }
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (SaveCommand.IsRunning || IsSaved || RequiresReload) return;
        AppDiagnostics.Current?.Mark(DiagnosticOperation.OrderCreate);
        await WaitForThicknessOptionsAsync();
        if (!CanSave()) return;
        if (IsBatchMode) { await SaveBatchAsync(); return; }
        _submitted = true;
        SaveError = null;
        AccessHostHelp.ShowFor(null);
        if (!Validate()) return;
        FlushLivePrice();
        var draft = BuildDraft();
        try
        {
            if (!await CheckPriceAsync(draft)) { if (!HasPriceValidationError && SaveError is null) SaveError = PriceError; return; }
            draft = BuildDraft();
            CreatedOrder = await _creator.CreateAsync(draft);
        }
        catch (OrderPriceChangedException exception)
        {
            ShowQuote(exception.Quote);
            SaveError = exception.Message;
            return;
        }
        catch (Exception exception)
        {
            SaveError = AppDiagnostics.Describe(DiagnosticOperation.OrderCreate, exception,
                "Не удалось добавить заявку. Введённые данные сохранены в форме — попробуйте ещё раз.");
            AccessHostHelp.ShowFor(exception);
            if (exception is OrderWriteException { RequiresReload: true })
            { RequiresReload = true; ReloadRequired?.Invoke(this, EventArgs.Empty); }
            return;
        }
        CreatedOrders = [CreatedOrder!];
        IsSaved = true;
        SaveCommand.NotifyCanExecuteChanged();
        CalculatePriceCommand.NotifyCanExecuteChanged();
        Saved?.Invoke(this, EventArgs.Empty);
    }

    private async Task<bool> CheckPriceAsync(ManualOrderDraft draft, IPriceReader? priceReader = null)
    {
        if ((priceReader ?? PriceReader) is not { } reader) return true;
        try
        {
            var result = await new CuttingPriceService(reader).CalculateResultAsync(draft.PriceRequest);
            if (result.Quote is { } quote)
            {
                if (draft.ExpectedQuote is { } expected && expected != quote)
                { ShowQuote(quote); SaveError = "Прайс изменился. Проверьте новую цену и повторите сохранение."; return false; }
                if (_quote is null) ShowQuote(quote);
                PriceError = null; return true;
            }
            _priceFailureKind = result.Failure; HasPriceValidationError = true; PriceError = result.Error;
        }
        catch (Exception exception)
        {
            PriceError = AppDiagnostics.Describe(DiagnosticOperation.PriceRead, exception, "Не удалось проверить цену. Повторите сохранение.");
            AccessHostHelp.ShowFor(exception);
        }
        _quote = null; PriceText = "—"; PriceBreakdown = "";
        return false;
    }

    internal ManualOrderDraft BuildDraft()
    {
        DateOnly.TryParseExact(OrderDate.Value.Trim(), "dd.MM.yyyy", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var date);
        TryDecimal(Thickness.Value, out var thickness);
        TryDecimal(Coefficient.Value, out var coefficient);
        int.TryParse(CutLength.Value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var length);
        return new ManualOrderDraft(date, SpecificationNumber.Value.Trim(), Manager.Value.Trim(),
            Customer.Value.Trim(), CuttingType.Value, Material.Value.Trim(), thickness, SheetSize.Value.Trim(),
            SheetCount.Value.Trim(), length, Weight.Value.Trim(), coefficient,
            IsHansLaser ? MaterialId.Value.Trim() : "", IsHansLaser ? Gas.Value.Trim() : "")
            { Machine = SelectedMachine, InitialColor = InitialColor, ExpectedQuote = _quote, PartCount = ImportPreview?.PartCount, PunchCount = ImportPreview?.PunchCount,
                HasBending = Annotations.HasBending, Comment = Annotations.Comment,
                CuttingDurationMinutes = CuttingDuration.TryParse(CuttingTime.Value, out var minutes) ? minutes : null };
    }

    internal bool ValidateBatchParameters()
    {
        Validate();
        return Annotations.Validate() && !Fields.Where(field => field != Manager && field != Customer).Any(field => field.HasError);
    }

    private void ShowQuote(CuttingPriceQuote quote)
    {
        _pricePreview.Cancel(); _quote = quote;
        var culture = CultureInfo.GetCultureInfo("ru-RU");
        PriceText = quote.StoredPrice.ToString("N0", culture) + " ₽";
        PriceBreakdown = $"{quote.RublesPerMeter.ToString("N0", culture)} ₽/м × {quote.CutLengthMeters} м × {quote.Coefficient.ToString(culture)}";
    }

    private bool Validate()
    {
        foreach (var field in Fields)
        {
            if (!IsHansLaser && (field == MaterialId || field == Gas))
            {
                field.Error = null;
                continue;
            }
            var value = field.Value.Trim();
            field.Error = value.Length > 255 ? "Не более 255 символов." :
                field != MaterialId && field != Gas && field != CuttingTime && value.Length == 0 ? "Заполните поле." : null;
        }
        if (OrderDate.Error is null && !DateOnly.TryParseExact(OrderDate.Value.Trim(), "dd.MM.yyyy",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            OrderDate.Error = "Введите существующую дату: ДД.ММ.ГГГГ.";
        if (CuttingType.Error is null && !CuttingType.Choices!.Contains(CuttingType.Value))
            CuttingType.Error = "Выберите вид резки.";
        if (Jornal.Desktop.Import.SpecificationMachineBinding.Validate(ImportPreview, CuttingType.Value, SelectedMachine) is { } importError)
            CuttingType.Error = importError;
        if (Material.Error is null && SelectedMachine?.SpecificationProfileId != CuttingMachine.WaterjetProfile && Material.Value.Trim().Any(char.IsWhiteSpace))
            Material.Error = "Название материала должно быть без пробелов.";
        if (SheetCount.Error is null && !OrderSheetCount.IsValid(SheetCount.Value.Trim()))
            SheetCount.Error = OrderSheetCount.ValidationMessage;
        if (Thickness.Error is null)
        {
            if (!TryDecimal(Thickness.Value, out var thickness) || thickness <= 0)
                Thickness.Error = "Введите положительное число; дробь через запятую или точку.";
        }
        if (CutLength.Error is null && !int.TryParse(CutLength.Value.Trim(), NumberStyles.None,
            CultureInfo.InvariantCulture, out _))
            CutLength.Error = "Укажите целое число метров.";
        if (Coefficient.Error is null && (!TryDecimal(Coefficient.Value, out var coefficient) || coefficient <= 0))
            Coefficient.Error = "Коэффициент должен быть положительным числом.";
        if (RequiresImportedHansFields)
        {
            if (MaterialId.Value.Trim() is not ("-1" or "0" or "1" or "2"))
                MaterialId.Error = "Проверьте ID: 0 — сталь, 1 — нержавеющая, 2 — цветной, -1 — стандартный прайс.";
            if (string.IsNullOrWhiteSpace(Gas.Value)) Gas.Error = "Проверьте газ по спецификации.";
        }
        if (!CuttingDuration.TryParse(CuttingTime.Value, out _)) CuttingTime.Error = CuttingDuration.ValidationMessage;
        var annotationsValid = Annotations.Validate();
        HasValidationErrors = !annotationsValid || Fields.Any(field => field.HasError);
        return !HasValidationErrors;
    }

    private static bool TryDecimal(string value, out decimal number) => decimal.TryParse(value.Trim().Replace(',', '.'),
        NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out number);

    private bool RequiresImportedHansFields => IsHansLaser && ImportPreview?.Report.Profile == Jornal.Parsing.Specifications.HansPdfSpecificationParser.Profile;
}

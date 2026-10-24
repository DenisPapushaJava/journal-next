using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Jornal.Workflow.Core;
using Jornal.Core.Orders;
using Jornal.Workflow.Infrastructure;

namespace Jornal.Workflow.Desktop;

public partial class SpecificationDraft : ObservableObject
{
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("ru-RU");
    public Guid Id { get; init; } = Guid.NewGuid();
    public SpecificationRevision? Saved { get; }
    public string? SourcePath { get; set; }
    private readonly string? _nativeProjectPath;
    public string? FolderPath => Path.GetDirectoryName(_nativeProjectPath ?? SourcePath);
    public bool HasFolder => !string.IsNullOrWhiteSpace(FolderPath);
    public string FolderHint => HasFolder ? "Открыть папку спецификации" : "У спецификации пока нет файла и папки";
    public string? ImportProfile { get; set; }
    public ReportFileSnapshot? IncomingReport { get; set; }
    public string? ImportNotice { get; init; }
    public bool HasImportNotice => !string.IsNullOrWhiteSpace(ImportNotice);
    public bool IsNew => Saved is null;
    private readonly string? _importedMaterial;
    private bool _loading;
    private CuttingRate? _rate;
    public Guid MachineId { get; private set; }
    public void UseMachine(WorkflowMachine? machine)
    {
        var id = machine?.Id ?? Guid.Empty;
        if (MachineId == id && (machine is null || Machine == machine.Name)) return;
        MachineId = id;
        if (machine is not null && Machine != machine.Name)
        { var wasLoading = _loading; _loading = true; Machine = machine.Name; _loading = wasLoading; }
        Recalculate();
    }
    public IReadOnlyList<string> MachineSuggestions { get; private set; } = [];
    public IReadOnlyList<string> MaterialGroupSuggestions { get; private set; } = [];
    public IReadOnlyList<string> ThicknessSuggestions { get; private set; } = [];
    public void SetPriceSuggestions(IReadOnlyList<string> machines, IReadOnlyList<string> groups, IReadOnlyList<string> thicknesses)
    {
        MachineSuggestions = machines; MaterialGroupSuggestions = groups; ThicknessSuggestions = thicknesses;
        base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(MachineSuggestions)));
        base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(MaterialGroupSuggestions)));
        base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(ThicknessSuggestions)));
    }
    [ObservableProperty] private string _number = "";
    [ObservableProperty] private string _date = "";
    [ObservableProperty] private string _machine = "Лазер";
    [ObservableProperty] private string _material = "";
    [ObservableProperty] private string _materialGroup = "Базовая";
    [ObservableProperty] private bool _usesMaterialCategories;
    [ObservableProperty] private string _thickness = "";
    [ObservableProperty] private string _blankSize = "";
    [ObservableProperty] private string _quantity = "1";
    [ObservableProperty] private string _length = "";
    [ObservableProperty] private string _pierces = "";
    [ObservableProperty] private string _weight = "";
    [ObservableProperty] private string _duration = "";
    [ObservableProperty] private string _coefficient = "1";
    [ObservableProperty] private bool _hasBending;
    [ObservableProperty] private string? _comment;
    [ObservableProperty] private bool _materialNeedsReview;
    [ObservableProperty] private bool _materialConfirmed;
    [ObservableProperty] private string _pricePerMeter = "";
    [ObservableProperty] private string _pricePerPierce = "";
    [ObservableProperty] private string? _calculationError;
    public CuttingCalculation? Quote { get; private set; }
    public string Caption => string.IsNullOrWhiteSpace(Number) ? "Новая спецификация" : Number;
    public string VersionText => Saved is null ? "Не сохранена" : $"Версия {Saved.Number}";
    public string TotalText => Quote is { } q ? Money(q.TotalCost) + " ₽" : "Не рассчитано";
    public string Breakdown => Quote is { } q ? $"Метры: {Money(q.LengthCost)} ₽\nПробивки: {Money(q.PierceCost)} ₽\nКоэффициент: {Coefficient}" : "";
    public string SourceName => SourcePath is null ? "Ручной ввод" : Path.GetFileName(SourcePath);
    public bool IsDirty
    {
        get
        {
            if (Saved is null) return true;
            try { return Number != Saved.DocumentNumber || DocumentDate() != Saved.DocumentDate || !Parameters().SameInputs(Saved.Parameters) || Quote != Saved.Calculation ||
                IncomingReport is { } report && report.Sha256 != Saved.Report?.Sha256; }
            catch (WorkflowValidationException) { return true; }
        }
    }
    public bool CanSave => Quote is not null && IsDirty && (!MaterialNeedsReview || MaterialConfirmed);

    public SpecificationDraft() { }
    public SpecificationDraft(WorkflowSpecification specification, string? nativeProjectPath = null)
    {
        _nativeProjectPath = nativeProjectPath;
        _loading = true; Id = specification.Id; Saved = specification.Current;
        var r = Saved; var p = r.Parameters;
        SourcePath = r.SourcePath; ImportProfile = r.ImportProfile;
        Number = r.DocumentNumber; Date = r.DocumentDate.ToString("dd.MM.yyyy", Culture);
        MachineId = p.MachineId; Machine = p.Machine; Material = p.Material; MaterialGroup = p.MaterialGroup; Thickness = N(p.ThicknessMm);
        BlankSize = p.BlankSize; Quantity = N(p.BlankQuantity); Length = N(p.CutLengthMeters);
        Pierces = p.PierceCount?.ToString(Culture) ?? ""; Weight = p.WeightKg is { } w ? N(w) : "";
        Duration = p.DurationSeconds is { } seconds ? FormatDuration(seconds) : "";
        Coefficient = N(p.ManualCoefficient); HasBending = p.HasBending; Comment = p.Comment;
        _rate = new(r.Calculation.RateId, p.Machine, p.MaterialGroup, p.ThicknessMm, r.Calculation.PricePerMeter, r.Calculation.PricePerPierce) { MachineId = p.MachineId };
        PricePerMeter = N(_rate.PricePerMeter); PricePerPierce = N(_rate.PricePerPierce!.Value);
        _loading = false; Recalculate();
    }
    public SpecificationDraft(string? importedMaterial, bool needsReview) : this()
    { _importedMaterial = importedMaterial; MaterialNeedsReview = needsReview; }
    partial void OnMaterialChanged(string value)
    { if (!_loading && _importedMaterial is not null && value != _importedMaterial) MaterialNeedsReview = false; }
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_loading || e.PropertyName is nameof(Caption) or nameof(VersionText) or nameof(TotalText) or nameof(Breakdown) or nameof(IsDirty) or nameof(CanSave) or nameof(CalculationError)) return;
        if (e.PropertyName == nameof(Number)) base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(Caption)));
        Recalculate();
    }
    public CuttingParameters Parameters() => new(Machine?.Trim() ?? "", Material?.Trim() ?? "", MaterialGroup ?? "", Decimal(Thickness, "толщину"), BlankSize ?? "",
        Decimal(Quantity, "количество"), Decimal(Length, "длину реза"), string.IsNullOrWhiteSpace(Pierces) ? null : Integer(Pierces),
        string.IsNullOrWhiteSpace(Weight) ? null : Decimal(Weight, "вес"), ParseDuration(Duration), Decimal(Coefficient, "коэффициент"),
        HasBending, string.IsNullOrWhiteSpace(Comment) ? null : Comment.Trim(), MaterialNeedsReview && !MaterialConfirmed) { MachineId = MachineId };
    public DateOnly DocumentDate() => DateOnly.TryParseExact(Date, "dd.MM.yyyy", Culture, DateTimeStyles.None, out var date)
        ? date : throw new WorkflowValidationException("Укажите дату спецификации в формате ДД.ММ.ГГГГ.");
    public CuttingRate Rate()
    {
        if (_rate is null || _rate.MachineId != MachineId || _rate.MaterialGroup != MaterialGroup || _rate.ThicknessMm != Decimal(Thickness, "толщину") ||
            _rate.PricePerMeter != Decimal(PricePerMeter, "цену метра") || _rate.PricePerPierce != Decimal(PricePerPierce, "цену пробивки"))
            throw new WorkflowValidationException("Сначала сохраните тариф или обновите его из прайса.");
        return _rate;
    }
    public CuttingRate NewRate() => new(Guid.NewGuid(), Machine?.Trim() ?? "", MaterialGroup ?? "", Decimal(Thickness, "толщину"),
        Decimal(PricePerMeter, "цену метра"), string.IsNullOrWhiteSpace(PricePerPierce) ? null : Decimal(PricePerPierce, "цену пробивки")) { MachineId = MachineId };
    public void ApplyRate(CuttingRate? rate)
    {
        if (rate is null)
        {
            _loading = true; _rate = null; PricePerMeter = ""; PricePerPierce = ""; _loading = false;
            Recalculate(); CalculationError = "Для выбранного станка, категории и толщины нет тарифа."; return;
        }
        _loading = true; _rate = rate; PricePerMeter = N(rate.PricePerMeter); PricePerPierce = rate.PricePerPierce is { } p ? N(p) : "";
        _loading = false; Recalculate();
    }
    public void Recalculate()
    {
        if (_loading) return;
        try
        {
            var parameters = Parameters();
            if (_rate is not null && (_rate.MachineId != parameters.MachineId || _rate.MaterialGroup != parameters.MaterialGroup || _rate.ThicknessMm != parameters.ThicknessMm))
                throw new WorkflowValidationException("Для выбранных параметров нет применённого тарифа. Обновите тариф или сохраните новый.");
            var rate = new CuttingRate(_rate?.Id ?? Guid.Empty, parameters.Machine, parameters.MaterialGroup, parameters.ThicknessMm,
                Decimal(PricePerMeter, "цену метра"), string.IsNullOrWhiteSpace(PricePerPierce) ? null : Decimal(PricePerPierce, "цену пробивки")) { MachineId = MachineId };
            Quote = CuttingCalculation.Calculate(parameters, rate); CalculationError = null;
        }
        catch (WorkflowValidationException ex) { Quote = null; CalculationError = ex.Message; }
        base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(TotalText))); base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(Breakdown)));
        base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(IsDirty))); base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(CanSave)));
    }
    public static decimal Decimal(string? value, string label) => decimal.TryParse(value?.Replace(',', '.'), NumberStyles.AllowDecimalPoint,
        CultureInfo.InvariantCulture, out var number) ? number : throw new WorkflowValidationException($"Укажите {label} числом.");
    private static int Integer(string value) => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= 0
        ? n : throw new WorkflowValidationException("Количество пробивок — целое неотрицательное число.");
    private static long? ParseDuration(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (!CuttingDuration.TryParse(text, out var minutes) || minutes is not { } value) throw new WorkflowValidationException("Время резки — ЧЧ:ММ.");
        return checked(value * 60);
    }
    public static string FormatDuration(long seconds) { var minutes = (long)Math.Ceiling(seconds / 60m); return $"{minutes / 60:00}:{minutes % 60:00}"; }
    public static string N(decimal value) => value.ToString("0.############################", Culture);
    public static string Money(decimal value) => value.ToString("#,#0.############################", Culture);
}

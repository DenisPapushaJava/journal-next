using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jornal.Desktop.ViewModels;
using Jornal.Infrastructure.Diagnostics;
using Jornal.Parsing.Documents;
using Jornal.Parsing.Specifications;
using Jornal.Workflow.Core;
using Jornal.Workflow.Infrastructure;

namespace Jornal.Workflow.Desktop;

public sealed record WorkflowOrderRow(WorkflowOrder Order, string Customer, string Manager, string Technologist)
{
    public Employee? Creator { get; init; }
    public IBrush? CreatorBrush => Creator?.ColorArgb is { } color ? new SolidColorBrush(Color.FromUInt32(color)) : null;
    public string CreatorDescription => Creator is { } creator ? "Создал: " + creator.Name +
        (creator.ColorArgb is null ? " · цвет не выбран" : "") : "Автор создания не указан";
    public string Number => Order.DisplayNumber;
    public string NumberDate => Number[..10];
    public string NumberTime => Number[11..13] + ":" + Number[13..15] + ":" + Number[15..17] + Number[17..];
    public string NumberCaption => "Заявка " + Number;
    public string NumberHint => NumberCaption + "\n" + CreatorDescription;
    public string Blcb => Order.Blcb is null ? "—" : $"{Order.Blcb} / {Order.BlcbYear}";
    public int SpecificationCount => Order.ActiveSpecifications.Count();
    public string Total => !Order.ActiveSpecifications.Any() ? "—" : SpecificationDraft.Money(Order.CuttingTotal);
    public string Updated => Order.UpdatedAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm");
    public bool HasDraft { get; init; }
    public string Details => (HasDraft ? "Черновик · " : "") + Manager + " · БЛЦБ " + Blcb;
}
public sealed record RevisionRow(Guid SpecificationId, SpecificationRevision Revision, string Author)
{
    public string Document => Revision.DocumentNumber;
    public int Version => Revision.Number;
    public string At => Revision.SavedAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm");
    public string Machine => Revision.Parameters.Machine;
    public string Price => SpecificationDraft.Money(Revision.Calculation.TotalCost);
}
public sealed record ComparisonRow(string Field, string Before, string After);

public partial class WorkflowViewModel : ObservableObject
{
    private readonly LocalWorkflowWorkspaceStore _store;
    private WorkflowWorkspace _workspace = WorkflowWorkspace.Empty;
    private Guid? _openOrderId;
    private long? _loadedOrderVersion;
    private bool _refreshing;
    public AppUpdatesViewModel Updates { get; }
    public DiagnosticService? Diagnostics { get; }
    public ObservableCollection<WorkflowOrderRow> Orders { get; } = [];
    public ObservableCollection<Employee> Employees { get; } = [];
    public ObservableCollection<SpecificationDraft> Drafts { get; } = [];
    public ObservableCollection<RevisionRow> Revisions { get; } = [];
    public ObservableCollection<ComparisonRow> Comparison { get; } = [];
    public ObservableCollection<string> Events { get; } = [];
    public IReadOnlyList<string> Machines { get; } = ["Лазер", "Плазма", "Газо-кислородная", "HansLaser", "ГАР", "Труборез"];
    public IReadOnlyList<string> MaterialGroups { get; } = ["Базовая", "Нержавеющая сталь", "Цветные металлы"];
    [ObservableProperty] private WorkflowOrderRow? _selectedOrder;
    [ObservableProperty] private SpecificationDraft? _selectedDraft;
    [ObservableProperty] private RevisionRow? _selectedRevision;
    [ObservableProperty] private Employee? _currentEmployee;
    [ObservableProperty] private Employee? _manager;
    [ObservableProperty] private Employee? _technologist;
    [ObservableProperty] private string _newCustomer = "";
    [ObservableProperty] private string _newManager = "";
    [ObservableProperty] private string? _blcb;
    [ObservableProperty] private string _blcbYear = "";
    [ObservableProperty] private bool _isCreating;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string _resultText = "";
    [ObservableProperty] private string _search = "";
    public bool IsJournal => _openOrderId is null;
    public bool IsOrderOpen => _openOrderId is not null;
    public bool HasSelectedDraft => SelectedDraft is not null;
    public bool HasArchivedReport => SelectedRevision?.Revision.Report is not null;
    private bool CanSaveSpecification => CanUse && IsCurrentOrderActive && !IsCurrentCardStale && SelectedDraft?.CanSave == true;
    public bool CanUse => !IsBusy && !Updates.IsApplying;
    public bool HasPendingEdits => HasCurrentCardEdits || _cards.Any(pair => (pair.Key != _openOrderId || IsCreating) && pair.Value.HasEdits);
    public string OrderTitle => CurrentOrder is { } o ? $"Заявка {o.DisplayNumber} · {CustomerName(o.CustomerId)}" : "Заявки";
    public string TotalText => CurrentOrder is { } o && o.ActiveSpecifications.Any() ? "Резка: " + SpecificationDraft.Money(o.CuttingTotal) + " ₽" +
        (o.PendingProjectCount > 0 ? " · не все позиции рассчитаны" : HasUnselectedVariant ? " · есть переделка" : "") : "Резка не рассчитана";
    public string BendingText => CurrentOrder is { } o ? $"Гибка: {o.ActiveSpecifications.Count(s => s.Current.Parameters.HasBending)} спецификаций · цена отдельно" : "";
    private WorkflowOrder? CurrentOrder => _openOrderId is { } id ? _workspace.Orders.SingleOrDefault(o => o.Id == id) : null;

    public WorkflowViewModel(LocalWorkflowWorkspaceStore store, AppUpdatesViewModel updates, DiagnosticService? diagnostics = null)
    { _store = store; Updates = updates; Diagnostics = diagnostics; _preferences = new(Path.Combine(Path.GetDirectoryName(store.FilePath)!, "preferences.json")); }

    [RelayCommand]
    private async Task ReloadAsync()
    {
        if (HasCurrentCardEdits) { Error = "Сохраните или отмените изменения текущей карточки перед обновлением."; return; }
        await RunAsync(async () =>
        {
            _workspace = await _store.ReadAsync();
            try { LoadLocalPreferences(_preferences); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
            { _localPreferences = new(); NotifyEntrySuggestions(); Error = "Не удалось прочитать локальные настройки. Данные заявок доступны; проверьте профиль и списки."; }
            RefreshEmployees(_localPreferences.EmployeeId);
            Manager ??= Employees.FirstOrDefault(); Technologist ??= CurrentEmployee;
            RefreshOrders();
            NotifyPriceCatalog();
            if (CurrentOrder is { } order) LoadOrder(order);
            else if (SelectedOrder is { } selected) LoadOrder(selected.Order);
        });
    }
    [RelayCommand] private void NewOrder()
    {
        if (!CanUse) return;
        if (CurrentEmployee is null) { Error = "Выберите себя: Настройки → Общие → Технолог."; return; }
        if (IsCreating) return;
        CaptureCard();
        IsCreating = true; NewCustomer = ""; NewManager = ""; Error = null;
    }
    [RelayCommand] private void CancelNewOrder()
    {
        IsCreating = false; Error = null;
        if (CurrentOrder is { } order && _cards.TryGetValue(order.Id, out var state)) RestoreCard(order, state);
    }
    [RelayCommand] private async Task CreateOrderAsync()
    {
        if (!CanUse) return;
        await RunAsync(async () =>
        {
            var customer = NewCustomer; var manager = NewManager; var actorId = ActorId();
            var created = await _store.CreateOrderFromNamesAsync(manager, customer, actorId, DateTimeOffset.Now);
            _workspace = created.Workspace; var orderId = created.OrderId; RefreshEmployees(actorId);
            IsCreating = false; IsPlanning = false; _preparedProject = null; Search = "";
            RefreshOrders(); LoadOrder(_workspace.Orders.Single(o => o.Id == orderId)); RefreshOrders();
        });
    }
    [RelayCommand] private void OpenOrder()
    {
        if (SelectedOrder is null || !CanUse) return;
        SwitchCard(SelectedOrder.Order);
    }
    [RelayCommand] private void Back()
    {
        if (HasCurrentCardEdits) { Error = "Сохраните или отмените изменения спецификаций."; return; }
        _openOrderId = null; Drafts.Clear(); SelectedDraft = null; Projects.Clear(); SelectedProject = null; ResultText = ""; IsResultExpanded = false; ResultIsStale = false; NotifyOrder();
    }
    [RelayCommand] private void AddManualSpecification()
    {
        if (!CanUse || !IsCurrentOrderActive) return;
        var draft = new SpecificationDraft(); Drafts.Add(draft); SelectedDraft = draft;
    }
    [RelayCommand] private void CancelDraft()
    {
        if (SelectedDraft is not { } draft || !CanUse) return;
        var saved = CurrentOrder?.Specifications.SingleOrDefault(s => s.Id == draft.Id);
        var index = Drafts.IndexOf(draft);
        if (saved is null) { Drafts.Remove(draft); SelectedDraft = Drafts.FirstOrDefault(); }
        else { Drafts[index] = CreateDraft(saved); SelectedDraft = Drafts[index]; }
        Error = null;
    }
    [RelayCommand] private async Task SaveHeaderAsync()
    {
        if (!CanUse || CurrentOrder is not { IsDeleted: false } order) return;
        await RunAsync(async () =>
        {
            int? year = null;
            if (!string.IsNullOrWhiteSpace(Blcb)) year = int.TryParse(BlcbYear, out var n) ? n : throw new WorkflowValidationException("Укажите год регистрации БЛЦБ.");
            var managerId = Manager?.Id ?? Guid.Empty; var technologistId = Technologist?.Id ?? Guid.Empty; var actorId = ActorId(); var blcb = Blcb; var expected = CardVersion;
            _workspace = await _store.WriteOrderAsync(order.Id, expected, book => WorkflowChanges.UpdateOrder(book, order.Id, expected,
                managerId, technologistId, blcb, year, actorId, DateTimeOffset.UtcNow));
            _loadedOrderVersion = CurrentOrder!.Version;
            RefreshOrders(); RefreshHistory(); NotifyOrder();
        });
    }
    [RelayCommand] private async Task SaveRateAsync()
    {
        if (!CanUse || SelectedDraft is not { } draft) return;
        await RunAsync(async () =>
        {
            var rate = draft.NewRate();
            _workspace = await _store.WriteAsync(_workspace.Version, book => WorkflowChanges.SetRate(book, rate));
            draft.ApplyRate(_workspace.Rates.Single(r => r.MachineId == draft.MachineId && r.MaterialGroup == rate.MaterialGroup && r.ThicknessMm == rate.ThicknessMm));
        });
    }
    [RelayCommand] private void RefreshRate()
    {
        if (!CanUse || SelectedDraft is not { } draft) return;
        try { draft.ApplyRate(_workspace.Rates.SingleOrDefault(r => r.MachineId == draft.MachineId && r.MaterialGroup == draft.MaterialGroup && r.ThicknessMm == SpecificationDraft.Decimal(draft.Thickness, "толщину"))); }
        catch (WorkflowValidationException ex) { Error = ex.Message; }
    }
    [RelayCommand(CanExecute = nameof(CanSaveSpecification))] private async Task SaveSpecificationAsync()
    {
        if (!CanUse || CurrentOrder is not { IsDeleted: false } order || SelectedDraft is not { } draft || !draft.IsDirty) return;
        await RunAsync(async () =>
        {
            var parameters = draft.Parameters(); var rate = draft.Rate(); var date = draft.DocumentDate(); var actorId = ActorId(); var documentNumber = draft.Number;
            var expected = CardVersion;
            AcceptedReport? report = null;
            if (draft.IncomingReport is { } incoming)
            {
                await WorkflowReportFiles.RevalidateAsync(incoming);
                report = draft.Saved?.Report?.Sha256 == incoming.Sha256 ? draft.Saved.Report :
                    await WorkflowReportFiles.ArchiveAsync(incoming, WorkflowReportCandidate.Values(FromReport(incoming.Parsed, incoming.SourcePath, _workspace.Machines)));
            }
            _workspace = await _store.WriteOrderAsync(order.Id, expected, book => WorkflowChanges.SaveSpecification(book, order.Id, expected,
                draft.Id, parameters, documentNumber, date, actorId, DateTimeOffset.UtcNow, draft.SourcePath, draft.ImportProfile, rate, report));
            _loadedOrderVersion = CurrentOrder!.Version;
            var specification = CurrentOrder!.Specifications.Single(s => s.Id == draft.Id); var index = Drafts.IndexOf(draft);
            Drafts[index] = CreateDraft(specification); SelectedDraft = Drafts[index]; MarkResultStale(); RefreshProjects(draft.Id); RefreshOrders(); RefreshHistory(); NotifyOrder();
        });
    }
    [RelayCommand] private void GenerateResult()
    {
        if (CurrentOrder is not { } order || !CanUse) return;
        if (HasCurrentCardEdits) { Error = "Сохраните изменения перед формированием результата."; return; }
        if (order.PendingProjectCount > 0 || HasUnselectedVariant)
        { Error = "Сохраните расчёты всех позиций и выберите варианты для итога."; return; }
        ResultText = string.Join("\n", order.ActiveSpecifications.Select(s => s.Current).OrderBy(r => r.Parameters.ThicknessMm).ThenBy(r => r.Parameters.Material)
            .Select(r => $"{r.Parameters.BlankSize}, Материал: {r.Parameters.Material}, {SpecificationDraft.N(r.Parameters.BlankQuantity)} шт. " +
                $"Вес {r.Parameters.WeightKg?.ToString(CultureInfo.GetCultureInfo("ru-RU")) ?? "—"} кг. Резка {SpecificationDraft.Money(r.Calculation.TotalCost)} ₽. " +
                (r.Parameters.DurationSeconds is { } seconds ? $"Время резки: {SpecificationDraft.FormatDuration(seconds)} " : "") +
                $"резка {_workspace.Machines.SingleOrDefault(m => m.Id == r.Parameters.MachineId)?.Name ?? r.Parameters.Machine}"));
        if (ResultText.Length > 0) ResultText += "\n";
        ResultIsStale = false;
    }

    public async Task ImportFilesAsync(IReadOnlyList<string> paths, CancellationToken token)
    {
        if (!CanUse || CurrentOrder is null) { Error = "Сначала создайте или откройте заявку."; return; }
        if (paths.Count > 100) { Error = "В одном пакете — до 100 спецификаций."; return; }
        await RunAsync(async () =>
        {
            var failures = new List<string>();
            foreach (var path in paths)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var snapshot = await Task.Run(() => WorkflowReportFiles.ReadAsync(path, token), token);
                    if (CurrentOrder!.Projects.Any(project => WorkflowReportFiles.Matches(snapshot, project,
                        CurrentOrder.Specifications.SingleOrDefault(s => s.Id == project.Id), _workspace.Machines.Single(m => m.Id == project.MachineId))))
                        throw new WorkflowValidationException("Этот отчёт относится к подготовленному проекту. Нажмите «Обновить спецификации».");
                    var draft = FromReport(snapshot.Parsed, path, _workspace.Machines); draft.IncomingReport = snapshot;
                    ConfigurePriceSuggestions(draft); TryApplyCatalogRate(draft);
                    Drafts.Add(draft); SelectedDraft ??= draft;
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
                { failures.Add(Path.GetFileName(path) + ": " + (ex is DocumentRejectedException or WorkflowValidationException ? ex.Message : "не удалось прочитать файл")); }
            }
            if (failures.Count > 0) Error = string.Join("\n", failures.Take(4)) + (failures.Count > 4 ? $"\nЕщё файлов с ошибками: {failures.Count - 4}" : "");
        });
    }
    internal static SpecificationDraft FromReport(SpecificationParseResult report, string path, IReadOnlyList<WorkflowMachine>? machines = null, Guid? id = null)
    {
        if (report.SheetLayouts.Select(l => l.Value.Dimensions).Distinct().Count() > 1)
            throw new WorkflowValidationException("В спецификации разные размеры заготовок. Этот случай будет добавлен следующим этапом.");
        var review = report.Profile == WaterjetPdfSpecificationParser.Profile || report.Issues.Any(i => i.Code == "MaterialNeedsReview");
        var dimensions = report.Dimensions?.Value ?? report.SheetLayouts.FirstOrDefault()?.Value.Dimensions;
        string Number(decimal? n) => n is { } v ? SpecificationDraft.N(v) : "";
        var machineName = report.Profile == WaterjetPdfSpecificationParser.Profile ? "ГАР" : report.CuttingType?.Value ?? "";
        var matches = machines?.Where(m => m.ImportProfile == report.Profile).ToArray() ?? [];
        var bound = matches.Where(m => m.LegacyKey == "legacy:" + machineName).ToArray();
        if (bound.Length == 0) bound = matches.Where(m => m.LegacyName == machineName).ToArray();
        if (bound.Length == 0) bound = matches.Where(m => m.Name == machineName).ToArray();
        var ambiguousMachine = bound.Length > 1 || bound.Length == 0 && matches.Length > 1;
        if (bound.Length == 1) machineName = bound[0].Name;
        else if (matches.Length == 1) machineName = matches[0].Name;
        if (ambiguousMachine) machineName = "";
        var draft = new SpecificationDraft(report.Material?.Value, review)
        {
            Id = id ?? Guid.NewGuid(),
            SourcePath = Path.GetFullPath(path), ImportProfile = report.Profile,
            ImportNotice = string.Join("\n", report.Issues.Where(i => i.Code != "MaterialNeedsReview").Take(4).Select(i => i.Message)
                .Concat(ambiguousMachine ? ["Формат подходит нескольким станкам. Выберите станок для расчёта."] : Array.Empty<string>())),
            Number = report.Header?.Value.JournalNumber ?? "",
            Date = report.Header?.Value.OrderDate.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) ?? "",
            Machine = machineName,
            Material = report.Material?.Value ?? "", Thickness = Number(dimensions?.ThicknessMillimeters),
            BlankSize = dimensions is null ? "" : $"Лист {SpecificationDraft.N(dimensions.ThicknessMillimeters)}×{SpecificationDraft.N(dimensions.WidthMillimeters)}×{SpecificationDraft.N(dimensions.LengthMillimeters)}",
            Quantity = report.SheetCount?.Value.ToString(CultureInfo.InvariantCulture) ?? "",
            Length = report.CutLengthMillimeters is { } length ? SpecificationProfileCatalog.Default.NormalizeCutLengthMeters(report.Profile, length.Value).ToString(CultureInfo.InvariantCulture) : "",
            Pierces = report.PunchCount?.Value.ToString(CultureInfo.InvariantCulture) ?? "", Weight = Number(report.WeightKilograms?.Value),
            Duration = report.CuttingDuration is { } t ? SpecificationDraft.FormatDuration((long)Math.Ceiling(t.Value.TotalSeconds)) : "",
            UsesMaterialCategories = report.Profile == HansPdfSpecificationParser.Profile,
            MaterialGroup = report.Profile == HansPdfSpecificationParser.Profile ? report.MaterialId?.Value switch
            { "1" => "Нержавеющая сталь", "2" => "Цветные металлы", "0" or "-1" => "Базовая", _ => "" } : "Базовая"
        };
        // Не используем прежний SpecificationImportPreview: он вычисляет коэффициент по пробивкам.
        draft.MaterialNeedsReview = review; draft.MaterialConfirmed = false;
        return draft;
    }
    private SpecificationDraft CreateDraft(WorkflowSpecification specification) =>
        new(specification, CurrentOrder?.Projects.SingleOrDefault(p => p.Id == specification.Id)?.NativePath);

    private void LoadOrder(WorkflowOrder order)
    {
        var sameOrder = _openOrderId == order.Id;
        if (sameOrder && _loadedOrderVersion != order.Version) MarkResultStale();
        else if (!sameOrder) { ResultText = ""; IsResultExpanded = false; OrderTabIndex = 0; ResultIsStale = false; }
        _loadedOrderVersion = order.Version;
        _openOrderId = order.Id; Drafts.Clear(); foreach (var specification in order.Specifications) Drafts.Add(CreateDraft(specification));
        _refreshing = true;
        try { SelectedOrder = Orders.SingleOrDefault(row => row.Order.Id == order.Id); }
        finally { _refreshing = false; }
        SelectedDraft = Drafts.FirstOrDefault(); Manager = Employees.Single(e => e.Id == order.ManagerId); Technologist = Employees.Single(e => e.Id == order.TechnologistId);
        Blcb = order.Blcb; BlcbYear = order.BlcbYear?.ToString(CultureInfo.InvariantCulture) ?? ""; Error = null; RefreshProjects(); RefreshHistory(); NotifyOrder();
    }
    private void RefreshOrders()
    {
        var selected = _openOrderId ?? SelectedOrder?.Order.Id; _refreshing = true;
        try
        {
            var search = Search.Trim();
            var reference = search.StartsWith("Заявка ", StringComparison.OrdinalIgnoreCase) ? search[7..].Trim() : search;
            reference = reference.Replace(" ", "-", StringComparison.Ordinal).Replace(":", "", StringComparison.Ordinal);
            var exactOrder = _workspace.Orders.FirstOrDefault(o => o.ReferenceNumber == reference);
            Orders.Clear(); foreach (var order in _workspace.Orders.Where(o => !o.IsDeleted).OrderByDescending(o => o.UpdatedAt))
            {
                var row = new WorkflowOrderRow(order, CustomerName(order.CustomerId), EmployeeName(order.ManagerId), EmployeeName(order.TechnologistId))
                {
                    HasDraft = HasDraftForOrder(order.Id),
                    Creator = _workspace.Employees.SingleOrDefault(e => e.Id == order.CreatedById)
                };
                if (exactOrder is not null ? order.Id == exactOrder.Id : search.Length == 0 ||
                    (row.NumberCaption + " " + row.NumberDate + " " + row.NumberTime + " " + row.Blcb + " " + row.Customer + " " + row.Manager + " " + row.Technologist + " " +
                    string.Join(" ", order.Specifications.Select(s => s.Current.DocumentNumber + " " + s.Current.Parameters.Material))).Contains(search, StringComparison.OrdinalIgnoreCase)) Orders.Add(row);
            }
            SelectedOrder = Orders.FirstOrDefault(o => o.Order.Id == selected) ?? (_openOrderId is null ? Orders.FirstOrDefault() : null);
            NotifyProfile();
            OnPropertyChanged(nameof(TrashCaption));
        }
        finally { _refreshing = false; }
    }
    partial void OnSearchChanged(string value) { if (!_refreshing) RefreshOrders(); }
    partial void OnManagerChanged(Employee? value) => NotifyHeaderEdit();
    partial void OnTechnologistChanged(Employee? value) => NotifyHeaderEdit();
    partial void OnBlcbChanged(string? value) => NotifyHeaderEdit();
    partial void OnBlcbYearChanged(string value) => NotifyHeaderEdit();
    partial void OnNewCustomerChanged(string value) => OnPropertyChanged(nameof(HasPendingEdits));
    partial void OnNewManagerChanged(string value) => OnPropertyChanged(nameof(HasPendingEdits));
    partial void OnIsCreatingChanged(bool value) { OnPropertyChanged(nameof(HasPendingEdits)); OnPropertyChanged(nameof(IsCardVisible)); OnPropertyChanged(nameof(IsNoCardSelected)); }
    partial void OnSelectedDraftChanged(SpecificationDraft? oldValue, SpecificationDraft? newValue)
    {
        if (oldValue is not null) oldValue.PropertyChanged -= DraftChanged;
        if (newValue is not null) { newValue.PropertyChanged += DraftChanged; ConfigurePriceSuggestions(newValue); }
        OnPropertyChanged(nameof(HasPendingEdits)); OnPropertyChanged(nameof(HasSelectedDraft)); SaveSpecificationCommand.NotifyCanExecuteChanged();
    }
    private void DraftChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SpecificationDraft.IsDirty)) OnPropertyChanged(nameof(HasPendingEdits));
        if (e.PropertyName == nameof(SpecificationDraft.CanSave)) SaveSpecificationCommand.NotifyCanExecuteChanged();
        if (sender is SpecificationDraft draft && e.PropertyName is nameof(SpecificationDraft.Machine) or nameof(SpecificationDraft.MaterialGroup) or nameof(SpecificationDraft.Thickness))
        {
            if (_catalogUpdating) return;
            if (e.PropertyName == nameof(SpecificationDraft.Machine)) draft.UseMachine(_workspace.Machines.SingleOrDefault(m => m.Name == draft.Machine));
            ConfigurePriceSuggestions(draft);
            if (draft.Saved is null) TryApplyCatalogRate(draft);
        }
    }
    partial void OnSelectedRevisionChanged(RevisionRow? value)
    {
        OnPropertyChanged(nameof(HasArchivedReport));
        Comparison.Clear(); if (value is null) return;
        var spec = CurrentOrder!.Specifications.Single(s => s.Id == value.SpecificationId);
        var before = spec.Revisions.FirstOrDefault(r => r.Number == value.Version - 1); var after = value.Revision;
        void Row(string name, string? b, string a) => Comparison.Add(new(name, b ?? "—", a));
        Row("Станок", before?.Parameters.Machine, after.Parameters.Machine); Row("Материал", before?.Parameters.Material, after.Parameters.Material);
        Row("Толщина, мм", before is null ? null : SpecificationDraft.N(before.Parameters.ThicknessMm), SpecificationDraft.N(after.Parameters.ThicknessMm));
        Row("Длина, м", before is null ? null : SpecificationDraft.N(before.Parameters.CutLengthMeters), SpecificationDraft.N(after.Parameters.CutLengthMeters));
        Row("Пробивки", before?.Parameters.PierceCount?.ToString(), after.Parameters.PierceCount?.ToString() ?? "—");
        Row("Цена метра", before is null ? null : SpecificationDraft.N(before.Calculation.PricePerMeter), SpecificationDraft.N(after.Calculation.PricePerMeter));
        Row("Цена пробивки", before is null ? null : SpecificationDraft.N(before.Calculation.PricePerPierce), SpecificationDraft.N(after.Calculation.PricePerPierce));
        Row("Коэффициент", before is null ? null : SpecificationDraft.N(before.Parameters.ManualCoefficient), SpecificationDraft.N(after.Parameters.ManualCoefficient));
        Row("Гибка", before is null ? null : before.Parameters.HasBending ? "Да" : "Нет", after.Parameters.HasBending ? "Да" : "Нет");
        Row("Стоимость", before is null ? null : SpecificationDraft.Money(before.Calculation.TotalCost), SpecificationDraft.Money(after.Calculation.TotalCost));
    }
    private void RefreshHistory()
    {
        Revisions.Clear(); Events.Clear(); if (CurrentOrder is not { } order) return;
        foreach (var spec in order.Specifications) foreach (var revision in spec.Revisions.OrderByDescending(r => r.Number)) Revisions.Add(new(spec.Id, revision, EmployeeName(revision.AuthorId)));
        foreach (var e in order.Events.Reverse()) Events.Add($"{e.At.ToLocalTime():dd.MM HH:mm} · {EmployeeName(e.ActorId)} · {e.Description}");
        SelectedRevision = Revisions.FirstOrDefault();
    }
    private Guid ActorId() => CurrentEmployee?.Id ?? throw new WorkflowValidationException("Выберите себя: Настройки → Общие → Технолог.");
    private string CustomerName(Guid id) => _workspace.Customers.Single(c => c.Id == id).Name;
    private string EmployeeName(Guid id) => _workspace.Employees.Single(e => e.Id == id).Name;
    private void NotifyOrder()
    {
        foreach (var name in new[] { nameof(IsJournal), nameof(IsOrderOpen), nameof(IsCardVisible), nameof(IsNoCardSelected), nameof(HeaderSummary),
            nameof(OrderTitle), nameof(TotalText), nameof(BendingText), nameof(ProjectsStatus), nameof(IsCurrentOrderActive), nameof(IsCurrentCardStale),
            nameof(HasCardReview), nameof(CardReviewText), nameof(TrashCaption) }) OnPropertyChanged(name);
        SaveSpecificationCommand.NotifyCanExecuteChanged();
    }
    partial void OnIsBusyChanged(bool value) { OnPropertyChanged(nameof(CanUse)); SaveSpecificationCommand.NotifyCanExecuteChanged(); }
    private async Task RunAsync(Func<Task> action)
    {
        if (IsBusy) return; IsBusy = true; Error = null;
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Error = ex switch { WorkflowValidationException or WorkflowConflictException => ex.Message,
                IOException => "Локальные данные недоступны или заняты. Введённые параметры остались в форме.",
                _ => "Не удалось выполнить действие. Введённые параметры остались в форме." };
            Jornal.Infrastructure.Diagnostics.AppDiagnostics.Capture(Jornal.Infrastructure.Diagnostics.DiagnosticOperation.Unexpected, ex);
        }
        finally { IsBusy = false; }
    }
    public void Detach() { if (SelectedDraft is { } draft) draft.PropertyChanged -= DraftChanged; }
    public WorkflowRestartDraft CreateRestartDraft()
    {
        CaptureCard();
        return new(1, _openOrderId, ResultText)
        {
            Results = _cards.Where(pair => pair.Value.Result.Length > 0)
                .Select(pair => new WorkflowRestartResult(pair.Key, pair.Value.Version, pair.Value.Result,
                    pair.Value.ResultExpanded, pair.Value.ResultStale)).ToArray()
        };
    }
    public void RestoreRestartDraft(WorkflowRestartDraft draft)
    {
        foreach (var result in draft.Results ?? [])
            if (_workspace.Orders.Any(o => o.Id == result.OrderId))
                _cards[result.OrderId] = new(result.Version, false, [], null, null, null, null, null, "",
                    result.Text, result.Expanded, result.Stale, new(false, null, "", null, null), LetterOnly: true);
        if (draft.OrderId is { } id && _workspace.Orders.SingleOrDefault(o => o.Id == id) is { } order)
        {
            LoadOrder(order);
            if (_cards.TryGetValue(id, out var state)) RestoreCard(order, state);
        }
        ResultText = draft.ResultText;
    }
}

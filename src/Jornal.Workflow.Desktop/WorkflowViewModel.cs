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

public sealed class WorkflowOrderRow(WorkflowOrder order, string customer, string manager, string technologist) : ObservableObject
{
    public WorkflowOrder Order { get; private set; } = order;
    public string Customer { get; private set; } = customer;
    public string Manager { get; private set; } = manager;
    public string Technologist { get; private set; } = technologist;
    public Employee? Creator { get; set; }
    public IBrush? CreatorBrush => Creator?.ColorArgb is { } color ? new SolidColorBrush(Color.FromUInt32(color)) : null;
    public string CreatorDescription => Creator is { } creator ? "Создал: " + creator.Name +
        (creator.ColorArgb is null ? " · цвет не выбран" : "") : "Автор создания не указан";
    public string Number => Order.DisplayNumber;
    public string NumberDate => Number[..10];
    public string NumberTime => Number[11..13] + ":" + Number[13..15] + ":" + Number[15..17] + Number[17..];
    public string NumberCaption => "Заявка " + Number;
    public string NumberHint => NumberCaption + "\n" + CreatorDescription;
    public string Blcb => Order.Blcb ?? "—";
    public int SpecificationCount => Order.ActiveSpecifications.Count();
    public string Total => !Order.ActiveSpecifications.Any() ? "—" : SpecificationDraft.Money(Order.CuttingTotal);
    public string Updated => Order.UpdatedAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm");
    public bool HasDraft { get; set; }
    public bool IsMissing { get; set; }
    public string SearchDetail { get; set; } = "";
    public bool HasSearchDetail => SearchDetail.Length > 0;
    public string Details => (IsMissing ? "Удалена · локальный черновик · " : HasDraft ? "Черновик · " : "") + Manager + " · БЛЦБ " + Blcb;
    internal void Apply(WorkflowOrderRow incoming)
    {
        var changed = Order.Version != incoming.Order.Version || Customer != incoming.Customer || Manager != incoming.Manager ||
            Technologist != incoming.Technologist || Creator != incoming.Creator || HasDraft != incoming.HasDraft ||
            IsMissing != incoming.IsMissing || SearchDetail != incoming.SearchDetail;
        Order = incoming.Order; Customer = incoming.Customer; Manager = incoming.Manager; Technologist = incoming.Technologist;
        Creator = incoming.Creator; HasDraft = incoming.HasDraft; IsMissing = incoming.IsMissing; SearchDetail = incoming.SearchDetail;
        if (!changed) return;
        foreach (var name in new[] { nameof(Order), nameof(Customer), nameof(Manager), nameof(Technologist), nameof(Creator),
            nameof(CreatorBrush), nameof(CreatorDescription), nameof(Number), nameof(NumberDate), nameof(NumberTime), nameof(NumberCaption),
            nameof(NumberHint), nameof(Blcb), nameof(SpecificationCount), nameof(Total), nameof(Updated), nameof(HasDraft), nameof(IsMissing),
            nameof(SearchDetail), nameof(HasSearchDetail), nameof(Details) }) OnPropertyChanged(name);
    }
}
public partial class WorkflowViewModel : ObservableObject
{
    private WorkflowConnection _connection;
    private IWorkflowWorkspaceStore _store => _connection.Store;
    private WorkflowWorkspace _workspace = WorkflowWorkspace.Empty;
    private Guid? _openOrderId;
    private long? _loadedOrderVersion;
    private WorkflowOrder? _loadedOrderSnapshot;
    private bool _refreshing;
    public AppUpdatesViewModel Updates { get; }
    public DiagnosticService? Diagnostics { get; }
    public ObservableCollection<WorkflowOrderRow> Orders { get; } = [];
    public ObservableCollection<Employee> Employees { get; } = [];
    public ObservableCollection<SpecificationDraft> Drafts { get; } = [];
    public IReadOnlyList<string> MaterialGroups { get; } = ["Базовая", "Нержавеющая сталь", "Цветные металлы"];
    [ObservableProperty] private WorkflowOrderRow? _selectedOrder;
    [ObservableProperty] private SpecificationDraft? _selectedDraft;
    [ObservableProperty] private Employee? _currentEmployee;
    [ObservableProperty] private string _managerName = "";
    [ObservableProperty] private Employee? _technologist;
    [ObservableProperty] private string _newCustomer = "";
    [ObservableProperty] private string _newManager = "";
    [ObservableProperty] private string? _blcb;
    [ObservableProperty] private bool _isCreating;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string _resultText = "";
    [ObservableProperty] private string _search = "";
    public bool IsJournal => _openOrderId is null;
    public bool IsOrderOpen => _openOrderId is not null;
    public bool HasSelectedDraft => SelectedDraft is not null;
    public bool HasAcceptedReport => SelectedDraft?.Saved?.Report is not null;
    private bool CanSaveSpecification => CanUse && IsCurrentOrderActive && !IsCurrentCardStale && SelectedDraft?.CanSave == true;
    public bool CanUse => !IsBusy && !_readingDatabase && !_savingProjectInclusion && !Updates.IsApplying;
    public bool CanBrowseWorkspace => (!IsBusy || _savingProjectInclusion) && !Updates.IsApplying;
    public bool HasPendingEdits => HasCurrentCardEdits || _workspace.Orders.Any(o => o.HasProjectRename) ||
        _cards.Any(pair => ((pair.Key != _openOrderId || IsCreating) && pair.Value.HasEdits) ||
            !_workspace.Orders.Any(o => o.Id == pair.Key) && pair.Value.Result.Length > 0);
    public string OrderTitle => CurrentOrder is { } o ? $"Заявка {o.DisplayNumber} · {CustomerName(o.CustomerId)}" : "Заявки";
    public string TotalText => CurrentOrder is { } o && o.ActiveSpecifications.Any() ? "Резка: " + SpecificationDraft.Money(o.CuttingTotal) + " ₽" +
        (o.PendingProjectCount > 0 ? " · не все позиции рассчитаны" : HasUnselectedVariant ? " · есть переделка" : "") : "Резка не рассчитана";
    public string BendingText => CurrentOrder is { } o ? $"Гибка: {o.ActiveSpecifications.Count(s => s.Current.Parameters.HasBending)} спецификаций · цена отдельно" : "";
    private WorkflowOrder? CurrentOrder => _openOrderId is { } id ? _workspace.Orders.SingleOrDefault(o => o.Id == id) ??
        (_loadedOrderSnapshot?.Id == id ? _loadedOrderSnapshot : null) : null;

    public WorkflowViewModel(IWorkflowWorkspaceStore store, AppUpdatesViewModel updates, DiagnosticService? diagnostics = null,
        WorkflowPreferencesStore? preferences = null, IWorkflowDatabaseService? databases = null)
    {
        _connection = new(store); Updates = updates; Diagnostics = diagnostics;
        _preferences = preferences ?? new(store is WorkflowMdbStore ? WorkflowIdentity.PreferencesPath : Path.Combine(Path.GetDirectoryName(store.FilePath)!, "preferences.json"));
        _databases = databases ?? new WorkflowDatabaseService();
    }

    [RelayCommand]
    private async Task ReloadAsync()
    {
        if (HasCurrentCardEdits && !RequiresDatabaseRefresh) { Error = "Сохраните или отмените изменения текущей карточки перед обновлением."; return; }
        CancelDatabaseSearch();
        await RunAsync(async () =>
        {
            CaptureCard(); var restore = RequiresDatabaseRefresh && _openOrderId is { } oldId ? _cards.GetValueOrDefault(oldId) : null;
            _loadedOrderSnapshot = CurrentOrder; ConfigureDatabaseQuery();
            var workspace = await _store.ReadAsync();
            try { LoadLocalPreferences(_preferences); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
            { Error = "Не удалось прочитать локальные настройки. Данные заявок доступны; проверьте профиль и списки."; }
            _workspace = workspace; _hasLoadedOrders = true;
            if (IsCurrentOrderMissing && string.IsNullOrEmpty(ResultText)) ForgetOpenCard();
            RefreshEmployees(_connection.EmployeeId);
            Technologist ??= CurrentEmployee;
            RefreshOrders();
            NotifyPriceCatalog();
            if (_store is WorkflowMdbStore { RecoveredOrderId: { } recovered } mdb && _workspace.Orders.SingleOrDefault(o => o.Id == recovered) is { } created)
            { IsCreating = false; LoadOrder(created); mdb.AcknowledgeRecovery(); }
            else if (CurrentOrder is { } order) { LoadOrder(order); if (restore is not null) RestoreCard(order, restore); }
            else if (SelectedOrder is { } selected) LoadOrder(selected.Order);
            NotifyDatabase();
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
            if (!IsInPeriod(_workspace.Orders.Single(o => o.Id == orderId))) FilterDate = null;
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
    internal void AddLegacySpecificationDraft()
    {
        if (!CanUse || !IsCurrentOrderActive) return;
        var draft = new SpecificationDraft(); Drafts.Add(draft); SelectedDraft = draft; RefreshProjects(draft.Id);
    }
    [RelayCommand] private void CancelDraft()
    {
        if (SelectedDraft is not { } draft || !CanUse) return;
        var saved = CurrentOrder?.Specifications.SingleOrDefault(s => s.Id == draft.Id);
        var index = Drafts.IndexOf(draft);
        if (saved is null) { Drafts.Remove(draft); SelectedDraft = Drafts.FirstOrDefault(); RefreshProjects(SelectedDraft?.Id); }
        else { Drafts[index] = CreateDraft(saved); SelectedDraft = Drafts[index]; }
        Error = null;
    }
    [RelayCommand] private async Task SaveHeaderAsync()
    {
        if (!CanUse || !IsCurrentOrderActive || CurrentOrder is not { IsDeleted: false } order) return;
        await RunAsync(async () =>
        {
            var managerName = ManagerName; var technologistId = Technologist?.Id ?? Guid.Empty; var actorId = ActorId(); var blcb = Blcb; var expected = CardVersion;
            _workspace = await _store.WriteOrderAsync(order.Id, expected, book => WorkflowChanges.UpdateOrderFromManagerName(book, order.Id, expected,
                managerName, technologistId, blcb, actorId, DateTimeOffset.UtcNow));
            RefreshEmployees(actorId); ManagerName = EmployeeName(CurrentOrder!.ManagerId);
            _loadedOrderVersion = CurrentOrder!.Version;
            RefreshOrders(); NotifyOrder();
        });
    }
    internal async Task SaveLegacyRateAsync()
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
        if (!CanUse || !IsCurrentOrderActive || CurrentOrder is not { IsDeleted: false } order || SelectedDraft is not { } draft || !draft.IsDirty) return;
        if (order.HasProjectRename) { Error = "Сначала завершите изменение параметров проекта."; return; }
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
            Drafts[index] = CreateDraft(specification); SelectedDraft = Drafts[index]; MarkResultStale(); RefreshProjects(draft.Id); RefreshOrders(); NotifyOrder();
        });
    }
    [RelayCommand] private void GenerateResult()
    {
        if (CurrentOrder is not { } order || !CanUse) return;
        if (HasCurrentCardEdits) { Error = "Сохраните изменения перед формированием результата."; return; }
        if (order.HasProjectRename) { Error = "Завершите изменение параметров проекта перед формированием результата."; return; }
        if (order.PendingProjectCount > 0 || HasUnselectedVariant)
        { Error = "Сохраните расчёты всех позиций и выберите варианты для итога."; return; }
        ResultText = WorkflowResultFormatter.Format(order.ActiveSpecifications.Select(s => s.Current), _workspace.Machines, ResultPricing);
        ResultIsStale = false;
    }

    internal async Task ImportLegacyFilesAsync(IReadOnlyList<string> paths, CancellationToken token)
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
                        throw new WorkflowValidationException("Этот отчёт относится к подготовленному проекту. Нажмите «Загрузить спецификации».");
                    var draft = FromReport(snapshot.Parsed, path, _workspace.Machines); draft.IncomingReport = snapshot;
                    ConfigurePriceSuggestions(draft); TryApplyCatalogRate(draft);
                    Drafts.Add(draft); SelectedDraft ??= draft;
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
                { failures.Add(Path.GetFileName(path) + ": " + (ex is DocumentRejectedException or WorkflowValidationException ? ex.Message : "не удалось прочитать файл")); }
            }
            if (failures.Count > 0) Error = string.Join("\n", failures.Take(4)) + (failures.Count > 4 ? $"\nЕщё файлов с ошибками: {failures.Count - 4}" : "");
            RefreshProjects(SelectedDraft?.Id);
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
        _loadedOrderVersion = order.Version; _loadedOrderSnapshot = order;
        _openOrderId = order.Id; Drafts.Clear();
        foreach (var specification in order.Specifications.Where(s => !order.Projects.Any(p => p.Id == s.Id && p.IsDeleted))) Drafts.Add(CreateDraft(specification));
        _refreshing = true;
        try { SelectedOrder = Orders.SingleOrDefault(row => row.Order.Id == order.Id); }
        finally { _refreshing = false; }
        SelectedDraft = Drafts.FirstOrDefault(); ManagerName = EmployeeName(order.ManagerId); Technologist = Employees.Single(e => e.Id == order.TechnologistId);
        Blcb = order.Blcb; Error = null; RefreshProjects(); NotifyOrder();
    }
    private void RefreshOrders(bool preserveRows = false)
    {
        var selected = _openOrderId ?? SelectedOrder?.Order.Id; _refreshing = true;
        try
        {
            var query = new WorkflowSearchQuery(Search); var index = OrderSearchIndex;
            var exactId = index.FindExactOrder(query); _isExactNumberSearch = exactId is not null;
            foreach (var id in _cards.Where(pair => !_workspace.Orders.Any(o => o.Id == pair.Key) &&
                !pair.Value.HasEdits && pair.Value.Result.Length == 0).Select(pair => pair.Key).ToArray()) _cards.Remove(id);
            var rows = new List<WorkflowOrderRow>();
            if (!preserveRows) Orders.Clear();
            foreach (var order in _workspace.Orders.Where(o => !o.IsDeleted && IsInPeriod(o)).OrderByDescending(o => o.UpdatedAt))
            {
                var detail = "";
                if (exactId is { } exact ? order.Id != exact : !index.Documents[order.Id].Matches(query, out detail)) continue;
                var row = new WorkflowOrderRow(order, CustomerName(order.CustomerId), EmployeeName(order.ManagerId), EmployeeName(order.TechnologistId))
                {
                    HasDraft = HasDraftForOrder(order.Id),
                    SearchDetail = detail,
                    Creator = _workspace.Employees.SingleOrDefault(e => e.Id == order.CreatedById)
                };
                if (preserveRows) rows.Add(row); else Orders.Add(row);
            }
            foreach (var state in _cards.Where(pair => !_workspace.Orders.Any(o => o.Id == pair.Key) &&
                (pair.Value.HasEdits || pair.Value.Result.Length > 0)).Select(pair => pair.Value))
            {
                if (state.SourceOrder is not { } source || !IsInPeriod(source)) continue;
                if (exactId is not null || !index.CreateDocument(source).Matches(query, out var detail)) continue;
                var row = new WorkflowOrderRow(source, CustomerName(source.CustomerId), EmployeeName(source.ManagerId), EmployeeName(source.TechnologistId))
                { HasDraft = true, IsMissing = true, SearchDetail = detail, Creator = _workspace.Employees.SingleOrDefault(e => e.Id == source.CreatedById) };
                if (preserveRows) rows.Add(row); else Orders.Add(row);
            }
            if (preserveRows) SynchronizeOrderRows(rows);
            SelectedOrder = Orders.FirstOrDefault(o => o.Order.Id == selected) ?? (_openOrderId is null ? Orders.FirstOrDefault() : null);
            if (!preserveRows) NotifyProfile();
            OnPropertyChanged(nameof(TrashCaption));
            NotifyOrderList();
        }
        finally { _refreshing = false; }
    }
    partial void OnSearchChanged(string value)
    {
        OnPropertyChanged(nameof(HasSearchText)); OnPropertyChanged(nameof(HasSearchQuery));
        if (_refreshing) return;
        if (FilterDate is not null) FilterDate = null;
        else if (_store is IWorkflowQueryStore) ScheduleDatabaseSearch();
        else RefreshOrders();
        if (!IsOrderOpen) SelectedOrder = null;
    }
    partial void OnManagerNameChanged(string value) => NotifyHeaderEdit();
    partial void OnTechnologistChanged(Employee? value) => NotifyHeaderEdit();
    partial void OnBlcbChanged(string? value) => NotifyHeaderEdit();
    partial void OnNewCustomerChanged(string value) => OnPropertyChanged(nameof(HasPendingEdits));
    partial void OnNewManagerChanged(string value) => OnPropertyChanged(nameof(HasPendingEdits));
    partial void OnIsCreatingChanged(bool value) { OnPropertyChanged(nameof(HasPendingEdits)); OnPropertyChanged(nameof(IsCardVisible)); OnPropertyChanged(nameof(IsNoCardSelected)); }
    partial void OnSelectedDraftChanged(SpecificationDraft? oldValue, SpecificationDraft? newValue)
    {
        if (oldValue is not null) oldValue.PropertyChanged -= DraftChanged;
        if (newValue is not null) { newValue.PropertyChanged += DraftChanged; ConfigurePriceSuggestions(newValue); }
        if (newValue is not null) Projects.SingleOrDefault(p => p.Project.Id == newValue.Id)?.ApplyLocalChanges(newValue.IsDirty);
        if (!_refreshingProjects && newValue is not null && Projects.SingleOrDefault(p => p.Project.Id == newValue.Id) is { } row &&
            !ReferenceEquals(SelectedProject, row))
        {
            _refreshingProjects = true;
            try { SelectedProject = row; } finally { _refreshingProjects = false; }
        }
        NotifyProjectCalculation();
        OnPropertyChanged(nameof(HasAcceptedReport)); OnPropertyChanged(nameof(HasPendingEdits)); OnPropertyChanged(nameof(HasSelectedDraft)); SaveSpecificationCommand.NotifyCanExecuteChanged();
    }
    private void DraftChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SpecificationDraft.IsDirty))
        {
            OnPropertyChanged(nameof(HasPendingEdits));
            if (sender is SpecificationDraft changed)
                Projects.SingleOrDefault(p => p.Project.Id == changed.Id)?.ApplyLocalChanges(changed.IsDirty);
        }
        if (e.PropertyName == nameof(SpecificationDraft.CanSave)) SaveSpecificationCommand.NotifyCanExecuteChanged();
        if (sender is SpecificationDraft draft && e.PropertyName is nameof(SpecificationDraft.Machine) or nameof(SpecificationDraft.MaterialGroup) or nameof(SpecificationDraft.Thickness))
        {
            if (_catalogUpdating) return;
            if (e.PropertyName == nameof(SpecificationDraft.Machine)) draft.UseMachine(_workspace.Machines.SingleOrDefault(m => m.Name == draft.Machine));
            ConfigurePriceSuggestions(draft);
            if (draft.Saved is null) TryApplyCatalogRate(draft);
        }
    }
    private Guid ActorId() => ActiveEmployee?.Id ?? throw new WorkflowValidationException("Выберите себя: Настройки → Общие → Технолог.");
    private string CustomerName(Guid id) => _workspace.Customers.Single(c => c.Id == id).Name;
    private string EmployeeName(Guid id) => _workspace.Employees.Single(e => e.Id == id).Name;
    private void NotifyOrder()
    {
        foreach (var name in new[] { nameof(IsJournal), nameof(IsOrderOpen), nameof(IsCardVisible), nameof(IsNoCardSelected), nameof(HeaderSummary),
            nameof(OrderTitle), nameof(TotalText), nameof(BendingText), nameof(ProjectsStatus), nameof(IsCurrentOrderActive), nameof(IsCurrentCardStale),
            nameof(HasCardReview), nameof(CardReviewText), nameof(TrashCaption), nameof(IsCurrentOrderMissing), nameof(HasPendingEdits) }) OnPropertyChanged(name);
        SaveSpecificationCommand.NotifyCanExecuteChanged();
    }
    partial void OnIsBusyChanged(bool value)
    { OnPropertyChanged(nameof(CanUse)); OnPropertyChanged(nameof(CanBrowseWorkspace)); SaveSpecificationCommand.NotifyCanExecuteChanged(); NotifyOrderList(); }
    private async Task RunAsync(Func<Task> action, string? fileError = null, bool projectInclusion = false)
    {
        if (IsBusy || _readingDatabase || _savingProjectInclusion && !projectInclusion) return; IsBusy = true; Error = null;
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Error = ex switch { WorkflowValidationException or WorkflowConflictException or WorkflowOutcomeUnknownException => ex.Message,
                IOException => fileError ?? (_store is WorkflowMdbStore ? "База данных недоступна или занята. Проверьте сеть и обновите журнал. Введённые параметры остались в форме." : "Локальные данные недоступны или заняты. Введённые параметры остались в форме."),
                UnauthorizedAccessException when fileError is not null => fileError,
                _ => "Не удалось выполнить действие. Введённые параметры остались в форме." };
            Jornal.Infrastructure.Diagnostics.AppDiagnostics.Capture(Jornal.Infrastructure.Diagnostics.DiagnosticOperation.Unexpected, ex);
        }
        finally { IsBusy = false; NotifyDatabase(); }
    }
    public void Detach() { CancelDatabaseSearch(); if (SelectedDraft is { } draft) draft.PropertyChanged -= DraftChanged; }
    public WorkflowRestartDraft CreateRestartDraft()
    {
        CaptureCard();
        return new(1, _openOrderId, ResultText)
        {
            DatabaseId = _store.DatabaseId,
            Results = _cards.Where(pair => pair.Value.Result.Length > 0)
                .Select(pair => new WorkflowRestartResult(pair.Key, pair.Value.Version, pair.Value.Result,
                    pair.Value.ResultExpanded, pair.Value.ResultStale)).ToArray()
        };
    }
    public void RestoreRestartDraft(WorkflowRestartDraft draft)
    {
        if (draft.DatabaseId != _store.DatabaseId) return;
        foreach (var result in draft.Results ?? [])
            if (_workspace.Orders.Any(o => o.Id == result.OrderId))
                _cards[result.OrderId] = new(result.Version, false, [], null, null, "", null, null,
                    result.Text, result.Expanded, result.Stale, new(false, null, "", null, null), LetterOnly: true);
        if (draft.OrderId is { } id && _workspace.Orders.SingleOrDefault(o => o.Id == id) is { } order)
        {
            LoadOrder(order);
            if (_cards.TryGetValue(id, out var state)) RestoreCard(order, state);
        }
        ResultText = draft.ResultText;
    }
}

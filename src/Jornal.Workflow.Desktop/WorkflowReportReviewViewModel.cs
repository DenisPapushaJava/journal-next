using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jornal.Workflow.Core;
using Jornal.Parsing.Specifications;
using Jornal.Workflow.Infrastructure;

namespace Jornal.Workflow.Desktop;

public sealed record WorkflowReportChoice(ReportFileSnapshot Snapshot)
{
    public string Name => Path.GetFileName(Snapshot.SourcePath);
}

public partial class WorkflowFieldConflict : ObservableObject
{
    private readonly Action<int> _choose;
    public string Field { get; }
    public string Manual { get; }
    public string Report { get; }
    public static string[] Options { get; } = ["Оставить ручное", "Взять из отчёта"];
    [ObservableProperty] private int _selection = -1;
    public WorkflowFieldConflict(string field, string manual, string report, Action<int> choose)
    { Field = field; Manual = manual; Report = report; _choose = choose; }
    partial void OnSelectionChanged(int value) { if (value >= 0) _choose(value); }
}

public partial class WorkflowReportCandidate : ObservableObject
{
    private WorkflowWorkspace _workspace;
    private readonly WorkflowSpecification? _existing;
    private Action _changed;
    private bool _pricing;
    private bool _freshRate;
    private readonly bool _latestReportWins;
    private string? _inputSourceHash;
    private bool _initializing = true, _hasManualEdits, _editedSinceCreation;
    internal bool HasUnpreservedEdits => _editedSinceCreation;
    private string? _sourceProblem;
    private IReadOnlyList<string> _materialSuggestions = [];
    internal void UseMaterialSuggestions(IReadOnlyList<string> values)
    { _materialSuggestions = values; Draft?.SetMaterialSuggestions(values); }
    private readonly string _emptyState;
    public CuttingProject Project { get; }
    public string Position { get; }
    public string PositionCaption => _workspace.Orders.SelectMany(o => o.ActiveProjects).Count(p => p.ItemId == Project.ItemId) > 1
        ? "Позиция " + Position.Replace(" · ", " · вариант ", StringComparison.Ordinal) : "Позиция " + Position.Split(" · ")[0];
    public string CardTitle
    {
        get
        {
            var machine = _workspace.Machines.Single(m => m.Id == Project.MachineId).Name;
            var material = Draft?.Material ?? _existing?.Current.Parameters.Material;
            return machine + " · " + SpecificationDraft.N(Project.ThicknessMm) + " мм" +
                (string.IsNullOrWhiteSpace(material) ? "" : " · " + material);
        }
    }
    public string ProjectName => Project.Name;
    public ObservableCollection<WorkflowFieldConflict> Conflicts { get; } = [];
    public IReadOnlyList<WorkflowReportChoice> Choices { get; }
    public ReportValues? Baseline { get; private set; }
    public ReportFileSnapshot? Snapshot => SelectedFile?.Snapshot;
    [ObservableProperty] private WorkflowReportChoice? _selectedFile;
    [ObservableProperty] private SpecificationDraft? _draft;
    [ObservableProperty] private bool _showReportFields;
    public string ReportSummary => Draft is not { } draft ? "" :
        $"{draft.Material} · {draft.Thickness} мм · {draft.BlankSize}\nРез: {draft.Length} м · пробивки: {draft.Pierces} · время: {draft.Duration}\nКоличество: {draft.Quantity} · вес: {draft.Weight} кг";
    public bool HasDraft => Draft is not null;
    internal bool WasAccepted { get; private set; }
    internal void MarkSourceChanged(string message) { _sourceProblem = message; WasAccepted = false; Changed(); }
    internal void MarkAccepted() { WasAccepted = true; IsSelected = false; Changed(); }
    private bool _guided;
    public bool ShowMaterialCheck => !_guided && Draft?.MaterialNeedsReview == true;
    internal void UseGuidance(bool value) { if (_guided == value) return; _guided = value; OnPropertyChanged(nameof(ShowMaterialCheck)); }
    public bool HasSourceChoice => Choices.Count > 1 || Snapshot is null && Choices.Count > 0;
    internal bool HasManualEdits => _hasManualEdits;
    internal bool CanAcceptAutomatically => !WasAccepted && (_latestReportWins || IsSelected) && Ready && (_latestReportWins || !_hasManualEdits) &&
        Snapshot?.Parsed.Issues.All(i => i.Severity != SpecificationIssueSeverity.Error) == true;
    internal bool NeedsDecision => !WasAccepted && Status != "Без изменений" && (Choices.Count > 0 || HasDraft);
    public bool NeedsAttention => !WasAccepted && (_sourceProblem is not null || Conflicts.Any(c => c.Selection < 0) ||
        (Snapshot is null ? Choices.Count > 0 || _existing is not null : Snapshot.Sha256 != _existing?.Current.Report?.Sha256 || Draft?.IsDirty == true));
    [ObservableProperty] private bool _isSelected;
    public string Status => _sourceProblem is not null ? "Нужна проверка" : Snapshot is null ? _emptyState :
        Snapshot.Sha256 == _existing?.Current.Report?.Sha256 ? Draft?.IsDirty == true ? "Параметры изменены" : "Без изменений" :
        Ready ? _existing is null ? "Новая · готово" : "Изменена · готово" : "Нужна проверка";
    public string Message => _sourceProblem ?? (Snapshot is null ? _emptyState :
        Conflicts.Any(c => c.Selection < 0) ? "Выберите значения спорных полей." :
        NeedsTariffCategory ? "В отчёте HansLaser нет известной категории тарифа. Выберите её ниже." :
        Draft?.MaterialNeedsReview == true && !Draft.MaterialConfirmed ? "Проверьте материал." :
        Draft is not null && !MatchesProject ?
            "Толщина или станок отличаются от проекта. Подготовьте другой вариант." :
        !_hasManualEdits && Snapshot?.Parsed.Issues.FirstOrDefault(i => i.Severity == SpecificationIssueSeverity.Error) is { } issue ? issue.Message :
        Draft is { } tariffDraft && string.IsNullOrWhiteSpace(tariffDraft.PricePerMeter) ?
            $"Не найден тариф: {tariffDraft.Machine}, {tariffDraft.Thickness} мм, {tariffDraft.MaterialGroup}. Нажмите «Настроить тариф»." :
        Draft?.CalculationError ?? (Ready ? "Тариф и ручной коэффициент сохранены; будет принята копия отчёта." : Status));
    public string Total => UnconfirmedMaterialQuote is { } preview ? SpecificationDraft.Money(preview.TotalCost) + " ₽" : Draft?.TotalText ?? (_existing is { } saved ? SpecificationDraft.Money(saved.Current.Calculation.TotalCost) + " ₽" : "—");
    private bool MatchesProject
    {
        get
        {
            try { return Draft is { } d && d.MachineId == Project.MachineId &&
                    SpecificationDraft.Decimal(d.Thickness, "толщину") == Project.ThicknessMm; }
            catch (WorkflowValidationException) { return false; }
        }
    }
    public bool Ready
    {
        get
        {
            if (_sourceProblem is not null || Snapshot is null || Draft is not { CanSave: true } d || Conflicts.Any(c => c.Selection < 0)) return false;
            try { return d.MachineId == Project.MachineId && d.Parameters().ThicknessMm == Project.ThicknessMm &&
                    !string.IsNullOrWhiteSpace(d.Number) && d.DocumentDate() != default; }
            catch (WorkflowValidationException) { return false; }
        }
    }

    public WorkflowReportCandidate(WorkflowWorkspace workspace, CuttingProject project, int position,
        IReadOnlyList<ReportFileSnapshot> files, string emptyState, Action changed, WorkflowReportCandidate? previous = null, bool latestReportWins = false, SpecificationDraft? localDraft = null)
    {
        if (latestReportWins && previous?._hasManualEdits != true) previous = null;
        _latestReportWins = latestReportWins;
        _workspace = workspace; Project = project; Position = $"{position} · {project.Variant}";
        _existing = workspace.Orders.SelectMany(o => o.Specifications).SingleOrDefault(s => s.Id == project.Id);
        _hasManualEdits = previous?._hasManualEdits == true || localDraft?.IsDirty == true;
        var uncertain = emptyState.StartsWith("Часть отчётов", StringComparison.Ordinal) ||
            emptyState.StartsWith("Подбор больше", StringComparison.Ordinal);
        _changed = changed; _emptyState = uncertain ? "Подбор неполный. " + emptyState + " Выберите доступный источник вручную." :
            files.Count > 1 ? "Несколько отчётов · выберите источник" : emptyState;
        if ((previous?.Draft ?? localDraft) is { } oldDraft)
        {
            _inputSourceHash = oldDraft.IncomingReport?.Sha256 ?? oldDraft.Saved?.Report?.Sha256;
            Draft = latestReportWins ? WorkflowCalculationSnapshots.MergeLocal(_existing, project.NativePath, oldDraft) : _existing is null ? new SpecificationDraft { Id = project.Id } : new(_existing);
            var values = Parts(Values(oldDraft));
            if (!latestReportWins)
            {
                for (var i = 0; i < values.Length; i++) Set(Draft, i, values[i]);
                Draft.Coefficient = oldDraft.Coefficient; Draft.Comment = oldDraft.Comment; Draft.HasBending = oldDraft.HasBending; Draft.HasChamfer = oldDraft.HasChamfer;
            }
            Draft.MaterialNeedsReview = oldDraft.MaterialNeedsReview; Draft.MaterialConfirmed = oldDraft.MaterialConfirmed;
            Draft.SourcePath = oldDraft.SourcePath; Draft.ImportProfile = oldDraft.ImportProfile; Draft.IncomingReport = oldDraft.IncomingReport;
            Draft.UseMachine(workspace.Machines.Single(m => m.Id == project.MachineId));
            Baseline = previous?.Baseline ?? oldDraft.Saved?.Report?.Parsed; _freshRate = previous?._freshRate == true ||
                oldDraft.Saved is { } rateBefore && (oldDraft.PricePerMeter != SpecificationDraft.N(rateBefore.Calculation.PricePerMeter) || oldDraft.PricePerPierce != SpecificationDraft.N(rateBefore.Calculation.PricePerPierce));
            _pendingFields = previous?.Conflicts.Where(c => c.Selection < 0).Select(c => c.Field).ToHashSet() ?? [];
        }
        Choices = files.Select(f => new WorkflowReportChoice(f)).ToArray();
        if (Choices.Count == 1 && !uncertain) SelectedFile = Choices[0];
        else if (Draft is not null) Draft.PropertyChanged += DraftChanged;
        if (previous?.Snapshot is not null) IsSelected = previous.IsSelected;
        ShowReportFields = previous?.ShowReportFields ?? NeedsReportFields;
        _initializing = false;
    }
    private HashSet<string> _pendingFields = [];
    internal void UseChangedCallback(Action changed) => _changed = changed;
    partial void OnSelectedFileChanged(WorkflowReportChoice? value)
    {
        if (!_initializing) { _hasManualEdits = true; _editedSinceCreation = true; }
        var previous = Draft; var previousBaseline = Baseline;
        var previousHash = previous?.IncomingReport?.Sha256;
        var previousConfirmation = previous?.MaterialConfirmed == true; var previousMaterial = previous?.Material;
        if (previous is not null) previous.PropertyChanged -= DraftChanged;
        Conflicts.Clear(); _sourceProblem = null; if (previous is null) _freshRate = false;
        if (value is null || previous is null && value.Snapshot.Sha256 == _existing?.Current.Report?.Sha256)
        { Draft = null; IsSelected = false; Changed(); return; }
        SpecificationDraft source;
        try { source = WorkflowViewModel.FromReport(value.Snapshot.Parsed, value.Snapshot.SourcePath, _workspace.Machines, Project.Id); }
        catch (WorkflowValidationException ex)
        {
            _sourceProblem = $"«{value.Name}»: {ex.Message} Исправьте отчёт в CAM и повторите подбор.";
            Draft = previous; IsSelected = false; Changed(); return;
        }
        var machine = _workspace.Machines.Single(m => m.Id == Project.MachineId); source.UseMachine(machine);
        var needsMaterialReview = source.MaterialNeedsReview;
        Baseline = Values(source);
        Draft = previous ?? (_existing is null ? source : new SpecificationDraft(_existing));
        Draft.IncomingReport = value.Snapshot; Draft.SourcePath = value.Snapshot.SourcePath; Draft.ImportProfile = value.Snapshot.Parsed.Profile;
        var sourceChanged = (previousHash ?? _inputSourceHash ?? _existing?.Current.Report?.Sha256) != value.Snapshot.Sha256;
        if (_latestReportWins && sourceChanged)
        {
            var incoming = Parts(Baseline); for (var i = 0; i < incoming.Length; i++) Set(Draft, i, incoming[i]);
        }
        else if (_existing is not null || previous is not null)
        {
            var current = Parts(Values(Draft)); var incoming = Parts(Baseline);
            var old = previousBaseline ?? _existing?.Current.Report?.Parsed;
            var before = old is null ? null : Parts(old);
            for (var i = 0; i < incoming.Length; i++)
            {
                var index = i; var manual = current[i]; var report = incoming[i];
                if (i == 3 && report.Length == 0) continue; // Missing Hans category needs explicit selection below.
                var pending = !_latestReportWins && _pendingFields.Contains(Labels[i]);
                if (manual == report || !pending && before is not null && report == before[i]) continue;
                if (!pending && before is not null && manual == before[i]) Set(Draft, i, report);
                else Conflicts.Add(new(Labels[i], manual, report, decision =>
                { _hasManualEdits = true; _editedSinceCreation = true; Set(Draft!, index, decision == 0 ? manual : report); Reprice(); Changed(); }));
            }
        }
        Draft.MaterialNeedsReview = needsMaterialReview && (sourceChanged || previous?.MaterialNeedsReview == true);
        Draft.MaterialConfirmed = previousHash == value.Snapshot.Sha256 && previousConfirmation && previousMaterial == Draft.Material;
        Draft.UsesMaterialCategories = source.UsesMaterialCategories;
        if (source.UsesMaterialCategories && Baseline.MaterialGroup.Length == 0 &&
            previousHash != value.Snapshot.Sha256) Draft.MaterialGroup = "";
        _pendingFields = [];
        Draft.PropertyChanged += DraftChanged;
        Reprice(); IsSelected = true; if (NeedsReportFields) ShowReportFields = true; Changed();
    }
    public bool ShowCategoryQuestion => Draft?.UsesMaterialCategories == true && string.IsNullOrWhiteSpace(Baseline?.MaterialGroup);
    public bool ShowPierceQuestion => Draft is not null && string.IsNullOrWhiteSpace(Baseline?.Pierces);
    public bool NeedsTariffCategory => Draft is { UsesMaterialCategories: true } d && string.IsNullOrWhiteSpace(d.MaterialGroup);
    public bool NeedsPierceCount => Draft is { } d && string.IsNullOrWhiteSpace(d.Pierces);
    public bool CanConfirmMaterial => UnconfirmedMaterialQuote is not null;
    private CuttingCalculation? UnconfirmedMaterialQuote
    {
        get
        {
            if (_sourceProblem is not null || Snapshot is null || Draft is not { MaterialNeedsReview: true, MaterialConfirmed: false } d || Conflicts.Any(c => c.Selection < 0) || !MatchesProject) return null;
            try
            {
                if (string.IsNullOrWhiteSpace(d.Number) || d.DocumentDate() == default) return null;
                return CuttingCalculation.Calculate(d.Parameters() with { MaterialNeedsReview = false }, d.Rate());
            }
            catch (Exception ex) when (ex is WorkflowValidationException or OverflowException) { return null; }
        }
    }
    private bool NeedsReportFields
    {
        get
        {
            if (Draft is not { } draft) return false;
            try
            {
                _ = draft.Parameters(); _ = draft.DocumentDate();
                return string.IsNullOrWhiteSpace(draft.Number) || !MatchesProject;
            }
            catch (WorkflowValidationException) { return true; }
        }
    }
    private void DraftChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SpecificationDraft.MaterialSuggestions)) return;
        if (_pricing) return;
        if (e.PropertyName is nameof(SpecificationDraft.Material) or nameof(SpecificationDraft.MaterialGroup) or
            nameof(SpecificationDraft.Thickness) or nameof(SpecificationDraft.BlankSize) or nameof(SpecificationDraft.Quantity) or
            nameof(SpecificationDraft.Length) or nameof(SpecificationDraft.Pierces) or nameof(SpecificationDraft.Weight) or
            nameof(SpecificationDraft.Duration) or nameof(SpecificationDraft.Number) or nameof(SpecificationDraft.Date) or
            nameof(SpecificationDraft.Coefficient) or nameof(SpecificationDraft.Comment) or nameof(SpecificationDraft.HasBending) or
            nameof(SpecificationDraft.HasChamfer) or nameof(SpecificationDraft.MaterialConfirmed) or
            nameof(SpecificationDraft.PricePerMeter) or nameof(SpecificationDraft.PricePerPierce)) { _hasManualEdits = true; _editedSinceCreation = true; }
        if (e.PropertyName is nameof(SpecificationDraft.MaterialGroup) or nameof(SpecificationDraft.Thickness)) Reprice();
        if (e.PropertyName is nameof(SpecificationDraft.CanSave) or nameof(SpecificationDraft.TotalText) or nameof(SpecificationDraft.MaterialConfirmed)) Changed();
    }
    private void Reprice()
    {
        if (_pricing || Draft is not { } draft) return;
        _pricing = true;
        try
        {
            var p = draft.Parameters();
            var prior = _existing?.Current;
            if (!_freshRate && prior is not null && p.MachineId == prior.Parameters.MachineId &&
                p.MaterialGroup == prior.Parameters.MaterialGroup && p.ThicknessMm == prior.Parameters.ThicknessMm)
                draft.ApplyRate(new(prior.Calculation.RateId, p.Machine, p.MaterialGroup, p.ThicknessMm,
                    prior.Calculation.PricePerMeter, prior.Calculation.PricePerPierce) { MachineId = p.MachineId });
            else draft.ApplyRate(_workspace.Rates.SingleOrDefault(r => r.MachineId == p.MachineId &&
                r.MaterialGroup == p.MaterialGroup && r.ThicknessMm == p.ThicknessMm));
        }
        catch (WorkflowValidationException) { /* Incomplete fields retain the existing validation message. */ }
        finally { _pricing = false; }
    }
    public void RefreshRate() { _editedSinceCreation = true; _hasManualEdits = true; _freshRate = true; Reprice(); Changed(); }
    internal bool TariffChanged(WorkflowWorkspace workspace)
    {
        CuttingRate? Rate(WorkflowWorkspace book) => book.Rates.SingleOrDefault(r => r.MachineId == Project.MachineId &&
            r.MaterialGroup == Draft?.MaterialGroup && r.ThicknessMm == Project.ThicknessMm);
        return Rate(workspace) != Rate(_workspace);
    }
    internal void UpdatePricing(WorkflowWorkspace workspace, bool refresh)
    {
        _workspace = workspace; if (refresh) { _freshRate = true; _hasManualEdits = true; _editedSinceCreation = true; }
        Draft?.UseMachine(workspace.Machines.Single(m => m.Id == Project.MachineId)); Reprice(); Changed();
    }
    partial void OnIsSelectedChanged(bool value) => _changed();
    partial void OnDraftChanged(SpecificationDraft? value)
    { value?.SetMaterialSuggestions(_materialSuggestions); OnPropertyChanged(nameof(HasDraft)); }
    private void Changed()
    {
        OnPropertyChanged(nameof(ShowMaterialCheck)); OnPropertyChanged(nameof(ShowCategoryQuestion)); OnPropertyChanged(nameof(ShowPierceQuestion)); OnPropertyChanged(nameof(NeedsTariffCategory)); OnPropertyChanged(nameof(NeedsPierceCount)); OnPropertyChanged(nameof(CanConfirmMaterial)); OnPropertyChanged(nameof(HasSourceChoice)); OnPropertyChanged(nameof(Status)); OnPropertyChanged(nameof(Message)); OnPropertyChanged(nameof(Total)); OnPropertyChanged(nameof(Ready)); OnPropertyChanged(nameof(CardTitle)); OnPropertyChanged(nameof(ReportSummary)); _changed();
    }
    internal static ReportValues Values(SpecificationDraft d) =>
        new(d.Number, d.Date, d.Material, d.MaterialGroup, d.Thickness, d.BlankSize, d.Quantity, d.Length, d.Pierces, d.Weight, d.Duration);
    internal static string[] Parts(ReportValues v) =>
        [v.DocumentNumber, v.DocumentDate, v.Material, v.MaterialGroup, v.Thickness, v.BlankSize, v.Quantity, v.Length, v.Pierces, v.Weight, v.Duration];
    private static readonly string[] Labels = ["Номер", "Дата", "Материал", "Категория", "Толщина", "Заготовка", "Количество", "Длина реза", "Пробивки", "Вес", "Время"];
    internal static void Set(SpecificationDraft d, int field, string value)
    {
        switch (field)
        {
            case 0: d.Number = value; break; case 1: d.Date = value; break; case 2: d.Material = value; break;
            case 3: d.MaterialGroup = value; break; case 4: d.Thickness = value; break; case 5: d.BlankSize = value; break;
            case 6: d.Quantity = value; break; case 7: d.Length = value; break; case 8: d.Pierces = value; break;
            case 9: d.Weight = value; break; case 10: d.Duration = value; break;
        }
    }
}

public partial class WorkflowReportReviewViewModel : ObservableObject
{
    private Func<IReadOnlyList<WorkflowReportCandidate>, CancellationToken, Task<string?>> _save;
    private readonly Func<WorkflowReportReviewViewModel, CancellationToken, Task<(WorkflowReportReviewViewModel? Review, string? Error)>>? _refresh;
    private readonly Func<WorkflowWorkspace>? _pricingSnapshot;
    public long PreviewOrderVersion { get; internal set; }
    internal Func<bool>? IsContextCurrent { get; init; }
    internal Func<long?>? CurrentOrderVersion { get; init; }
    private bool _isGuided;
    public bool IsGuided { get => _isGuided; internal set { if (_isGuided == value) return; _isGuided = value; Changed(); } }
    public IEnumerable<WorkflowReportCandidate> GuidedCandidates => Candidates.Where(c => c.NeedsDecision);
    public ObservableCollection<WorkflowReportCandidate> VisibleCandidates { get; } = [];
    public string Title => IsGuided ? "Уточнить расчёт" : "Подбор спецификаций";
    public string Footer => IsGuided ? "Готовые проекты рассчитаны автоматически. Уточните оставшиеся данные." : "Отмеченные готовые отчёты сохраняются вместе";
    public ObservableCollection<WorkflowReportCandidate> Candidates { get; } = [];
    [ObservableProperty] private WorkflowReportCandidate? _selected;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _wasSaved;
    [ObservableProperty] private IReadOnlyList<string> _materialSuggestions = [];
    partial void OnMaterialSuggestionsChanged(IReadOnlyList<string> value)
    { foreach (var candidate in Candidates) candidate.UseMaterialSuggestions(value); }
    private bool _orderChanged, _needsRefresh;
    public bool CanEdit => !IsBusy && !WasSaved;
    private bool CanSave => CanEdit && !_orderChanged && (IsGuided && (_needsRefresh || Selected?.CanConfirmMaterial == true) || Candidates.Any(c => (IsGuided || c.IsSelected) && !c.WasAccepted && c.Ready));
    private bool CanRefresh => CanEdit && !_orderChanged && _refresh is not null;
    public string SaveCaption => IsGuided ? _needsRefresh ? "Продолжить" : Selected?.CanConfirmMaterial == true ? "Подтвердить и продолжить" : "Применить и продолжить" : $"Принять готовые ({Candidates.Count(c => c.IsSelected && c.Ready)})";
    public string Summary => IsGuided && _needsRefresh ? "Данные заявки обновились. Продолжение учтёт актуальные значения и сохранит ваши правки." : IsGuided ? $"Нужно уточнить: {GuidedCandidates.Count()}. Выберите проект слева и выполните действие справа." : WasSaved ? "Готовые отчёты сохранены. Остальные проекты можно обновить позже." :
        $"{Candidates.Count(c => c.Ready)} готовы · {Candidates.Count(c => c.Status == "Без изменений")} без изменений · " +
        $"{Candidates.Count(c => !c.Ready && c.Status != "Без изменений")} ожидают или требуют проверки";
    public WorkflowReportReviewViewModel(Func<IReadOnlyList<WorkflowReportCandidate>, CancellationToken, Task<string?>> save,
        Func<WorkflowReportReviewViewModel, CancellationToken, Task<(WorkflowReportReviewViewModel? Review, string? Error)>>? refresh = null,
        Func<WorkflowWorkspace>? pricingSnapshot = null)
    { _save = save; _refresh = refresh; _pricingSnapshot = pricingSnapshot; }
    public void Changed()
    {
        foreach (var candidate in Candidates) { candidate.UseGuidance(IsGuided); candidate.UseMaterialSuggestions(MaterialSuggestions); }
        var selected = Selected;
        var visible = (IsGuided ? GuidedCandidates : Candidates).ToArray();
        foreach (var removed in VisibleCandidates.Where(c => !visible.Contains(c)).ToArray()) VisibleCandidates.Remove(removed);
        for (var index = 0; index < visible.Length; index++)
        {
            var current = VisibleCandidates.IndexOf(visible[index]);
            if (current < 0) VisibleCandidates.Insert(index, visible[index]);
            else if (current != index) VisibleCandidates.Move(current, index);
        }
        if (selected is not null && VisibleCandidates.Contains(selected)) Selected = selected;
        else if (Selected is null || !VisibleCandidates.Contains(Selected)) Selected = VisibleCandidates.FirstOrDefault();
        OnPropertyChanged(nameof(SaveCaption)); OnPropertyChanged(nameof(Summary)); SaveCommand.NotifyCanExecuteChanged();
    }
    internal void MarkOrderChanged()
    {
        if (IsGuided) { _needsRefresh = true; Changed(); return; }
        _orderChanged = true;
        Error = "Заявка изменена в другом окне. Ваши правки остались в этом подборе; принять его нельзя. Чтобы начать заново, закройте окно и нажмите F5: несохранённые правки подбора будут сброшены.";
        Changed(); RefreshCommand.NotifyCanExecuteChanged();
    }
    partial void OnSelectedChanged(WorkflowReportCandidate? value) { OnPropertyChanged(nameof(SaveCaption)); SaveCommand.NotifyCanExecuteChanged(); }
    partial void OnIsBusyChanged(bool value) { OnPropertyChanged(nameof(CanEdit)); Changed(); RefreshCommand.NotifyCanExecuteChanged(); }
    partial void OnWasSavedChanged(bool value) { OnPropertyChanged(nameof(CanEdit)); Changed(); RefreshCommand.NotifyCanExecuteChanged(); }
    [RelayCommand(CanExecute = nameof(CanRefresh))] private async Task RefreshAsync(CancellationToken token)
    {
        if (!CanRefresh || _refresh is null) return;
        IsBusy = true; Error = null;
        try
        {
            await RefreshCoreAsync(token);
        }
        catch (OperationCanceledException) { Error = "Подбор отменён; введённые параметры остались в окне."; }
        finally { IsBusy = false; }
    }
    private async Task<bool> RefreshCoreAsync(CancellationToken token)
    {
        if (_refresh is null) return false;
        var (next, error) = await _refresh(this, token);
        if (ReferenceEquals(next, this)) return false;
        Error = error;
        if (next is null) return false;
        var selected = Selected?.Project.Id;
        _save = async (candidates, cancellation) =>
        {
            var failure = await next._save(candidates, cancellation);
            if (next._needsRefresh) _needsRefresh = true;
            return failure;
        };
        PreviewOrderVersion = next.PreviewOrderVersion; _needsRefresh = false;
        MaterialSuggestions = next.MaterialSuggestions;
        Candidates.Clear();
        foreach (var candidate in next.Candidates) { candidate.UseChangedCallback(Changed); Candidates.Add(candidate); }
        Selected = Candidates.SingleOrDefault(c => c.Project.Id == selected) ?? Candidates.FirstOrDefault(); Changed();
        return true;
    }
    public void RefreshPricing(bool refreshSelected = true)
    {
        if (!CanEdit) return;
        if (_pricingSnapshot is not { } snapshot) { Selected?.RefreshRate(); return; }
        var workspace = snapshot();
        foreach (var candidate in Candidates) candidate.UpdatePricing(workspace,
            ReferenceEquals(candidate, Selected) && (refreshSelected || candidate.TariffChanged(workspace)));
    }
    [RelayCommand] private void RefreshRate() => RefreshPricing();
    internal async Task AcceptAutomaticAsync(CancellationToken token)
    {
        if (!CanEdit || _orderChanged) return;
        var ready = Candidates.Where(c => c.CanAcceptAutomatically).ToArray();
        if (ready.Length == 0) return;
        IsBusy = true; Error = null;
        try { Error = await _save(ready, token); if (Error is null && CurrentOrderVersion?.Invoke() is { } version) PreviewOrderVersion = version; Changed(); }
        finally { IsBusy = false; }
    }
    [RelayCommand(CanExecute = nameof(CanSave))] private async Task SaveAsync(CancellationToken token)
    {
        if (!CanSave) return;
        IsBusy = true; Error = null;
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                if (IsGuided && _needsRefresh)
                {
                    var confirmedSource = Selected?.Snapshot?.Sha256;
                    if (!await RefreshCoreAsync(token)) return;
                    // A different specification needs its own material confirmation.
                    if (Selected is { CanConfirmMaterial: true } next && next.Snapshot?.Sha256 != confirmedSource) return;
                }
                if (IsGuided && Selected is { CanConfirmMaterial: true, Draft: { } materialDraft }) materialDraft.MaterialConfirmed = true;
                var ready = Candidates.Where(c => (IsGuided || c.IsSelected) && !c.WasAccepted && c.Ready).ToArray();
                if (ready.Length == 0) { WasSaved = IsGuided && !GuidedCandidates.Any(); return; }
                Error = await _save(ready, token);
                if (Error is not null)
                {
                    if (IsGuided && _needsRefresh && attempt == 0) continue;
                    return;
                }
                if (CurrentOrderVersion?.Invoke() is { } version) PreviewOrderVersion = version;
                WasSaved = !IsGuided || !GuidedCandidates.Any();
                if (IsGuided && !WasSaved) Selected = GuidedCandidates.FirstOrDefault();
                Changed(); return;
            }
        }
        catch (OperationCanceledException) { Error = "Сохранение отменено; подбор остался в окне."; }
        finally { IsBusy = false; }
    }
}

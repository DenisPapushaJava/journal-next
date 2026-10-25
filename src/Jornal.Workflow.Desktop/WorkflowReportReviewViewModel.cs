using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jornal.Workflow.Core;
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
    private readonly WorkflowWorkspace _workspace;
    private readonly WorkflowSpecification? _existing;
    private Action _changed;
    private bool _pricing;
    private bool _freshRate;
    private readonly string _emptyState;
    public CuttingProject Project { get; }
    public string Position { get; }
    public string PositionCaption => "Позиция " + Position.Replace(" · ", " · вариант ", StringComparison.Ordinal);
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
    public bool HasDraft => Draft is not null;
    [ObservableProperty] private bool _isSelected;
    public string Status => Snapshot is null ? _emptyState :
        Snapshot.Sha256 == _existing?.Current.Report?.Sha256 ? Draft?.IsDirty == true ? "Параметры изменены" : "Без изменений" :
        Ready ? _existing is null ? "Новая · готово" : "Изменена · готово" : "Нужна проверка";
    public string Message => Snapshot is null ? _emptyState :
        Conflicts.Any(c => c.Selection < 0) ? "Выберите значения спорных полей." :
        Draft?.MaterialNeedsReview == true && !Draft.MaterialConfirmed ? "Проверьте материал." :
        Draft is not null && !MatchesProject ?
            "Толщина или станок отличаются от проекта. Подготовьте другой вариант." :
        Draft?.CalculationError ?? (Ready ? "Тариф и ручной коэффициент сохранены; будет принята копия отчёта." : Status);
    public string Total => Draft?.TotalText ?? (_existing is { } saved ? SpecificationDraft.Money(saved.Current.Calculation.TotalCost) + " ₽" : "—");
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
            if (Snapshot is null || Draft is not { CanSave: true } d || Conflicts.Any(c => c.Selection < 0)) return false;
            try { return d.MachineId == Project.MachineId && d.Parameters().ThicknessMm == Project.ThicknessMm &&
                    !string.IsNullOrWhiteSpace(d.Number) && d.DocumentDate() != default; }
            catch (WorkflowValidationException) { return false; }
        }
    }

    public WorkflowReportCandidate(WorkflowWorkspace workspace, CuttingProject project, int position,
        IReadOnlyList<ReportFileSnapshot> files, string emptyState, Action changed, WorkflowReportCandidate? previous = null)
    {
        _workspace = workspace; Project = project; Position = $"{position} · {project.Variant}";
        _existing = workspace.Orders.SelectMany(o => o.Specifications).SingleOrDefault(s => s.Id == project.Id);
        var uncertain = emptyState.StartsWith("Часть отчётов", StringComparison.Ordinal) ||
            emptyState.StartsWith("Подбор больше", StringComparison.Ordinal);
        _changed = changed; _emptyState = uncertain ? "Подбор неполный · выберите источник вручную" :
            files.Count > 1 ? "Несколько отчётов · выберите источник" : emptyState;
        if (previous?.Draft is { } oldDraft)
        {
            Draft = _existing is null ? new SpecificationDraft { Id = project.Id } : new(_existing);
            var values = Parts(Values(oldDraft)); for (var i = 0; i < values.Length; i++) Set(Draft, i, values[i]);
            Draft.Coefficient = oldDraft.Coefficient; Draft.Comment = oldDraft.Comment; Draft.HasBending = oldDraft.HasBending; Draft.HasChamfer = oldDraft.HasChamfer;
            Draft.MaterialNeedsReview = oldDraft.MaterialNeedsReview; Draft.MaterialConfirmed = oldDraft.MaterialConfirmed;
            Draft.SourcePath = oldDraft.SourcePath; Draft.ImportProfile = oldDraft.ImportProfile; Draft.IncomingReport = oldDraft.IncomingReport;
            Draft.UseMachine(workspace.Machines.Single(m => m.Id == project.MachineId));
            Baseline = previous.Baseline; _freshRate = previous._freshRate;
            _pendingFields = previous.Conflicts.Where(c => c.Selection < 0).Select(c => c.Field).ToHashSet();
        }
        Choices = files.Select(f => new WorkflowReportChoice(f)).ToArray();
        if (Choices.Count == 1 && !uncertain) SelectedFile = Choices[0];
        else if (Draft is not null) Draft.PropertyChanged += DraftChanged;
        if (previous?.Snapshot is not null) IsSelected = previous.IsSelected;
    }
    private HashSet<string> _pendingFields = [];
    internal void UseChangedCallback(Action changed) => _changed = changed;
    partial void OnSelectedFileChanged(WorkflowReportChoice? value)
    {
        var previous = Draft; var previousBaseline = Baseline;
        var previousHash = previous?.IncomingReport?.Sha256;
        var previousConfirmation = previous?.MaterialConfirmed == true; var previousMaterial = previous?.Material;
        if (previous is not null) previous.PropertyChanged -= DraftChanged;
        Conflicts.Clear(); if (previous is null) _freshRate = false;
        if (value is null || previous is null && value.Snapshot.Sha256 == _existing?.Current.Report?.Sha256)
        { Draft = null; IsSelected = false; Changed(); return; }
        var source = WorkflowViewModel.FromReport(value.Snapshot.Parsed, value.Snapshot.SourcePath, _workspace.Machines, Project.Id);
        var machine = _workspace.Machines.Single(m => m.Id == Project.MachineId); source.UseMachine(machine);
        var needsMaterialReview = source.MaterialNeedsReview;
        Baseline = Values(source);
        Draft = previous ?? (_existing is null ? source : new SpecificationDraft(_existing));
        Draft.IncomingReport = value.Snapshot; Draft.SourcePath = value.Snapshot.SourcePath; Draft.ImportProfile = value.Snapshot.Parsed.Profile;
        if (_existing is not null || previous is not null)
        {
            var current = Parts(Values(Draft)); var incoming = Parts(Baseline);
            var old = previousBaseline ?? _existing?.Current.Report?.Parsed;
            var before = old is null ? null : Parts(old);
            for (var i = 0; i < incoming.Length; i++)
            {
                var index = i; var manual = current[i]; var report = incoming[i];
                if (i == 3 && report.Length == 0) continue; // Missing Hans category needs explicit selection below.
                var pending = _pendingFields.Contains(Labels[i]);
                if (manual == report || !pending && before is not null && report == before[i]) continue;
                if (!pending && before is not null && manual == before[i]) Set(Draft, i, report);
                else Conflicts.Add(new(Labels[i], manual, report, decision =>
                { Set(Draft!, index, decision == 0 ? manual : report); Reprice(); Changed(); }));
            }
        }
        Draft.MaterialNeedsReview = needsMaterialReview;
        Draft.MaterialConfirmed = previousHash == value.Snapshot.Sha256 && previousConfirmation && previousMaterial == Draft.Material;
        Draft.UsesMaterialCategories = source.UsesMaterialCategories;
        if (source.UsesMaterialCategories && Baseline.MaterialGroup.Length == 0 &&
            previousHash != value.Snapshot.Sha256) Draft.MaterialGroup = "";
        _pendingFields = [];
        Draft.PropertyChanged += DraftChanged;
        Reprice(); IsSelected = true; Changed();
    }
    private void DraftChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_pricing) return;
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
    public void RefreshRate() { _freshRate = true; Reprice(); Changed(); }
    partial void OnIsSelectedChanged(bool value) => _changed();
    partial void OnDraftChanged(SpecificationDraft? value) => OnPropertyChanged(nameof(HasDraft));
    private void Changed()
    {
        OnPropertyChanged(nameof(Status)); OnPropertyChanged(nameof(Message)); OnPropertyChanged(nameof(Total)); OnPropertyChanged(nameof(Ready)); OnPropertyChanged(nameof(CardTitle)); _changed();
    }
    internal static ReportValues Values(SpecificationDraft d) =>
        new(d.Number, d.Date, d.Material, d.MaterialGroup, d.Thickness, d.BlankSize, d.Quantity, d.Length, d.Pierces, d.Weight, d.Duration);
    private static string[] Parts(ReportValues v) =>
        [v.DocumentNumber, v.DocumentDate, v.Material, v.MaterialGroup, v.Thickness, v.BlankSize, v.Quantity, v.Length, v.Pierces, v.Weight, v.Duration];
    private static readonly string[] Labels = ["Номер", "Дата", "Материал", "Категория", "Толщина", "Заготовка", "Количество", "Длина реза", "Пробивки", "Вес", "Время"];
    private static void Set(SpecificationDraft d, int field, string value)
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
    public long PreviewOrderVersion { get; init; }
    public ObservableCollection<WorkflowReportCandidate> Candidates { get; } = [];
    [ObservableProperty] private WorkflowReportCandidate? _selected;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _wasSaved;
    public bool CanEdit => !IsBusy && !WasSaved;
    private bool CanSave => CanEdit && Candidates.Any(c => c.IsSelected && c.Ready);
    private bool CanRefresh => CanEdit && _refresh is not null;
    public string SaveCaption => $"Сохранить готовые ({Candidates.Count(c => c.IsSelected && c.Ready)})";
    public string Summary => WasSaved ? "Готовые отчёты сохранены. Остальные проекты можно обновить позже." :
        $"{Candidates.Count(c => c.Ready)} готовы · {Candidates.Count(c => c.Status == "Без изменений")} без изменений · " +
        $"{Candidates.Count(c => !c.Ready && c.Status != "Без изменений")} ожидают или требуют проверки";
    public WorkflowReportReviewViewModel(Func<IReadOnlyList<WorkflowReportCandidate>, CancellationToken, Task<string?>> save,
        Func<WorkflowReportReviewViewModel, CancellationToken, Task<(WorkflowReportReviewViewModel? Review, string? Error)>>? refresh = null)
    { _save = save; _refresh = refresh; }
    public void Changed()
    { OnPropertyChanged(nameof(SaveCaption)); OnPropertyChanged(nameof(Summary)); SaveCommand.NotifyCanExecuteChanged(); }
    partial void OnIsBusyChanged(bool value) { OnPropertyChanged(nameof(CanEdit)); Changed(); RefreshCommand.NotifyCanExecuteChanged(); }
    partial void OnWasSavedChanged(bool value) { OnPropertyChanged(nameof(CanEdit)); Changed(); RefreshCommand.NotifyCanExecuteChanged(); }
    [RelayCommand(CanExecute = nameof(CanRefresh))] private async Task RefreshAsync(CancellationToken token)
    {
        if (_refresh is null) return;
        IsBusy = true; Error = null;
        try
        {
            var (next, error) = await _refresh(this, token); Error = error;
            if (next is null) return;
            var selected = Selected?.Project.Id; _save = next._save;
            Candidates.Clear();
            foreach (var candidate in next.Candidates) { candidate.UseChangedCallback(Changed); Candidates.Add(candidate); }
            Selected = Candidates.SingleOrDefault(c => c.Project.Id == selected) ?? Candidates.FirstOrDefault(); Changed();
        }
        catch (OperationCanceledException) { Error = "Подбор отменён; введённые параметры остались в окне."; }
        finally { IsBusy = false; }
    }
    [RelayCommand] private void RefreshRate() => Selected?.RefreshRate();
    [RelayCommand(CanExecute = nameof(CanSave))] private async Task SaveAsync(CancellationToken token)
    {
        IsBusy = true; Error = null;
        try
        {
            Error = await _save(Candidates.Where(c => c.IsSelected && c.Ready).ToArray(), token);
            if (Error is null) WasSaved = true;
        }
        catch (OperationCanceledException) { Error = "Сохранение отменено; подбор остался в окне."; }
        finally { IsBusy = false; }
    }
}

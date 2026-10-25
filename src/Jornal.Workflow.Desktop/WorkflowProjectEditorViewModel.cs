using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jornal.Workflow.Core;
using Jornal.Workflow.Infrastructure;

namespace Jornal.Workflow.Desktop;

public partial class WorkflowProjectEditorViewModel : ObservableObject
{
    private IWorkflowWorkspaceStore _store;
    private readonly Action<WorkflowWorkspace> _snapshot;
    private readonly Guid _orderId;
    private readonly Guid _actor;
    private long _version;
    private CuttingProject _project;
    public IReadOnlyList<WorkflowMachine> Machines { get; }
    [ObservableProperty] private WorkflowMachine? _machine;
    [ObservableProperty] private string _thickness = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _error;
    public bool WasSaved { get; private set; }
    public ProjectFileFailure? FileFailure { get; private set; }
    public bool CanEdit => !IsBusy && _project.Rename is null;
    public bool CanSave => !IsBusy && Machine is not null &&
        (_project.Rename is not null || Machine.Id != _project.MachineId ||
            !decimal.TryParse(Thickness.Replace(',', '.'), System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out var thickness) || thickness != _project.ThicknessMm);
    public string CurrentName => _project.Name;
    public string PreviewName
    {
        get
        {
            if (_project.Rename is { } pending) return pending.Name;
            try { return Machine is null ? "Выберите станок" : WorkflowProjectNames.Renamed(_project, Machine, SpecificationDraft.Decimal(Thickness, "толщину")); }
            catch (WorkflowValidationException) { return "Укажите положительную толщину"; }
        }
    }
    public string PreviewPath
    {
        get
        {
            try { return _project.Rename?.NativePath ?? (Machine is null ? "" : WorkflowProjects.ProposeRename(_project, Machine,
                SpecificationDraft.Decimal(Thickness, "толщину"), _actor, DateTimeOffset.UtcNow).NativePath); }
            catch (WorkflowValidationException) { return ""; }
        }
    }
    public string Notice => _project.Rename is null
        ? "Изменяются название проекта, папка и пустой файл заготовки. Дата-время и позиция сохраняются."
        : "Переименование не завершено. Завершите его с указанными параметрами; программа проверит фактическое расположение файлов.";
    public IReadOnlyList<string> ThicknessSuggestions { get; private set; }
    private readonly CuttingRate[] _rates;

    public WorkflowProjectEditorViewModel(IWorkflowWorkspaceStore store, WorkflowWorkspace book, Guid orderId,
        Guid projectId, Guid actor, Action<WorkflowWorkspace> snapshot)
    {
        _store = store; _snapshot = snapshot; _orderId = orderId; _actor = actor;
        var order = book.Orders.Single(o => o.Id == orderId); _version = order.Version;
        _project = order.Projects.Single(p => p.Id == projectId); Machines = book.Machines; _rates = book.Rates;
        _machine = Machines.Single(m => m.Id == (_project.Rename?.MachineId ?? _project.MachineId));
        _thickness = SpecificationDraft.N(_project.Rename?.ThicknessMm ?? _project.ThicknessMm);
        ThicknessSuggestions = Suggestions();
    }
    partial void OnMachineChanged(WorkflowMachine? value)
    { ThicknessSuggestions = Suggestions(); OnPropertyChanged(nameof(ThicknessSuggestions)); Changed(); }
    partial void OnThicknessChanged(string value) => Changed();
    partial void OnIsBusyChanged(bool value) => Changed();
    private IReadOnlyList<string> Suggestions() => _rates.Where(r => r.MachineId == Machine?.Id)
        .Select(r => r.ThicknessMm).Distinct().Order().Select(SpecificationDraft.N).ToArray();
    private void Changed()
    {
        foreach (var name in new[] { nameof(PreviewName), nameof(PreviewPath), nameof(CanEdit), nameof(CanSave), nameof(Notice) }) OnPropertyChanged(name);
        SaveCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (!CanSave) return;
        IsBusy = true; Error = null; FileFailure = null;
        try
        {
            var thickness = SpecificationDraft.Decimal(Thickness, "толщину");
            var book = await new WorkflowProjectMaintenance(_store).RenameAsync(_orderId, _version, _project.Id,
                Machine!.Id, thickness, _actor);
            _snapshot(book); WasSaved = true;
        }
        catch (ProjectFileFailure ex)
        {
            FileFailure = ex; Error = ex.Message;
            try
            {
                var book = await _store.ReadOrderAsync(_orderId); var order = book.Orders.Single(o => o.Id == _orderId);
                _project = order.Projects.Single(p => p.Id == _project.Id); _version = order.Version;
                _snapshot(book);
            }
            catch (Exception readError) when (readError is IOException or UnauthorizedAccessException or WorkflowValidationException)
            { Error += " Не удалось проверить сохранённое состояние. Закройте редактор и обновите заявку через F5."; }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { Error = ex is WorkflowConflictException or WorkflowValidationException ? ex.Message : "Не удалось сохранить параметры. Введённые значения остались в форме."; }
        finally { IsBusy = false; Changed(); }
    }
}

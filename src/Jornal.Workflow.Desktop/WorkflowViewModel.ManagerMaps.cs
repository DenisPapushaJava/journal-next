using Avalonia.Threading;
using Jornal.Infrastructure.Diagnostics;
using Jornal.Workflow.Core;
using Jornal.Workflow.Infrastructure;

namespace Jornal.Workflow.Desktop;

public partial class WorkflowViewModel
{
    private readonly Dictionary<(IWorkflowWorkspaceStore Source, Guid OrderId), CancellationTokenSource> _managerMapJobs = [];
    private (IWorkflowWorkspaceStore Source, Guid OrderId, string Text)? _managerMapsNotice;
    public bool IsPreparingManagerMaps => CurrentOrder is { } order && _managerMapJobs.ContainsKey((_store, order.Id));
    public string ManagerMapsNotice => IsPreparingManagerMaps ? "Собираем карты для менеджера…" : _managerMapsNotice is { } notice &&
        ReferenceEquals(notice.Source, _store) && notice.OrderId == CurrentOrder?.Id ? notice.Text : "";
    public bool HasManagerMapsNotice => ManagerMapsNotice.Length > 0;
    private void NotifyManagerMapsNotice()
    {
        OnPropertyChanged(nameof(IsPreparingManagerMaps));
        OnPropertyChanged(nameof(ManagerMapsNotice));
        OnPropertyChanged(nameof(HasManagerMapsNotice));
    }

    internal void CancelCurrentManagerMaps()
    {
        if (CurrentOrder is { } order && _managerMapJobs.TryGetValue((_store, order.Id), out var job))
            job.Cancel();
    }
    private void CancelManagerMapJobs()
    {
        foreach (var job in _managerMapJobs.Values.ToArray()) job.Cancel();
    }

    internal bool ManagerMapsAsPdf => _localPreferences.ManagerMapsAsPdf;
    internal async Task<string?> SaveManagerMapFormatAsync(bool pdf, CancellationToken token)
    {
        if (Updates.IsApplying) return "Дождитесь завершения обновления.";
        try
        {
            var saved = await Task.Run(() => _preferences.Update(current =>
                current.ManagerMapsAsPdf == pdf ? current : current with { ManagerMapsAsPdf = pdf }), token);
            var changed = ManagerMapsAsPdf != saved.ManagerMapsAsPdf;
            _localPreferences = _localPreferences with { ManagerMapsAsPdf = saved.ManagerMapsAsPdf };
            if (changed) CancelManagerMapJobs();
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
        { return "Не удалось сохранить формат карт для менеджера на этом компьютере."; }
    }

    internal Task MergeManagerMapsAsync(WorkflowProjectRow row, CancellationToken token) =>
        CanUse && !IsPreparingManagerMaps && IsCurrentOrderActive && CurrentOrder is { } order && Projects.Contains(row) && row.HasManagerMapAction
            ? MergeManagerMapsAsync(order, [row.Project], automatic: false, token)
            : Task.CompletedTask;

    internal Task MergeManagerMapsAfterCalculationAsync(WorkflowReportReviewViewModel review, CancellationToken token)
    {
        if (!CanUse || CurrentOrder is not { } order || review.IsContextCurrent?.Invoke() != true ||
            review.PreviewOrderVersion != order.Version) return Task.CompletedTask;
        var included = CalculationProjectIds(order);
        var projects = order.ActiveProjects.Where(p => included.Contains(p.Id) && order.Specifications.Any(s => s.Id == p.Id)).ToArray();
        return MergeManagerMapsAsync(order, projects, automatic: true, token);
    }

    private async Task MergeManagerMapsAsync(WorkflowOrder order, CuttingProject[] projects, bool automatic, CancellationToken token)
    {
        var snapshot = _workspace;
        var jobs = (from project in projects
                    join machine in snapshot.Machines on project.MachineId equals machine.Id
                    where WorkflowManagerMapBundle.Supports(project, machine)
                    select (project, machine)).ToArray();
        if (jobs.Length == 0 || token.IsCancellationRequested || _cuttingTaskDetached) return;
        var source = _store;
        var key = (source, order.Id);
        var format = ManagerMapsAsPdf ? ShopMapBundle.Pdf : ShopMapBundle.Doc;
        using var job = CancellationTokenSource.CreateLinkedTokenSource(token);
        var previous = _managerMapJobs.GetValueOrDefault(key);
        _managerMapJobs[key] = job;
        previous?.Cancel();
        NotifyManagerMapsNotice();
        void Notice(string text)
        {
            if (_cuttingTaskDetached || !ReferenceEquals(source, _store)) return;
            _managerMapsNotice = (source, order.Id, text);
        }
        try
        {
            var completed = 0;
            string? lastFile = null;
            string? problem = null;
            foreach (var (project, machine) in jobs)
            {
                job.Token.ThrowIfCancellationRequested();
                async Task Validate(CancellationToken cancellation)
                {
                    var fresh = await source.ReadOrderAsync(order.Id, cancellation);
                    var current = fresh.Orders.SingleOrDefault(o => o.Id == order.Id && !o.IsDeleted);
                    if (current?.ActiveProjects.SingleOrDefault(p => p.Id == project.Id) is not { } latest ||
                        latest.NativePath != project.NativePath || latest.Name != project.Name || latest.Rename is not null ||
                        latest.MachineId != machine.Id || latest.ImportProfile != machine.ImportProfile ||
                        (automatic && (!current.Items.Any(i => i.IsIncluded && i.CurrentProjectId == project.Id) ||
                            !current.Specifications.Any(s => s.Id == project.Id))))
                        throw new WorkflowValidationException("Проект изменился. Обновите заявку и объедините карты повторно.");
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        cancellation.ThrowIfCancellationRequested();
                        if (_cuttingTaskDetached || !ReferenceEquals(source, _store) ||
                            ManagerMapsAsPdf != (format == ShopMapBundle.Pdf))
                            throw new OperationCanceledException();
                    });
                }
                foreach (var gnf in new[] { false, true })
                {
                    try
                    {
                        var file = await Task.Run(() => gnf
                            ? WorkflowManagerMapBundle.CreateGnfAsync(project, machine, _wordMapEditor, Validate, job.Token)
                            : WorkflowManagerMapBundle.CreateAsync(project, machine, format, _wordMapEditor, Validate, job.Token), job.Token);
                        if (file is null) continue;
                        completed++;
                        lastFile = Path.GetFileName(file);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
                    {
                        var variant = gnf ? " · Gnf" : "";
                        problem ??= $"«{project.Name}»{variant}: " + (ex is WorkflowValidationException ? ex.Message : "Проверьте доступ к папке проекта и закройте занятые файлы.");
                        AppDiagnostics.Capture(DiagnosticOperation.Unexpected, ex);
                    }
                }
            }
            job.Token.ThrowIfCancellationRequested();
            if (_managerMapJobs.GetValueOrDefault(key) != job) return;
            if (problem is not null) Notice((automatic ? "Расчёт сохранён. " : "") +
                (completed > 0 ? $"Готовых файлов: {completed}. " : "") + "Карты для менеджера: " + problem);
            else if (completed == 1) Notice("Карты для менеджера готовы · " + lastFile);
            else if (completed > 1) Notice($"Карты для менеджера готовы · файлов {completed}");
            else if (automatic) Notice("");
            else Notice(format == ShopMapBundle.Pdf ? "Сохраните карту раскроя рядом с проектом." : "Сохраните хотя бы две карты раскроя рядом с проектом.");
        }
        catch (OperationCanceledException)
        {
            if (_managerMapJobs.GetValueOrDefault(key) == job)
                Notice("Сборка карт отменена.");
        }
        finally
        {
            if (_managerMapJobs.GetValueOrDefault(key) == job)
            {
                _managerMapJobs.Remove(key);
                if (!_cuttingTaskDetached) NotifyManagerMapsNotice();
            }
        }
    }
}

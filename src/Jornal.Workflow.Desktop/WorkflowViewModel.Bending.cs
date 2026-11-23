using CommunityToolkit.Mvvm.Input;
using Jornal.Workflow.Core;
using Jornal.Workflow.Infrastructure;

namespace Jornal.Workflow.Desktop;

public partial class WorkflowViewModel
{
    internal async Task SetPlannedProjectBendingAsync(Guid projectId, bool bending)
    {
        if (CleanCardProblem is { } problem) { Error = problem; return; }
        if (CurrentOrder is not { } order) return;
        if (CurrentReportReview?.Candidates.Any(c => !c.WasAccepted && c.HasManualEdits) == true)
        {
            Error = "Сохраните или отмените правки подбора перед изменением отметки гибки.";
            return;
        }
        await RunAsync(async () =>
        {
            var project = WorkflowProjects.RequireBendingPlanProject(order, projectId);
            if (project.HasPlannedBending == bending) return;
            var version = CardVersion; var actor = ActorId();
            if (bending)
            {
                if (_connection.RemoteClient is { } remote)
                    await remote.PrepareCalculationAsync(order, projectId, actor, null, null, true, CancellationToken.None, beforeFirstCalculation: true);
                else await WorkflowBendingFiles.EnsureAsync(project);
            }
            var book = await _store.WriteOrderAsync(order.Id, version, w =>
                WorkflowProjects.SetPlannedBending(w, order.Id, version, projectId, bending, actor, DateTimeOffset.UtcNow));
            if (_bendingFolderFailures is { } failure && ReferenceEquals(failure.Source, _store) &&
                failure.OrderId == order.Id && failure.Projects.Contains(projectId))
            {
                var remaining = failure.Projects.Where(id => id != projectId).ToArray();
                _bendingFolderFailures = remaining.Length == 0 ? null : (failure.Source, failure.OrderId, remaining);
            }
            if (_reportReview?.OrderId == order.Id) _reportReview = null;
            if (_openOrderId != order.Id || Projects.SingleOrDefault(p => p.Project.Id == projectId) is not { } row ||
                _workspace.ProjectRoot != book.ProjectRoot || !_workspace.Machines.SequenceEqual(book.Machines) ||
                !_workspace.Employees.SequenceEqual(book.Employees))
            {
                ApplyProjectSnapshot(book, projectId);
                return;
            }

            // The version-checked write changes one project and leaves the saved calculations intact.
            _workspace = book;
            var saved = book.Orders.Single(o => o.Id == order.Id);
            _loadedOrderVersion = saved.Version;
            _loadedOrderSnapshot = saved;
            row.ApplyPlannedBending(saved.Projects.Single(p => p.Id == projectId));
            SelectedProject = row;
            MarkResultStale();
            RefreshOrders(preserveRows: true);
            NotifyOrder();
        }, "Не удалось изменить отметку гибки. Проверьте доступ к папке проекта и базе заявок.");
    }

    // Only current unresolved folder failures are held in memory; there is no new order history.
    private (IWorkflowWorkspaceStore Source, Guid OrderId, Guid[] Projects)? _bendingFolderFailures;
    public bool HasBendingFolderProblem => FailedBendingProjects().Length > 0;
    public string BendingFolderProblemText => $"Не удалось создать папку ГИБКА ({FailedBendingProjects().Length})";
    public string BendingFolderProblemDetails => FailedBendingProjects() is { Length: > 0 } failed ?
        $"Заявка сохранена. Не удалось создать папку ГИБКА для проектов: {string.Join(", ", failed.Take(3).Select(p => p.Name))}" +
        (failed.Length > 3 ? $" и ещё {failed.Length - 3}" : "") + ". Проверьте доступ к рабочим папкам и повторите создание." : "";

    private CuttingProject[] FailedBendingProjects() => _bendingFolderFailures is { } failure &&
        ReferenceEquals(failure.Source, _store) && CurrentOrder is { } order && order.Id == failure.OrderId ?
        order.ActiveProjects.Where(p => failure.Projects.Contains(p.Id) && ProjectHasBending(order, p)).ToArray() : [];

    private static bool ProjectHasBending(WorkflowOrder order, CuttingProject project) =>
        order.Specifications.SingleOrDefault(s => s.Id == project.Id)?.Current.Parameters.HasBending ?? project.HasPlannedBending;

    private void NotifyBendingFolders()
    { OnPropertyChanged(nameof(HasBendingFolderProblem)); OnPropertyChanged(nameof(BendingFolderProblemText)); OnPropertyChanged(nameof(BendingFolderProblemDetails)); }

    private async Task EnsureSavedBendingFoldersAsync(WorkflowOrder order, IEnumerable<Guid> projectIds, CancellationToken token = default)
    {
        var failed = FailedBendingProjects().Select(p => p.Id).ToHashSet();
        foreach (var project in order.ActiveProjects.Where(p => projectIds.Contains(p.Id) && ProjectHasBending(order, p)))
        {
            try { await WorkflowBendingFiles.EnsureAsync(project, token); failed.Remove(project.Id); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failed.Add(project.Id);
                if (ex is not OperationCanceledException) Jornal.Infrastructure.Diagnostics.AppDiagnostics.Capture(
                    Jornal.Infrastructure.Diagnostics.DiagnosticOperation.Unexpected, ex);
            }
        }
        _bendingFolderFailures = failed.Count == 0 ? null : (_store, order.Id, failed.ToArray());
        NotifyBendingFolders();
    }

    [RelayCommand] private async Task RetryBendingFoldersAsync()
    {
        if (!CanUse || !IsCurrentOrderActive || RequiresDatabaseRefresh || CurrentOrder is not { HasProjectRename: false } order) return;
        var ids = FailedBendingProjects().Select(p => p.Id).ToArray();
        if (ids.Length > 0) await RunAsync(() => EnsureSavedBendingFoldersAsync(order, ids));
    }

    public async Task<string?> PrepareSelectedBendingFolderAsync(CancellationToken token = default)
    {
        if (!CanUse || !IsCurrentOrderActive || RequiresDatabaseRefresh || CurrentOrder is not { HasProjectRename: false } order ||
            SelectedDraft is not { Saved.Parameters.HasBending: true } draft ||
            order.ActiveProjects.SingleOrDefault(p => p.Id == draft.Id) is not { } project) return null;
        string? path = null;
        await RunAsync(async () =>
        {
            if (_connection.RemoteClient is { } remote)
            {
                var folder = await remote.OpenCopyAsync(order.Id, project.Id, token, OfficeCopiesRoot, project.NativePath);
                var local = WorkflowOfficeProjectPaths.FileInProject(folder, WorkflowBendingFiles.FolderName);
                if (local is null) throw new WorkflowValidationException("Не удалось определить папку ГИБКА в локальной копии.");
                path = Directory.CreateDirectory(local).FullName;
                return;
            }
            await EnsureSavedBendingFoldersAsync(order, [project.Id], token);
            if (!FailedBendingProjects().Any(p => p.Id == project.Id))
                path = Path.Combine(Path.GetDirectoryName(project.NativePath!)!, WorkflowBendingFiles.FolderName);
        });
        return path;
    }
}

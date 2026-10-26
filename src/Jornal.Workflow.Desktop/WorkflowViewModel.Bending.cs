using CommunityToolkit.Mvvm.Input;
using Jornal.Workflow.Core;
using Jornal.Workflow.Infrastructure;

namespace Jornal.Workflow.Desktop;

public partial class WorkflowViewModel
{
    // Only current unresolved folder failures are held in memory; there is no new order history.
    private (IWorkflowWorkspaceStore Source, Guid OrderId, Guid[] Projects)? _bendingFolderFailures;
    public bool HasBendingFolderProblem => FailedBendingProjects().Length > 0;
    public string BendingFolderProblemText => $"Расчёты сохранены · не удалось создать ГИБКУ ({FailedBendingProjects().Length})";
    public string BendingFolderProblemDetails => FailedBendingProjects() is { Length: > 0 } failed ?
        $"Расчёты сохранены. Не удалось создать папку ГИБКА для проектов: {string.Join(", ", failed.Take(3).Select(p => p.Name))}" +
        (failed.Length > 3 ? $" и ещё {failed.Length - 3}" : "") + ". Проверьте доступ к рабочим папкам и повторите создание." : "";

    private CuttingProject[] FailedBendingProjects() => _bendingFolderFailures is { } failure &&
        ReferenceEquals(failure.Source, _store) && CurrentOrder is { } order && order.Id == failure.OrderId ?
        order.ActiveProjects.Where(p => failure.Projects.Contains(p.Id) && order.Specifications.Any(s => s.Id == p.Id && s.Current.Parameters.HasBending)).ToArray() : [];

    private void NotifyBendingFolders()
    { OnPropertyChanged(nameof(HasBendingFolderProblem)); OnPropertyChanged(nameof(BendingFolderProblemText)); OnPropertyChanged(nameof(BendingFolderProblemDetails)); }

    private async Task EnsureSavedBendingFoldersAsync(WorkflowOrder order, IEnumerable<Guid> specificationIds, CancellationToken token = default)
    {
        var failed = FailedBendingProjects().Select(p => p.Id).ToHashSet();
        foreach (var project in order.ActiveProjects.Where(p => specificationIds.Contains(p.Id) &&
            order.Specifications.Any(s => s.Id == p.Id && s.Current.Parameters.HasBending)))
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
            await EnsureSavedBendingFoldersAsync(order, [project.Id], token);
            if (!FailedBendingProjects().Any(p => p.Id == project.Id)) path = Path.Combine(Path.GetDirectoryName(project.NativePath!)!, "ГИБКА");
        });
        return path;
    }
}

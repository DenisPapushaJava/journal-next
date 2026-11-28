using Jornal.Infrastructure.Diagnostics;
using Jornal.Workflow.Core;

namespace Jornal.Workflow.Desktop;

public partial class WorkflowViewModel
{
    private CancellationTokenSource? _officePendingCheck;
    private WorkflowOfficePendingFiles? _officePendingFiles;
    private readonly HashSet<Guid> _openedOfficeCopies = [];
    public string? OfficePendingFilesNotice => _officePendingFiles switch
    {
        { Projects.Length: > 0 } pending => $"Неотправленные файлы · {pending.Projects.Length}",
        { Incomplete: true } => "Не удалось проверить локальные файлы",
        _ => null
    };
    public bool HasOfficePendingFilesNotice => !ShowOfflineJournal && OfficePendingFilesNotice is not null;
    internal bool CanPublishOfficeFiles => !ShowOfflineJournal && _connection.Office is { CanWrite: true } && _connection.RemoteClient is not null;
    internal bool HasOfficeWorkingCopies => _connection.RemoteClient is not null;

    internal async Task<WorkflowOfficePendingFiles?> RefreshOfficePendingFilesAsync(CancellationToken token, bool recentlyOpenedOnly = false)
    {
        if (_cuttingTaskDetached || _connection.RemoteClient is not { } server) return null;
        HashSet<Guid>? scope = null;
        if (recentlyOpenedOnly)
        {
            if (_officePendingCheck is not null) return _officePendingFiles;
            scope = _openedOfficeCopies.Concat(Projects.Where(row => row.CanPublishFiles).Select(row => row.Project.Id)).ToHashSet();
            if (scope.Count == 0) return _officePendingFiles;
        }
        using var check = CancellationTokenSource.CreateLinkedTokenSource(token);
        _officePendingCheck?.Cancel();
        _officePendingCheck = check;
        try
        {
            WorkflowOfficePendingFiles result;
            try { result = await Task.Run(() => server.ScanPendingFilesAsync(check.Token, scope), check.Token); }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                AppDiagnostics.Capture(DiagnosticOperation.Unexpected, ex);
                result = new([], [], IndexUnavailable: true);
            }
            if (check.IsCancellationRequested || _cuttingTaskDetached || !ReferenceEquals(server, _connection.RemoteClient)) return null;
            if (scope is not null && _officePendingFiles is { } previous)
                result = result.IndexUnavailable ? previous with { IndexUnavailable = true } : new(
                    previous.Projects.Where(id => !scope.Contains(id)).Concat(result.Projects).ToArray(),
                    previous.Unavailable.Where(id => !scope.Contains(id)).Concat(result.Unavailable).ToArray(),
                    previous.IndexUnavailable);
            if (!result.IndexUnavailable)
            {
                var pending = result.Projects.ToHashSet();
                var unavailable = result.Unavailable.ToHashSet();
                foreach (var row in Projects.Where(row => row.CanPublishFiles && !unavailable.Contains(row.Project.Id) &&
                    (scope is null || scope.Contains(row.Project.Id))))
                    row.ApplyPendingOfficeFiles(pending.Contains(row.Project.Id));
            }
            SetOfficePendingFiles(result);
            return result;
        }
        catch (OperationCanceledException) when (check.IsCancellationRequested) { return null; }
        finally { if (ReferenceEquals(_officePendingCheck, check)) _officePendingCheck = null; }
    }

    internal Task<WorkflowOfficeFileBatch> PublishOfficePendingFilesAsync(WorkflowOfficePendingFiles files, IProgress<int> progress,
        CancellationToken token)
    {
        if (!CanPublishOfficeFiles || _connection.RemoteClient is not { } server)
            throw new WorkflowValidationException("На сервере выключено удалённое сохранение.");
        var orders = _workspace.Orders;
        return Task.Run(() => server.PublishPendingFilesAsync(files.Projects, orders, progress, token), token);
    }

    private void SetOfficePendingFiles(WorkflowOfficePendingFiles? files)
    {
        var previous = OfficePendingFilesNotice;
        _officePendingFiles = files;
        if (previous == OfficePendingFilesNotice) return;
        OnPropertyChanged(nameof(OfficePendingFilesNotice));
        OnPropertyChanged(nameof(HasOfficePendingFilesNotice));
    }

    private void ApplyOfficeProjectPending(WorkflowProjectRow row, bool pending)
    {
        row.ApplyPendingOfficeFiles(pending);
        if (_officePendingCheck is not null || _officePendingFiles is not { } files) return;
        var id = row.Project.Id;
        var wasPending = files.Projects.Contains(id);
        if (wasPending == pending && !files.Unavailable.Contains(id)) return;
        var projects = pending ? files.Projects.Append(id).Distinct().ToArray() : files.Projects.Where(p => p != id).ToArray();
        SetOfficePendingFiles(files with { Projects = projects, Unavailable = files.Unavailable.Where(p => p != id).ToArray() });
    }

    private void ResetOfficePendingFiles()
    {
        _officePendingCheck?.Cancel();
        _openedOfficeCopies.Clear();
        SetOfficePendingFiles(null);
    }
}

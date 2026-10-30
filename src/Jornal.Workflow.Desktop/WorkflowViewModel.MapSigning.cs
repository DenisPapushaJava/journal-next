using System.Collections.ObjectModel;
using Avalonia.Threading;
using Jornal.Workflow.Core;
using Jornal.Workflow.Infrastructure;

namespace Jornal.Workflow.Desktop;

public partial class WorkflowViewModel
{
    private readonly IWorkflowWordMapEditor _wordMapEditor;
    public ObservableCollection<MapSigningResult> MapSigningResults { get; } = [];
    private (IWorkflowWorkspaceStore Source, Guid OrderId, long Version)? _mapSigningContext;
    private bool _isSigningMaps, _mapSigningNoticeDismissed;
    internal long MapSigningNoticeVersion { get; private set; }
    private bool IsMapSigningContextCurrent => _mapSigningContext is { } context && ReferenceEquals(context.Source, _store) &&
        CurrentOrder?.Id == context.OrderId && CurrentOrder.Version == context.Version && !_cuttingTaskDetached;
    public bool IsSigningMaps => _isSigningMaps && IsMapSigningContextCurrent;
    public bool ShowMapSigningStatus => IsSigningMaps || HasMapSigningResults && !_mapSigningNoticeDismissed;
    public bool ShowMapSigningDetails => ShowMapSigningStatus && HasMapSigningResults && !IsSigningMaps;
    public string MapSigningStatusIcon => HasMapSigningProblems ? "!" : "✓";
    public string MapSigningStatusText => IsSigningMaps ? "Подписываем карты…" : HasMapSigningProblems ?
        $"Карты: требуют внимания {MapSigningResults.Count(r => r.NeedsAttention)}" : MapSigningSummary;
    private string? MapSigningBlockReason => string.IsNullOrWhiteSpace(CurrentOrder?.Blcb) || string.IsNullOrWhiteSpace(Blcb) || Blcb.Trim() != CurrentOrder.Blcb ?
        "Укажите и сохраните БЛЦБ заявки" : null;
    public string MapSigningHint => MapSigningBlockReason ?? "Подписать карты включённых проектов: PDF БЛОК/AWJC и DOC HansLaser через Microsoft Word в фоне";
    public bool CanSignMaps => CanUse && !IsCopyingCuttingTask && IsCurrentOrderActive && MapSigningBlockReason is null && CurrentOrder is { } order &&
        order.Items.Any(i => i.IsIncluded && order.ActiveProjects.Any(p => p.Id == i.CurrentProjectId && p.ImportProfile is "block-html/v1" or "hans-pdf/v1" or "waterjet-awjc/v1"));
    public bool HasMapSigningResults => MapSigningResults.Count > 0 && IsMapSigningContextCurrent;
    public bool HasMapSigningProblems => HasMapSigningResults && MapSigningResults.Any(r => r.NeedsAttention);
    public string MapSigningSummary => $"Карты: подписано {MapSigningResults.Count(r => r.Status == MapSigningStatus.Signed)}, " +
        $"без изменений {MapSigningResults.Count(r => r.Status == MapSigningStatus.Unchanged)}" +
        (MapSigningResults.Count(r => r.NeedsAttention) is > 0 and var count ? $", требуют внимания {count}" : "");

    private void NotifyMapSigning()
    {
        if (_mapSigningContext is not null && !IsMapSigningContextCurrent) _mapSigningNoticeDismissed = true;
        foreach (var row in Projects) row.ApplyMapSigningBlock(MapSigningBlockReason);
        OnPropertyChanged(nameof(CanSignMaps)); OnPropertyChanged(nameof(MapSigningHint)); OnPropertyChanged(nameof(HasMapSigningResults));
        OnPropertyChanged(nameof(HasMapSigningProblems)); OnPropertyChanged(nameof(MapSigningSummary));
        foreach (var name in new[] { nameof(IsSigningMaps), nameof(ShowMapSigningStatus), nameof(ShowMapSigningDetails),
            nameof(MapSigningStatusText), nameof(MapSigningStatusIcon), nameof(MapSigningNoticeVersion) }) OnPropertyChanged(name);
    }

    internal void DismissMapSigningSuccess(long version)
    {
        if (version != MapSigningNoticeVersion || IsSigningMaps || HasMapSigningProblems || !HasMapSigningResults) return;
        _mapSigningNoticeDismissed = true; NotifyMapSigning();
    }

    private void DismissPreviousMapSigningNotice()
    {
        if (!_isSigningMaps) _mapSigningNoticeDismissed = true;
    }

    private void MapSigningProblem(WorkflowOrder order, string message)
    {
        _mapSigningContext = (_store, order.Id, order.Version); _mapSigningNoticeDismissed = false; MapSigningNoticeVersion++;
        MapSigningResults.Clear(); MapSigningResults.Add(new(Guid.Empty, "Заявка " + order.DisplayNumber, null, MapSigningStatus.Failed, 0, message));
        NotifyMapSigning();
    }

    internal void ApplyMapSigningResults(MapSigningResult[] results)
    {
        MapSigningResults.Clear(); foreach (var result in results) MapSigningResults.Add(result);
        if (results.Any(r => r.MayHaveChanged)) ResetShopTransfer();
        NotifyMapSigning();
    }

    public async Task SignMapsAsync(Guid? projectId = null, CancellationToken token = default)
    {
        if (!CanUse || !IsCurrentOrderActive || IsCopyingCuttingTask || CurrentOrder is not { } order) return;
        if (MapSigningBlockReason is { } block) { MapSigningProblem(order, block + "."); return; }
        if (IsCurrentCardStale || RequiresDatabaseRefresh) { MapSigningProblem(order, "Заявка изменена. Проверьте изменения и обновите журнал (F5)."); return; }
        if (HasCurrentCardEdits || HasPendingReportEdits) { MapSigningProblem(order, "Сохраните или отмените изменения карточки перед подписанием карт."); return; }
        if (projectId is { } id && !order.ActiveProjects.Any(p => p.Id == id)) return;
        var source = _store; var snapshot = _workspace;
        var projects = projectId is { } one ? order.ActiveProjects.Where(p => p.Id == one).ToArray() :
            order.ActiveProjects.Where(p => order.Items.Any(i => i.IsIncluded && i.CurrentProjectId == p.Id)).ToArray();
        async Task Validate(CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_cuttingTaskDetached || !ReferenceEquals(source, _store) || CurrentOrder?.Id != order.Id || CurrentOrder.Version != order.Version || HasCurrentCardEdits)
                    throw new WorkflowValidationException("Карточка изменилась во время подписания. Уже подписанные файлы сохранены; подготовьте оставшиеся карты заново.");
            });
            var fresh = await source.ReadOrderAsync(order.Id, cancellation);
            var current = fresh.Orders.SingleOrDefault(o => o.Id == order.Id);
            if (current is null || current.IsDeleted || current.Version != order.Version ||
                projects.Any(p => snapshot.Machines.Single(m => m.Id == p.MachineId) != fresh.Machines.SingleOrDefault(m => m.Id == p.MachineId)) ||
                snapshot.Employees.SingleOrDefault(e => e.Id == order.ManagerId)?.Name != fresh.Employees.SingleOrDefault(e => e.Id == order.ManagerId)?.Name ||
                snapshot.Customers.SingleOrDefault(c => c.Id == order.CustomerId)?.Name != fresh.Customers.SingleOrDefault(c => c.Id == order.CustomerId)?.Name)
                throw new WorkflowValidationException("Заявка или её реквизиты изменены другим пользователем. Обновите журнал (F5) перед подписанием.");
        }
        await RunAsync(async () =>
        {
            MapSigningResults.Clear(); _mapSigningContext = (source, order.Id, order.Version);
            _isSigningMaps = true; _mapSigningNoticeDismissed = false; MapSigningNoticeVersion++; NotifyMapSigning();
            try
            {
                await Validate(token);
                var results = await WorkflowMapSigning.SignAsync(order, snapshot, projectId, Validate, token, _wordMapEditor);
                if (!IsMapSigningContextCurrent) return;
                ApplyMapSigningResults(results);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { _mapSigningNoticeDismissed = true; }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                if (IsMapSigningContextCurrent) MapSigningProblem(order, ex is WorkflowValidationException or WorkflowConflictException or WorkflowOutcomeUnknownException ? ex.Message :
                    "Не удалось подписать карты. Проверьте доступ к папке проекта и закройте занятые файлы.");
                Jornal.Infrastructure.Diagnostics.AppDiagnostics.Capture(Jornal.Infrastructure.Diagnostics.DiagnosticOperation.Unexpected, ex);
            }
            finally { _isSigningMaps = false; NotifyMapSigning(); }
        });
    }
}

using System.ComponentModel;
using Avalonia.Threading;

namespace Jornal.Workflow.Desktop;

public partial class WorkflowWindow
{
    private readonly TimeProvider _mapStatusClock;
    private WorkflowViewModel? _mapStatusModel;
    private ITimer? _mapStatusHide;
    private long _mapStatusSchedule, _mapStatusNotice = -1;
    private bool _mapStatusStopped;

    private void InitializeMapSigningStatus()
    {
        DataContextChanged += OnMapSigningModelChanged;
        AttachMapSigningModel();
    }

    private void OnMapSigningModelChanged(object? sender, EventArgs e) => AttachMapSigningModel();
    private void AttachMapSigningModel()
    {
        if (_mapStatusModel is { } old) old.PropertyChanged -= OnMapSigningStatusChanged;
        _mapStatusModel = null; CancelMapSigningHide(); WorkflowMapSigningDetails.Flyout?.Hide();
        _mapStatusNotice = -1;
        if (_mapStatusStopped) return;
        _mapStatusModel = Model;
        if (_mapStatusModel is { } model) model.PropertyChanged += OnMapSigningStatusChanged;
        SyncMapSigningStatus();
    }

    private void OnMapSigningStatusChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WorkflowViewModel.ShowMapSigningStatus) or nameof(WorkflowViewModel.IsSigningMaps) or
            nameof(WorkflowViewModel.HasMapSigningResults) or nameof(WorkflowViewModel.MapSigningNoticeVersion)) SyncMapSigningStatus();
    }

    private void SyncMapSigningStatus()
    {
        if (_mapStatusStopped || !IsVisible || _mapStatusModel is not { } model || !ReferenceEquals(Model, model))
        { CancelMapSigningHide(); return; }
        if (_mapStatusNotice != model.MapSigningNoticeVersion)
        {
            _mapStatusNotice = model.MapSigningNoticeVersion;
            CancelMapSigningHide(); WorkflowMapSigningDetails.Flyout?.Hide();
        }
        if (!model.ShowMapSigningStatus || model.IsSigningMaps)
        { CancelMapSigningHide(); WorkflowMapSigningDetails.Flyout?.Hide(); return; }
        if (model.HasMapSigningProblems || WorkflowMapSigningDetails.Flyout?.IsOpen == true)
        { CancelMapSigningHide(); return; }
        if (_mapStatusHide is null) ScheduleMapSigningHide(model);
    }

    private void ScheduleMapSigningHide(WorkflowViewModel model)
    {
        CancelMapSigningHide(); var schedule = _mapStatusSchedule; var notice = model.MapSigningNoticeVersion;
        _mapStatusHide = _mapStatusClock.CreateTimer(_ => Dispatcher.UIThread.Post(() =>
        {
            if (_mapStatusStopped || schedule != _mapStatusSchedule ||
                !ReferenceEquals(Model, model) || notice != model.MapSigningNoticeVersion) return;
            CancelMapSigningHide();
            if (WorkflowMapSigningDetails.IsPointerOver || WorkflowMapSigningDetails.IsKeyboardFocusWithin || WorkflowMapSigningDetails.Flyout?.IsOpen == true)
            { SyncMapSigningStatus(); return; }
            model.DismissMapSigningSuccess(notice);
        }), null, TimeSpan.FromSeconds(5), Timeout.InfiniteTimeSpan);
    }

    private void CancelMapSigningHide()
    {
        _mapStatusSchedule++;
        _mapStatusHide?.Dispose(); _mapStatusHide = null;
    }

    private void OnMapSigningDetailsOpened(object? sender, EventArgs e) => CancelMapSigningHide();
    private void OnMapSigningDetailsClosed(object? sender, EventArgs e) => SyncMapSigningStatus();

    private void StopMapSigningStatus()
    {
        _mapStatusStopped = true; DataContextChanged -= OnMapSigningModelChanged;
        AttachMapSigningModel();
    }
}

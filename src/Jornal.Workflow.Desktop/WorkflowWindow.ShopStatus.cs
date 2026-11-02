using System.ComponentModel;
using Avalonia.Threading;

namespace Jornal.Workflow.Desktop;

public partial class WorkflowWindow
{
    private readonly TimeProvider _shopStatusClock;
    private WorkflowViewModel? _shopStatusModel;
    private WorkflowShopTransferViewModel? _shopStatusPreview;
    private ITimer? _shopStatusHide;
    private long _shopStatusSchedule;
    private bool _shopStatusStopped;

    private void InitializeShopStatus()
    { DataContextChanged += OnShopStatusContextChanged; AttachShopStatus(); }

    private void OnShopStatusContextChanged(object? sender, EventArgs e) => AttachShopStatus();
    private void OnShopStatusModelChanged(object? sender, PropertyChangedEventArgs e)
    { if (e.PropertyName == nameof(WorkflowViewModel.ShopTransfer)) AttachShopStatus(); }

    private void AttachShopStatus()
    {
        CancelShopStatusHide();
        if (_shopStatusModel is { } old) old.PropertyChanged -= OnShopStatusModelChanged;
        if (_shopStatusPreview is { } previous) previous.PropertyChanged -= OnShopStatusChanged;
        _shopStatusModel = _shopStatusStopped ? null : Model;
        _shopStatusPreview = _shopStatusModel?.ShopTransfer;
        if (_shopStatusModel is { } model) model.PropertyChanged += OnShopStatusModelChanged;
        if (_shopStatusPreview is { } preview) preview.PropertyChanged += OnShopStatusChanged;
        SyncShopStatus();
    }

    private void OnShopStatusChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WorkflowShopTransferViewModel.CanDismissNotice) or nameof(WorkflowShopTransferViewModel.ShowNotice) or
            nameof(WorkflowShopTransferViewModel.Error)) SyncShopStatus();
    }

    private void SyncShopStatus()
    {
        if (_shopStatusStopped || !IsVisible || _shopStatusPreview is not { CanDismissNotice: true, ShowNotice: true } preview)
        { CancelShopStatusHide(); return; }
        if (_shopStatusHide is not null) return;
        var schedule = _shopStatusSchedule;
        _shopStatusHide = _shopStatusClock.CreateTimer(_ => Dispatcher.UIThread.Post(() =>
        {
            if (_shopStatusStopped || schedule != _shopStatusSchedule || !ReferenceEquals(Model?.ShopTransfer, preview)) return;
            CancelShopStatusHide();
            if (WorkflowShopTransferDetails.IsPointerOver || WorkflowShopTransferDetails.IsKeyboardFocusWithin || OwnedWindows.Any(w => ReferenceEquals(w.DataContext, preview)))
            { SyncShopStatus(); return; }
            preview.DismissSuccessNotice();
        }), null, TimeSpan.FromSeconds(5), Timeout.InfiniteTimeSpan);
    }

    private void CancelShopStatusHide()
    { _shopStatusSchedule++; _shopStatusHide?.Dispose(); _shopStatusHide = null; }

    private void StopShopStatus()
    { _shopStatusStopped = true; DataContextChanged -= OnShopStatusContextChanged; AttachShopStatus(); }
}

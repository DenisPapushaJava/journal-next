using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Jornal.Desktop.Controls;
using Jornal.Desktop.Tables;

namespace Jornal.Workflow.Desktop;

public partial class WorkflowWindow
{
    private TableContextMenu _orderContextMenu = null!;
    private WorkflowViewModel? _orderContextModel;

    private void InitializeOrderContextMenu()
    {
        // The existing selection binding switches cards and preserves their drafts.
        _orderContextMenu = new(WorkflowOrdersGrid, CanOpenOrderContextMenu, PopulateOrderContextMenu, () => { });
        DataContextChanged += OnOrderContextModelChanged; AttachOrderContextModel();
    }

    private bool CanOpenOrderContextMenu() => Model is { CanUse: true, IsCreating: false } && OwnedWindows.Count == 0;
    private void OnOrderContextModelChanged(object? sender, EventArgs e) => AttachOrderContextModel();
    private void AttachOrderContextModel()
    {
        _orderContextMenu.Close(); _orderContextMenu.Menu.Items.Clear();
        if (_orderContextModel is { } previous) previous.PropertyChanged -= OnOrderContextChanged;
        _orderContextModel = Model;
        if (_orderContextModel is { } model) model.PropertyChanged += OnOrderContextChanged;
    }

    private void OnOrderContextChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WorkflowViewModel.SelectedOrder) or nameof(WorkflowViewModel.Search) or
            nameof(WorkflowViewModel.FilterDate) or nameof(WorkflowViewModel.WorkspacePath) or nameof(WorkflowViewModel.DatabaseId) or
            nameof(WorkflowViewModel.IsCreating) or nameof(WorkflowViewModel.IsCurrentOrderActive) or nameof(WorkflowViewModel.HeaderSummary) ||
            e.PropertyName == nameof(WorkflowViewModel.CanUse) && Model?.CanUse != true)
            _orderContextMenu.Close();
        else if (_orderContextMenu.Menu.IsOpen && Model is { } model)
        {
            void Update(string name, bool enabled, string hint)
            {
                if (_orderContextMenu.Menu.Items.OfType<MenuItem>().SingleOrDefault(i => i.Name == name) is not { } item) return;
                item.IsEnabled = enabled; ToolTip.SetTip(item, hint);
            }
            if (e.PropertyName == nameof(WorkflowViewModel.CanOpenResult)) Update("WorkflowOrderMenuResult", model.CanOpenResult, model.OpenResultHint);
            if (e.PropertyName == nameof(WorkflowViewModel.CanSendToWork)) Update("WorkflowOrderMenuSend", model.CanSendToWork, model.SendToWorkHint);
            if (e.PropertyName == nameof(WorkflowViewModel.CanMoveOrderToTrash)) Update("WorkflowOrderMenuTrash", model.CanMoveOrderToTrash, model.MoveOrderToTrashHint);
        }
    }

    private void PopulateOrderContextMenu(TableMenuTarget target, ContextMenu menu)
    {
        if (target.IsHeader || target.Item is not WorkflowOrderRow row || Model is not { } model ||
            !ReferenceEquals(model.SelectedOrder, row) || !model.IsCardVisible || !model.IsCurrentOrderActive || row.IsMissing) return;
        var menuVersion = _orderContextMenu.Version; var orderVersion = row.Order.Version;
        var path = model.WorkspacePath; var database = model.DatabaseId; var search = model.Search; var date = model.FilterDate;
        bool Current() => _orderContextMenu.Version == menuVersion && menu.IsOpen && IsVisible &&
            ReferenceEquals(Model, model) && CanOpenOrderContextMenu() && model.IsCardVisible && model.IsCurrentOrderActive &&
            ReferenceEquals(model.SelectedOrder, row) && model.Orders.Contains(row) && !row.IsMissing && !row.Order.IsDeleted &&
            row.Order.Version == orderVersion && model.WorkspacePath == path && model.DatabaseId == database && model.Search == search && model.FilterDate == date;
        void Add(string name, string caption, bool enabled, EventHandler<RoutedEventArgs> action, bool danger = false, string? hint = null)
        {
            var item = JournalMenu.Item(name, caption, enabled, action, Current, _orderContextMenu.Close, hint: hint, danger: danger);
            ToolTip.SetShowOnDisabled(item, true); menu.Items.Add(item);
        }

        Add("WorkflowOrderMenuResult", "Результат", model.CanOpenResult, (_, _) =>
        {
            model.OpenResultCommand.Execute(null);
            Dispatcher.UIThread.Post(() =>
            {
                if (IsVisible && ReferenceEquals(Model, model) && ReferenceEquals(model.SelectedOrder, row) && model.IsResultTab)
                    WorkflowResultEditor.Focus();
            }, DispatcherPriority.Loaded);
        }, hint: model.OpenResultHint);
        Add("WorkflowOrderMenuBlcb", string.IsNullOrWhiteSpace(model.Blcb) ? "Добавить БЛЦБ…" : "Изменить БЛЦБ…", model.CanUse, (_, _) =>
        {
            OrderHeaderExpander.IsExpanded = true;
            Dispatcher.UIThread.Post(() =>
            {
                if (!IsVisible || !ReferenceEquals(Model, model) || !ReferenceEquals(model.SelectedOrder, row) || !model.IsCurrentOrderActive) return;
                WorkflowBlcbInput.Focus(); WorkflowBlcbInput.SelectAll();
            }, DispatcherPriority.Loaded);
        });
        Add("WorkflowOrderMenuSend", "Отправить в работу…", model.CanSendToWork, OnPrepareShopTransfer, hint: model.SendToWorkHint);
        menu.Items.Add(new Separator());
        Add("WorkflowOrderMenuTrash", "В корзину…", model.CanMoveOrderToTrash, async (_, _) => await ShowOrderTrashConfirmationAsync(returnToJournal: true), danger: true, hint: model.MoveOrderToTrashHint);
    }

    private void StopOrderContextMenu()
    {
        DataContextChanged -= OnOrderContextModelChanged;
        if (_orderContextModel is { } model) model.PropertyChanged -= OnOrderContextChanged;
        _orderContextModel = null; _orderContextMenu.Dispose(); _orderContextMenu.Menu.Items.Clear();
    }
}

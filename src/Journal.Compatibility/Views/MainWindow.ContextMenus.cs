using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.VisualTree;
using Jornal.Core.Orders;
using Jornal.Desktop.Controls;
using Jornal.Desktop.Tables;
using Jornal.Desktop.ViewModels;

namespace Jornal.Desktop.Views;

public partial class MainWindow
{
    private TableContextMenu _ordersMenu = null!;
    private readonly ContextMenu _resultMenu = new();
    private int _resultMenuVersion;

    private void ConfigureContextMenus()
    {
        _ordersMenu = new(OrdersGrid, () => _viewModel?.CanUseJournal == true && OwnedWindows.Count == 0,
            PopulateOrderMenu, UpdateSelection);
        ResultTextBox.ContextFlyout = null;
        ResultTextBox.ContextMenu = _resultMenu;
        ResultTextBox.AddHandler(InputElement.KeyUpEvent, JournalMenu.OnContextKeyUp, RoutingStrategies.Tunnel);
        _resultMenu.Opening += (_, e) =>
        {
            if (_viewModel is null || OwnedWindows.Count > 0) { e.Cancel = true; return; }
            PopulateResultMenu();
        };
    }

    private void CloseContextMenus()
    { _ordersMenu.Close(); _resultMenuVersion++; _resultMenu.Close(); }

    private void PopulateOrderMenu(TableMenuTarget target, ContextMenu menu)
    {
        if (_viewModel is not { } model) return;
        var version = _ordersMenu.Version;
        var selected = OrdersGrid.SelectedItems.Cast<OrderRowViewModel>().Select(row => row.Id).ToHashSet();
        bool Valid() => _ordersMenu.Version == version && menu.IsOpen && ReferenceEquals(model, _viewModel) && model.CanUseJournal &&
            selected.SetEquals(OrdersGrid.SelectedItems.Cast<OrderRowViewModel>().Select(row => row.Id));
        MenuItem Add(string name, string label, bool enabled, EventHandler<RoutedEventArgs> action, string? icon = null, string? hint = null, bool danger = false)
        {
            var item = JournalMenu.Item(name, label, enabled, action, Valid, CloseContextMenus, icon, hint, danger);
            menu.Items.Add(item); return item;
        }
        if (target.IsHeader)
        {
            PopulateColumnMenu(target.Column, Add, menu); return;
        }
        if (target.Item is not OrderRowViewModel row || !model.Orders.Contains(row))
        {
            Add("OrderMenuNew", "Новая заявка…", model.CanCreateOrder, OnNewOrderClick, "plus");
            Add("OrderMenuRefresh", "Обновить", model.CanEnterSearch, (_, _) => model.ReloadCommand.Execute(null), "refresh-cw");
            return;
        }
        menu.Items.Add(JournalMenu.Heading(selected.Count == 1 ? "№ " + row.SpecificationNumber : $"Выбрано: {selected.Count}"));
        Add("OrderMenuEdit", "Редактировать…", model.CanEditOrder, OnEditOrderClick, "pencil", model.EditOrderHint);
        Add("OrderMenuDelivery", "Добавить БЛЦБ…", model.CanUpdateSelectedOrders, OnDeliveryNumberClick, "hash");
        menu.Items.Add(new Separator());
        Add("OrderMenuResult", "Сформировать результат", model.CanAddResult, OnAddResultClick, "list-checks");
        Add("OrderMenuRowComment", "Скопировать комментарий строки", model.CanCopyRowComment, OnCopyRowCommentClick, "message-square", model.RowCommentHint);
        Add("OrderMenuCuttingTask", "Скопировать задание на резку", model.CanCopyTelegramComment, OnCopyTelegramCommentClick, "clipboard-copy");
        Add("OrderMenuFolders", "Создать папки…", model.CanPrepareFolders, OnCreateFoldersClick, "folder-plus");
        menu.Items.Add(new Separator());
        var colors = new MenuItem { Name = "OrderMenuColors", Header = "Статус и цвет", IsEnabled = model.CanUpdateSelectedOrders };
        foreach (var (name, label, color) in new[] { ("Work", "В работе", OrderColors.Work), ("Ready", "Готово", OrderColors.Ready), ("Shipped", "Отгружено", OrderColors.Shipped) })
            colors.Items.Add(JournalMenu.Item("OrderMenu" + name, label, model.CanUpdateSelectedOrders,
                (_, _) => model.SetOrderColorCommand.Execute(color), Valid, CloseContextMenus));
        colors.Items.Add(new Separator());
        colors.Items.Add(JournalMenu.Item("OrderMenuColor", "Цвет…", model.CanUpdateSelectedOrders, OnSetCustomColorClick, Valid, CloseContextMenus, "palette"));
        colors.Items.Add(JournalMenu.Item("OrderMenuClearColor", "Снять цвет", model.CanUpdateSelectedOrders,
            (_, _) => model.SetOrderColorCommand.Execute(OrderColors.None), Valid, CloseContextMenus));
        menu.Items.Add(colors);
        Add("OrderMenuClearSelection", "Снять выбор", model.CanClearSelection, OnClearSelection, "x");
        menu.Items.Add(new Separator());
        Add("OrderMenuTrash", selected.Count > 1 ? $"В корзину ({selected.Count})…" : "В корзину…", model.CanTrashOrder, OnTrashOrderClick, "trash", danger: true);
    }

    private void PopulateColumnMenu(DataGridColumn? column,
        Func<string, string, bool, EventHandler<RoutedEventArgs>, string?, string?, bool, MenuItem> add, ContextMenu menu)
    {
        if (column is null || _viewModel is not { } model) return;
        if (OrdersGrid.Columns.IndexOf(column) == 0)
        { add("ColumnMenuSettings", "Настроить таблицу…", true, (_, _) => ColumnsButton.Flyout?.ShowAt(ColumnsButton), "columns-3", null, false); return; }
        add("ColumnMenuAscending", "По возрастанию", column.CanUserSort, (_, _) => _sorting.SortColumn(column, ListSortDirection.Ascending), null, null, false);
        add("ColumnMenuDescending", "По убыванию", column.CanUserSort, (_, _) => _sorting.SortColumn(column, ListSortDirection.Descending), null, null, false);
        add("ColumnMenuDefault", "Стандартный порядок", model.HasCustomSort, (_, _) => model.ResetSortingCommand.Execute(null), "rotate-ccw", null, false);
        var header = OrdersGrid.GetVisualDescendants().OfType<DataGridColumnHeader>().FirstOrDefault(item => Equals(item.Content, column.Header));
        var filter = header?.GetVisualDescendants().OfType<ColumnFilterHeader>().FirstOrDefault();
        var status = header?.GetVisualDescendants().OfType<StatusFilterHeader>().FirstOrDefault();
        if (filter is not null || status is not null)
            add("ColumnMenuFilter", "Фильтр…", true, (_, _) => { if (filter is not null) filter.ShowFilter(); else status!.ShowFilter(); }, "funnel", null, false);
        menu.Items.Add(new Separator());
        add("ColumnMenuHide", "Скрыть столбец", true, (_, _) => column.IsVisible = false, "columns-3", null, false);
    }

    private void PopulateResultMenu()
    {
        if (_viewModel is not { } model) return;
        _resultMenu.Items.Clear();
        var version = ++_resultMenuVersion;
        bool Valid() => _resultMenuVersion == version && _resultMenu.IsOpen && ReferenceEquals(model, _viewModel) && OwnedWindows.Count == 0;
        void Add(string name, string label, bool enabled, EventHandler<RoutedEventArgs> action, string? icon = null, bool danger = false) =>
            _resultMenu.Items.Add(JournalMenu.Item(name, label, enabled, action, Valid, CloseContextMenus, icon, danger: danger));
        Add("ResultMenuUndo", "Отменить", ResultTextBox.CanUndo && !ResultTextBox.IsReadOnly, (_, _) => ResultTextBox.Undo());
        _resultMenu.Items.Add(new Separator());
        Add("ResultMenuCut", "Вырезать", ResultTextBox.CanCut, (_, _) => ResultTextBox.Cut());
        Add("ResultMenuCopy", "Копировать выделенный текст", ResultTextBox.CanCopy, (_, _) => ResultTextBox.Copy());
        Add("ResultMenuPaste", "Вставить", ResultTextBox.CanPaste, (_, _) => ResultTextBox.Paste());
        Add("ResultMenuSelectAll", "Выделить всё", ResultTextBox.Text?.Length > 0, (_, _) => ResultTextBox.SelectAll());
        _resultMenu.Items.Add(new Separator());
        Add("ResultMenuEmail", "Копировать для письма", model.CanCopyResult, OnCopyResultClick, "clipboard-copy");
        Add("ResultMenuClear", "Очистить результат", model.CanClearResult, (_, _) => model.ClearResultCommand.Execute(null), "x");
    }
}

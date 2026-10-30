using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Jornal.Desktop.Controls;
using Jornal.Workflow.Desktop;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class WorkflowOrderContextMenuTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private sealed class RecordingWindow(WorkflowPreferencesStore preferences) : WorkflowWindow(preferences)
    {
        internal string? Copied;
        protected override Task WriteCuttingTaskClipboardAsync(string text) { Copied = text; return Task.CompletedTask; }
    }
    private static async Task Ready(ProjectFixture f)
    { var project = await f.AddAsync(); await ProjectFixture.WriteAsync(project); await f.AcceptAsync(); }
    private static async Task NewOrder(ProjectFixture f, string customer)
    { f.Model.NewOrderCommand.Execute(null); f.Model.NewManager = "Иванов"; f.Model.NewCustomer = customer; await f.Model.CreateOrderCommand.ExecuteAsync(null); }
    private static RecordingWindow Window(ProjectFixture f, bool dark = false, int width = 1180) =>
        new(new(Path.Combine(f.DirectoryPath, "preferences.json"))) { DataContext = f.Model, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light, Width = width, Height = 768 };

    [AvaloniaTheory]
    [InlineData(false, 1180)] [InlineData(true, 1180)] [InlineData(false, 1366)] [InlineData(true, 1366)]
    public async Task FourActionsUseClickedOrderAndPreserveOtherCardDrafts(bool dark, int width)
    {
        using var log = new WorkflowWindowTests.BindingLog(); using var f = new ProjectFixture(); await f.InitializeAsync(); await Ready(f);
        var firstId = f.Model.SelectedOrder!.Order.Id; f.Model.ResultText = "Ручной результат первой заявки";
        await NewOrder(f, "Вторая заявка"); await Ready(f); var secondId = f.Model.SelectedOrder!.Order.Id;
        var secondProject = f.Model.SelectedProject!.Project;
        var window = Window(f, dark, width);
        try
        {
            window.Show(); await Until(() => !f.Model.IsBusy); var grid = window.FindControl<DataGrid>("WorkflowOrdersGrid")!;
            f.Model.SelectedOrder = f.Model.Orders.Single(o => o.Order.Id == firstId);
            var firstDraft = f.Model.SelectedDraft!; firstDraft.Coefficient = "1,7";
            var before = JsonSerializer.Serialize(await f.Store.ReadAsync(Token));
            RightClick(Row(grid, secondId)); var menu = grid.ContextMenu!; Assert.True(menu.IsOpen);
            Assert.Equal(secondId, f.Model.SelectedOrder!.Order.Id);
            Assert.Equal(new[] { "Результат", "Добавить БЛЦБ…", "Отправить в работу…", "В корзину…" }, menu.Items.OfType<MenuItem>().Select(i => i.Header));
            Assert.IsType<Separator>(menu.Items[3]); Assert.Equal(5, menu.Items.Count);
            Assert.Equal(before, JsonSerializer.Serialize(await f.Store.ReadAsync(Token))); Assert.Null(window.Copied);
            Assert.Equal("Отправить в работу…", window.FindControl<JournalButton>("CopyWorkflowCuttingTask")!.Label);
            InWindow(window.FindControl<Button>("CopyWorkflowCuttingTask")!, window); Frame(window, $"order-menu-{width}-{dark}");
            Click(Item(menu, "WorkflowOrderMenuResult")); Assert.False(menu.IsOpen); Assert.Equal(1, f.Model.OrderTabIndex); Assert.NotEmpty(f.Model.ResultText);
            f.Model.ResultText = "Исправленный результат второй заявки";
            RightClick(Row(grid, secondId)); Click(Item(menu, "WorkflowOrderMenuResult"));
            Assert.Equal("Исправленный результат второй заявки", f.Model.ResultText); Assert.Null(window.Copied);
            f.Model.SelectedOrder = f.Model.Orders.Single(o => o.Order.Id == firstId); Pump();
            Assert.Same(firstDraft, f.Model.SelectedDraft); Assert.Equal("1,7", firstDraft.Coefficient);
            Assert.Equal("Ручной результат первой заявки", f.Model.ResultText);

            RightClick(Row(grid, secondId)); Click(Item(menu, "WorkflowOrderMenuBlcb"));
            var blcb = window.FindControl<TextBox>("WorkflowBlcbInput")!; await Until(() => blcb.IsFocused);
            Assert.True(window.FindControl<Expander>("OrderHeaderExpander")!.IsExpanded);
            Assert.Equal(before, JsonSerializer.Serialize(await f.Store.ReadAsync(Token)));
            window.KeyTextInput("009337"); Pump(); Assert.Equal("009337", f.Model.Blcb);
            Assert.Equal(before, JsonSerializer.Serialize(await f.Store.ReadAsync(Token)));
            Click(window.FindControl<Button>("SaveWorkflowHeader")!); await Until(() => !f.Model.IsBusy && f.Model.SelectedOrder?.Order.Blcb == "009337");
            RightClick(Row(grid, secondId)); Assert.Equal("Изменить БЛЦБ…", Item(menu, "WorkflowOrderMenuBlcb").Header);
            Click(Item(menu, "WorkflowOrderMenuTrash")); await Until(() => window.OwnedWindows.OfType<WorkflowTrashConfirmationWindow>().Any());
            var confirmation = window.OwnedWindows.OfType<WorkflowTrashConfirmationWindow>().Single();
            Assert.Contains("Вторая заявка", Assert.IsType<WorkflowTrashConfirmationViewModel>(confirmation.DataContext).Customer);
            Click(confirmation.FindControl<Button>("CancelWorkflowTrash")!); await Until(() => !confirmation.IsVisible);
            Assert.True(grid.IsKeyboardFocusWithin); Assert.Equal(2, f.Model.Orders.Count);
            RightClick(Row(grid, secondId)); Click(Item(menu, "WorkflowOrderMenuTrash"));
            await Until(() => window.OwnedWindows.OfType<WorkflowTrashConfirmationWindow>().Any());
            confirmation = window.OwnedWindows.OfType<WorkflowTrashConfirmationWindow>().Single(); Click(confirmation.FindControl<Button>("ConfirmWorkflowTrash")!);
            await Until(() => !confirmation.IsVisible); Assert.Equal(firstId, Assert.Single(f.Model.Orders).Order.Id);
            Assert.True(File.Exists(secondProject.NativePath));
            f.Model.SelectedOrder = f.Model.Orders.Single(); Assert.Same(firstDraft, f.Model.SelectedDraft);
            Assert.Equal("Ручной результат первой заявки", f.Model.ResultText); Assert.Empty(log.Errors);
        }
        finally { foreach (var child in window.OwnedWindows.ToArray()) child.Close(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task KeyboardEmptyAreaBusyStateAndStaleItemsKeepTheirScope()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); await Ready(f); var window = Window(f);
        try
        {
            window.Show(); await Until(() => !f.Model.IsBusy); var grid = window.FindControl<DataGrid>("WorkflowOrdersGrid")!; var menu = grid.ContextMenu!;
            grid.Focus(); Press(window, Key.F10, PhysicalKey.F10, RawInputModifiers.Shift); Assert.True(menu.IsOpen);
            var obsolete = Item(menu, "WorkflowOrderMenuResult");
            Press(TopLevel.GetTopLevel(menu)!, Key.Escape, PhysicalKey.Escape); Assert.False(menu.IsOpen); Assert.True(grid.IsKeyboardFocusWithin);
            Press(window, Key.Apps, PhysicalKey.ContextMenu); Assert.True(menu.IsOpen);
            obsolete.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Assert.True(menu.IsOpen); Assert.Empty(f.Model.ResultText);
            f.Model.Search = "Пример"; Pump(); Assert.False(menu.IsOpen); obsolete.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Assert.Empty(f.Model.ResultText);
            f.Model.Search = ""; Pump();
            RightClick(grid, new Point(20, grid.Bounds.Height - 10)); Assert.False(menu.IsOpen);
            RightClick(grid, new Point(30, 15)); Assert.False(menu.IsOpen);
            RightClick(Row(grid, f.Model.Orders.Single().Order.Id)); Assert.True(menu.IsOpen);
            var stale = Item(menu, "WorkflowOrderMenuBlcb"); f.Model.FilterDate = f.Model.FilterDate is null ? DateTime.Today : null; Pump(); Assert.False(menu.IsOpen);
            RightClick(Row(grid, f.Model.Orders.Single().Order.Id)); stale.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.True(menu.IsOpen); Assert.False(window.FindControl<Expander>("OrderHeaderExpander")!.IsExpanded); menu.Close();
            f.Model.IsBusy = true; RightClick(Row(grid, f.Model.Orders.Single().Order.Id)); Assert.False(menu.IsOpen); f.Model.IsBusy = false;
            f.Model.NewOrderCommand.Execute(null); f.Model.NewCustomer = "Черновик"; RightClick(Row(grid, f.Model.Orders.Single().Order.Id));
            Assert.False(menu.IsOpen); Assert.True(f.Model.IsCreating); Assert.Equal("Черновик", f.Model.NewCustomer); f.Model.CancelNewOrderCommand.Execute(null);
            RightClick(Row(grid, f.Model.Orders.Single().Order.Id)); Assert.True(menu.IsOpen); window.DataContext = null; Pump(); Assert.False(menu.IsOpen);
            window.DataContext = f.Model; Pump(); RightClick(Row(grid, f.Model.Orders.Single().Order.Id)); Assert.True(menu.IsOpen);
            window.Close(); Assert.False(menu.IsOpen); Assert.Null(grid.ContextMenu);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task SendUsesExistingReviewCopyAndHandoffFlowForClickedOrder()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); await Ready(f); var firstId = f.Model.SelectedOrder!.Order.Id;
        var project = f.Model.SelectedProject!.Project; f.Model.Blcb = "009337"; await f.Model.SaveHeaderCommand.ExecuteAsync(null);
        await f.Model.SaveShopRootAsync(null, Path.Combine(f.DirectoryPath, "shop"), Token);
        var source = Path.GetDirectoryName(project.NativePath!)!;
        await File.WriteAllTextAsync(Path.Combine(source, "Лист.cnc"), "UP", Token); await File.WriteAllTextAsync(Path.Combine(source, "Карта.pdf"), "Map", Token);
        await NewOrder(f, "Не отправлять эту заявку"); var secondId = f.Model.SelectedOrder!.Order.Id; var window = Window(f);
        try
        {
            window.Show(); await Until(() => !f.Model.IsBusy); var grid = window.FindControl<DataGrid>("WorkflowOrdersGrid")!;
            RightClick(Row(grid, firstId)); Assert.False(Directory.Exists(f.Model.ShopRoot)); Assert.Null(window.Copied);
            Click(Item(grid.ContextMenu!, "WorkflowOrderMenuSend")); await Until(() => window.OwnedWindows.OfType<WorkflowShopTransferWindow>().Any());
            Assert.False(grid.ContextMenu!.IsOpen); var dialog = window.OwnedWindows.OfType<WorkflowShopTransferWindow>().Single();
            var transfer = Assert.IsType<WorkflowShopTransferViewModel>(dialog.DataContext); await Until(() => !transfer.IsBusy);
            var row = Assert.Single(transfer.Projects); Assert.Equal(project.Id, row.Plan.Project.Id);
            Assert.False(Directory.Exists(f.Model.ShopRoot)); Assert.Null(window.Copied);
            foreach (var file in row.Files) file.IsSelected = true; row.Reviewed = true; await transfer.CopyAsync();
            Assert.True(transfer.AllPrepared); Assert.Contains("9337", window.Copied); Assert.True(File.Exists(Path.Combine(row.Destination, "Лист.cnc")));
            var book = await f.Store.ReadAsync(Token); Assert.True(book.Orders.Single(o => o.Id == firstId).HasCurrentShopHandoff);
            Assert.False(book.Orders.Single(o => o.Id == secondId).HasCurrentShopHandoff); dialog.Close();
        }
        finally { foreach (var child in window.OwnedWindows.ToArray()) child.Close(); window.Close(); }
    }

    private static MenuItem Item(ContextMenu menu, string name) => menu.Items.OfType<MenuItem>().Single(i => i.Name == name);
    private static DataGridRow Row(DataGrid grid, Guid id)
    { Pump(); var item = grid.ItemsSource!.Cast<WorkflowOrderRow>().Single(r => r.Order.Id == id); grid.ScrollIntoView(item, null); Pump(); return grid.GetVisualDescendants().OfType<DataGridRow>().Single(r => r.DataContext is WorkflowOrderRow row && row.Order.Id == id); }
    private static void RightClick(Control c) => RightClick(c, new Point(Math.Min(40, c.Bounds.Width / 2), c.Bounds.Height / 2));
    private static void RightClick(Control c, Point p)
    { var root = TopLevel.GetTopLevel(c)!; var point = c.TranslatePoint(p, root)!.Value; root.MouseMove(point); root.MouseDown(point, MouseButton.Right); root.MouseUp(point, MouseButton.Right); Pump(); }
    private static void Click(Control c)
    { var root = TopLevel.GetTopLevel(c)!; var p = c.TranslatePoint(new Point(c.Bounds.Width / 2, c.Bounds.Height / 2), root)!.Value; root.MouseDown(p, MouseButton.Left); root.MouseUp(p, MouseButton.Left); Pump(); }
    private static void Press(TopLevel root, Key key, PhysicalKey physical, RawInputModifiers modifiers = RawInputModifiers.None)
    { root.KeyPress(key, modifiers, physical, null); if (root.IsVisible) root.KeyRelease(key, modifiers, physical, null); Pump(); }
    private static void Pump() => Dispatcher.UIThread.RunJobs();
    private static async Task Until(Func<bool> ready)
    { for (var i = 0; i < 500; i++) { Pump(); if (ready()) return; await Task.Delay(10, Token); } Assert.True(ready()); }
    private static void InWindow(Control c, Window w)
    { var p = c.TranslatePoint(default, w)!.Value; Assert.InRange(p.X, 0, w.ClientSize.Width - c.Bounds.Width + 1); Assert.InRange(p.Y, 0, w.ClientSize.Height - c.Bounds.Height + 1); }
    private static void Frame(Window w, string name)
    { if (Environment.GetEnvironmentVariable("WORKFLOW_ORDER_MENU_SCREENSHOTS") is not { } output) return; Directory.CreateDirectory(output); w.CaptureRenderedFrame()!.Save(Path.Combine(output, name + ".png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions()); }
}

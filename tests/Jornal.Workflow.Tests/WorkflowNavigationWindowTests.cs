using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Jornal.Workflow.Desktop;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class WorkflowNavigationWindowTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static void Pump() => Dispatcher.UIThread.RunJobs();
    private static void Click(Button button)
    {
        Pump(); var root = TopLevel.GetTopLevel(button)!;
        var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), root)!.Value;
        root.MouseDown(point, MouseButton.Left); root.MouseUp(point, MouseButton.Left); Pump();
    }
    private static async Task Until(Func<bool> ready)
    { for (var i = 0; i < 500; i++) { Pump(); if (ready()) return; await Task.Delay(10, Token); } Assert.True(ready()); }
    private static WorkflowWindow Window(ProjectFixture f, bool dark = false, int width = 1180) =>
        new(new(Path.Combine(f.DirectoryPath, "preferences.json")))
        { DataContext = f.Model, Width = width, Height = 768, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
    private static WorkflowCloseWindow Dialog(WorkflowWindow window) => Assert.Single(window.OwnedWindows.OfType<WorkflowCloseWindow>());
    private static void AssertCenteredIcon(Button button)
    {
        var icon = button.GetVisualDescendants().OfType<Avalonia.Svg.Svg>().Single(i => i.IsVisible);
        var center = icon.TranslatePoint(new Point(icon.Bounds.Width / 2, icon.Bounds.Height / 2), button)!.Value;
        Assert.InRange(Math.Abs(center.X - button.Bounds.Width / 2), 0, .5);
        Assert.InRange(Math.Abs(center.Y - button.Bounds.Height / 2), 0, .5);
    }
    private static void Frame(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("WORKFLOW_NAVIGATION_SCREENSHOTS") is not { } folder) return;
        Directory.CreateDirectory(folder); window.CaptureRenderedFrame()!.Save(Path.Combine(folder, name + ".png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }

    [AvaloniaTheory]
    [InlineData(false, 1180)] [InlineData(true, 1180)] [InlineData(false, 1366)] [InlineData(true, 1366)]
    public async Task FilterNoticeCloseCancelAndSaveKeepPaneWidthsAndKeyboardFocus(bool dark, int width)
    {
        using var log = new WorkflowWindowTests.BindingLog(); using var f = new ProjectFixture(); await f.InitializeAsync();
        var window = Window(f, dark, width);
        try
        {
            window.Show(); await Until(() => !f.Model.IsBusy); Pump();
            AssertCenteredIcon(window.FindControl<Button>("CloseWorkflowCard")!);
            var splitter = window.FindControl<GridSplitter>("JournalPaneSplitter")!; var split = splitter.Bounds;
            f.Model.Search = "Не существует такой заявки"; Pump();
            Assert.True(window.FindControl<Border>("WorkflowCardOutsideFilter")!.IsEffectivelyVisible);
            Assert.True(f.Model.IsCardVisible); Assert.Empty(f.Model.Orders); Frame(window, $"outside-{width}-{dark}");
            Click(window.FindControl<Button>("ResetWorkflowOrderFilters")!);
            Assert.True(f.Model.SelectedOrder is not null, $"search={f.Model.Search}; date={f.Model.FilterDate}; rows={f.Model.Orders.Count}; open={f.Model.IsOrderOpen}; error={f.Model.Error}"); Assert.False(f.Model.IsCardOutsideFilter);
            Click(window.FindControl<Button>("CloseWorkflowCard")!); await Until(() => f.Model.IsNoCardSelected);
            Assert.Null(f.Model.SelectedOrder); Assert.Equal(split, splitter.Bounds);
            Assert.True(window.FindControl<TextBox>("WorkflowSearchInput")!.IsFocused);
            f.Model.SelectedOrder = f.Model.Orders.First(); f.Model.Blcb = "777"; Pump();
            Click(window.FindControl<Button>("CloseWorkflowCard")!); var dialog = Dialog(window);
            Assert.True(dialog.FindControl<Button>("SaveWorkflowClose")!.IsEffectivelyVisible);
            Assert.True(dialog.FindControl<Button>("CancelWorkflowClose")!.IsFocused);
            Frame(dialog, $"close-dirty-{width}-{dark}");
            dialog.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null); await Until(() => window.OwnedWindows.Count == 0);
            Assert.True(f.Model.IsCardVisible); Assert.Equal("777", f.Model.Blcb);
            Click(window.FindControl<Button>("CloseWorkflowCard")!); dialog = Dialog(window);
            Click(dialog.FindControl<Button>("SaveWorkflowClose")!); await Until(() => f.Model.IsNoCardSelected);
            Assert.Equal("777", (await f.Store.ReadAsync(Token)).Orders.Single().Blcb);
            Assert.Equal(split, splitter.Bounds); Frame(window, $"closed-{width}-{dark}"); Assert.Empty(log.Errors);
        }
        finally { WorkflowTestWindows.Close(window); }
    }

    [AvaloniaFact]
    public async Task OrdinaryExitProtectsInactiveDraftAndManualTextAndRejectsChangedConfirmation()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var first = f.Model.SelectedOrder!.Order.Id;
        f.Model.Blcb = "Не сохранить"; f.Model.ResultText = "Мой ручной текст";
        f.Model.NewOrderCommand.Execute(null); f.Model.NewManager = "Другой"; f.Model.NewCustomer = "Вторая";
        await f.Model.CreateOrderCommand.ExecuteAsync(null);
        var window = Window(f);
        try
        {
            window.Show(); await Until(() => !f.Model.IsBusy); window.Close(); var dialog = Dialog(window);
            Assert.False(dialog.FindControl<Button>("SaveWorkflowClose")!.IsEffectivelyVisible);
            Click(dialog.FindControl<Button>("CancelWorkflowClose")!); await Until(() => window.OwnedWindows.Count == 0); Assert.True(window.IsVisible);
            window.Close(); dialog = Dialog(window); f.Model.ResultText = "Новый ввод во время подтверждения";
            Click(dialog.FindControl<Button>("DiscardWorkflowClose")!); await Until(() => window.OwnedWindows.Count == 0);
            Assert.True(window.IsVisible); Assert.Contains("изменились", f.Model.Error); Assert.Equal("Новый ввод во время подтверждения", f.Model.ResultText);
            window.Close(); dialog = Dialog(window); Click(dialog.FindControl<Button>("DiscardWorkflowClose")!); await Until(() => !window.IsVisible);
            Assert.Null((await f.Store.ReadAsync(Token)).Orders.Single(o => o.Id == first).Blcb);
        }
        finally { WorkflowTestWindows.Close(window); }
    }

    [AvaloniaFact]
    public async Task NewOrderCancelUsesConfirmationAndNeverCreatesAnOrder()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var window = Window(f);
        try
        {
            window.Show(); await Until(() => !f.Model.IsBusy); f.Model.NewOrderCommand.Execute(null); f.Model.NewCustomer = "Новая"; Pump();
            AssertCenteredIcon(window.FindControl<Button>("CloseNewWorkflowCard")!);
            Click(window.FindControl<Button>("CloseNewWorkflowCard")!); var dialog = Dialog(window);
            Assert.False(dialog.FindControl<Button>("SaveWorkflowClose")!.IsEffectivelyVisible);
            Click(dialog.FindControl<Button>("CancelWorkflowClose")!); await Until(() => window.OwnedWindows.Count == 0);
            Assert.True(f.Model.IsCreating); Assert.Equal("Новая", f.Model.NewCustomer);
            Click(window.FindControl<Button>("CancelNewWorkflowCard")!); dialog = Dialog(window);
            Click(dialog.FindControl<Button>("DiscardWorkflowClose")!); await Until(() => !f.Model.IsCreating);
            Assert.True(f.Model.IsCardVisible); Assert.Single((await f.Store.ReadAsync(Token)).Orders);
        }
        finally { WorkflowTestWindows.Close(window); }
    }

    [AvaloniaTheory]
    [InlineData(false)] [InlineData(true)]
    public async Task ClipboardOperationBlocksCardAndApplicationCloseUntilCompletion(bool shopText)
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var p = await f.AddAsync();
        await ProjectFixture.WriteAsync(p); await f.AcceptAsync(); f.Model.Blcb = "55"; await f.Model.SaveHeaderCommand.ExecuteAsync(null);
        var window = Window(f); var release = new TaskCompletionSource();
        Task? copying = null;
        try
        {
            window.Show(); await Until(() => !f.Model.IsBusy); f.Model.ResultText = "Ручной результат";
            copying = shopText ? f.Model.CopyCuttingTaskAsync(_ => release.Task) : f.Model.CopyResultAsync(new Clipboard(async () => await release.Task)); Pump();
            Assert.False(window.FindControl<Button>("CloseWorkflowCard")!.IsEffectivelyEnabled);
            window.Close(); Assert.True(window.IsVisible); Assert.Empty(window.OwnedWindows);
            release.SetResult(); await copying; Assert.True(f.Model.CanCloseCard);
        }
        finally { release.TrySetResult(); if (copying is not null) await copying; WorkflowTestWindows.Close(window); }
    }

    [AvaloniaFact]
    public async Task SaveErrorKeepsTheWindowAndInputOpenAfterConfirmation()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var window = Window(f);
        try
        {
            window.Show(); await Until(() => !f.Model.IsBusy); f.Model.Blcb = "Не потерять";
            Click(window.FindControl<Button>("CloseWorkflowCard")!); var dialog = Dialog(window);
            using (File.Open(f.Store.FilePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                Click(dialog.FindControl<Button>("SaveWorkflowClose")!);
                await Until(() => window.OwnedWindows.Count == 0 && !f.Model.IsBusy);
            }
            Assert.True(window.IsVisible); Assert.True(f.Model.IsCardVisible); Assert.Equal("Не потерять", f.Model.Blcb);
            Assert.NotNull(f.Model.Error); Assert.Null((await f.Store.ReadAsync(Token)).Orders.Single().Blcb);
        }
        finally { WorkflowTestWindows.Close(window); }
    }

    private sealed class Clipboard(Func<Task> write) : Jornal.Desktop.Clipboard.IResultClipboard
    { public Task WriteAsync(Jornal.Core.Results.ResultClipboardContent content) => write(); }
}

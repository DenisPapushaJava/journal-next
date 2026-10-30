using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Jornal.Workflow.Core;
using Jornal.Workflow.Desktop;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class WorkflowAutoRefreshWindowTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    [AvaloniaTheory]
    [InlineData(false, 1180)]
    [InlineData(true, 1180)]
    [InlineData(false, 1366)]
    [InlineData(true, 1366)]
    public async Task BackgroundInsertionPreservesVisibleAnchorFocusInputAndPopupDefersReading(bool dark, int width)
    {
        using var f = await WorkflowAutoRefreshTests.Fixture.Create();
        var template = f.Memory.Book.Orders.Single(); var at = DateTimeOffset.Now;
        var orders = Enumerable.Range(1, 80).Select(i => template with { Id = Guid.NewGuid(), Number = i, ReferenceNumber = at.AddSeconds(-i).ToString("yyyy-MM-dd-HHmmss"), CreatedAt = at.AddSeconds(-i), UpdatedAt = at.AddSeconds(-i) }).ToArray();
        f.Memory.Book = f.Memory.Book with { Version = f.Memory.Book.Version + 1, Orders = orders };
        f.Preferences.Update(p => p with { DarkTheme = dark }); var clock = new JournalClock();
        var window = new WorkflowWindow(f.Preferences, journalRefreshClock: clock, journalSubscribe: (_, _, _) => Task.FromResult<IDisposable>(new JournalSubscription()))
        { DataContext = f.Model, Width = width, Height = 768 };
        try
        {
            window.Show(); await Until(() => !f.Model.IsBusy); Pump();
            var grid = window.FindControl<DataGrid>("WorkflowOrdersGrid")!;
            var anchor = f.Model.Orders[35]; grid.ScrollIntoView(anchor, null); Pump();
            var before = FirstVisible(grid); Assert.NotNull(before);
            window.FindControl<Expander>("OrderHeaderExpander")!.IsExpanded = true; Pump();
            var selected = f.Model.SelectedOrder; var input = window.FindControl<TextBox>("WorkflowBlcbInput")!;
            input.Focus(); input.Text = "мой ввод"; input.CaretIndex = 2; Pump(); Assert.True(input.IsFocused);
            var addition = template with { Id = Guid.NewGuid(), Number = 81, ReferenceNumber = at.AddSeconds(1).ToString("yyyy-MM-dd-HHmmss"), CreatedAt = at.AddSeconds(1), UpdatedAt = at.AddSeconds(1) };
            f.Memory.Book = f.Memory.Book with { Version = f.Memory.Book.Version + 1, Orders = orders.Prepend(addition).ToArray() };
            await window.TickJournalRefreshAsync(); clock.Advance(15); await window.TickJournalRefreshAsync(); Pump();
            Assert.Equal(81, f.Model.Orders.Count); Assert.Same(selected, f.Model.SelectedOrder); Assert.Equal("мой ввод", input.Text); Assert.Equal(2, input.CaretIndex); Assert.True(input.IsFocused);
            var after = FirstVisible(grid); Assert.Equal(before!.Value.Id, after!.Value.Id); Assert.InRange(Math.Abs(before.Value.Y - after.Value.Y), 0, 2);
            var actions = window.FindControl<Button>("WorkflowOrderActions")!; actions.Flyout!.ShowAt(actions); Pump();
            var count = f.Memory.Requests.Count; clock.Advance(15); await window.TickJournalRefreshAsync(); Assert.Equal(count, f.Memory.Requests.Count);
            actions.Flyout.Hide(); Pump(); clock.Advance(15); await window.TickJournalRefreshAsync();
            input.ContextFlyout = new Flyout { Content = new TextBlock { Text = "Копировать" } };
            input.ContextFlyout.ShowAt(input); Pump();
            count = f.Memory.Requests.Count; clock.Advance(15); await window.TickJournalRefreshAsync();
            Assert.Equal(count, f.Memory.Requests.Count); input.ContextFlyout.Hide(); Pump();
            count = f.Memory.Requests.Count;
            window.MouseDown(new Point(30, 20), MouseButton.Left); clock.Advance(15); await window.TickJournalRefreshAsync();
            Assert.Equal(count, f.Memory.Requests.Count); // A press on passive text need not capture the pointer.
            window.MouseUp(new Point(30, 20), MouseButton.Left); await window.TickJournalRefreshAsync();
            Assert.True(f.Memory.Requests.Count > count);
            var settings = f.Model.CreateGeneralSettings(); var dialog = new WorkflowSettingsWindow { DataContext = settings, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light, Width = width, Height = 768 };
            dialog.Show(); Pump();
            var check = dialog.FindControl<CheckBox>("WorkflowAutomaticJournalRefresh")!; Assert.True(check.IsChecked); check.IsChecked = false; Pump(); Assert.False(f.Preferences.Load().AutomaticJournalRefresh);
            Assert.True(check.Bounds.Width > 100); Save(dialog, $"auto-settings-{width}-{dark}"); dialog.Close();
            Save(window, $"auto-insert-{width}-{dark}");
        }
        finally { foreach (var child in window.OwnedWindows.ToArray()) child.Close(); window.Close(); }
    }
    private static (Guid Id, double Y)? FirstVisible(DataGrid grid) => grid.GetVisualDescendants().OfType<DataGridRow>()
        .Select(r => (Row: r, Y: r.TranslatePoint(default, grid)?.Y ?? double.MinValue))
        .Where(p => p.Row.DataContext is WorkflowOrderRow && p.Y + p.Row.Bounds.Height > 32)
        .OrderBy(p => p.Y).Select(p => ((Guid Id, double Y)?)(((WorkflowOrderRow)p.Row.DataContext!).Order.Id, p.Y)).FirstOrDefault();
    private static void Pump() => Dispatcher.UIThread.RunJobs();
    private static async Task Until(Func<bool> done)
    { for (var i = 0; i < 500 && !done(); i++) { await Task.Delay(10, Token); Pump(); } Pump(); Assert.True(done()); }
    private static void Save(Window window, string name)
    { if (Environment.GetEnvironmentVariable("JOURNAL_WORKFLOW_SCREENSHOTS") is not { } output) return; Directory.CreateDirectory(output); window.CaptureRenderedFrame()!.Save(Path.Combine(output, name + ".png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions()); }
}

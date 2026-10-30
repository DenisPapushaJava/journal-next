using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Jornal.Core.Results;
using Jornal.Desktop.Clipboard;
using Jornal.Desktop.ViewModels;
using Jornal.Workflow.Desktop;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class WorkflowResultWorkspaceTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static async Task Ready(ProjectFixture f)
    { await f.InitializeAsync(); var p = await f.AddAsync(); await ProjectFixture.WriteAsync(p); await f.AcceptAsync(); }
    private sealed class RecordingWindow(WorkflowPreferencesStore prefs) : WorkflowWindow(prefs)
    {
        internal ResultClipboardContent? Copied;
        internal bool Fail;
        protected override Task WriteResultClipboardAsync(ResultClipboardContent content)
        { if (Fail) throw new IOException("test"); Copied = content; return Task.CompletedTask; }
    }
    private sealed class Clipboard(Func<ResultClipboardContent, Task> write) : IResultClipboard
    { public Task WriteAsync(ResultClipboardContent content) => write(content); }

    [AvaloniaTheory]
    [InlineData(false, 1180)] [InlineData(true, 1180)] [InlineData(false, 1366)] [InlineData(true, 1366)]
    public async Task WorkspaceFitsKeepsManualTextAndCopiesChosenStyle(bool dark, int width)
    {
        using var log = new WorkflowWindowTests.BindingLog(); using var f = new ProjectFixture(); await Ready(f);
        var prefs = new WorkflowPreferencesStore(Path.Combine(f.DirectoryPath, "preferences.json"));
        var window = new RecordingWindow(prefs) { DataContext = f.Model, Width = width, Height = 768, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
        try
        {
            window.Show(); await Until(() => !f.Model.IsBusy); f.Model.OrderTabIndex = 0; Pump();
            var before = JsonSerializer.Serialize(await f.Store.ReadAsync(Token));
            Click(window.FindControl<Button>("OpenWorkflowResult")!); Assert.Equal(1, f.Model.OrderTabIndex); Assert.NotEmpty(f.Model.ResultText);
            var editor = window.FindControl<TextBox>("WorkflowResultEditor")!; var toolbar = window.FindControl<Grid>("WorkflowResultToolbar")!;
            Assert.False(editor.IsReadOnly); Assert.Equal(10m * 96 / 72, (decimal)editor.FontSize, 8);
            var paper = window.FindControl<Border>("WorkflowResultPaper")!;
            window.FindControl<TextBox>("WorkflowSearchInput")!.Focus(); window.MouseMove(new Point(5, 5)); Pump();
            PaperState(editor, paper, "#F1F2F4", f.Model.ResultTextColor); Frame(window, $"result-idle-{width}-{dark}");
            window.MouseMove(editor.TranslatePoint(new Point(30, 30), window)!.Value); Pump(); Assert.True(editor.IsPointerOver);
            PaperState(editor, paper, "#F1F2F4", f.Model.ResultTextColor); Frame(window, $"result-hover-{width}-{dark}");
            editor.Focus(); Pump(); Assert.True(editor.IsFocused);
            PaperState(editor, paper, "#F1F2F4", f.Model.ResultTextColor);
            Assert.Equal(Color.Parse("#8297B0"), Assert.IsAssignableFrom<ISolidColorBrush>(paper.BorderBrush).Color);
            Frame(window, $"result-focus-{width}-{dark}");
            Assert.False(window.FindControl<Button>("OpenWorkflowResult")!.IsEffectivelyVisible);
            Assert.False(window.FindControl<Button>("CopyWorkflowResultCompact")!.IsEffectivelyVisible);
            foreach (var name in new[] { "GenerateWorkflowResult", "CopyWorkflowResult", "ClearWorkflowResult", "WorkflowResultFontSize", "WorkflowResultColor" })
            { var c = window.FindControl<Control>(name)!; var b = Bounds(c, window); Assert.True(b.Left >= Bounds(toolbar, window).Left && b.Right <= Bounds(toolbar, window).Right); }
            var geometry = Geometry(window);
            window.FindControl<ComboBox>("WorkflowResultFontSize")!.SelectedItem = 14m; Pump();
            var color = window.FindControl<Button>("WorkflowResultColor")!; Click(color); Assert.True(color.Flyout!.IsOpen);
            Frame(window, $"result-palette-{width}-{dark}");
            var content = Assert.IsType<Flyout>(color.Flyout).Content as Control;
            var blue = content!.GetVisualDescendants().OfType<Button>().Single(b => b.DataContext is ResultColorOption { Hex: "#2F5496" });
            Click(blue); Assert.False(color.Flyout.IsOpen); Assert.Equal("#2F5496", f.Model.ResultTextColor);
            Assert.Equal(new ResultTextStyle { FontSizePoints = 14, ColorHex = "#2F5496" }, prefs.Load().ResultFormatting);
            editor.Focus(); editor.SelectAll(); TopLevel.GetTopLevel(editor)!.KeyTextInput("Мой исправленный текст\n"); Pump();
            Assert.Equal("Мой исправленный текст\n", f.Model.ResultText);
            Click(window.FindControl<Button>("CopyWorkflowResult")!); await Until(() => window.Copied is not null);
            Assert.Equal(f.Model.ResultText, window.Copied!.Text); Assert.Contains("font-size:14pt", window.Copied.Html); Assert.Contains("color:#2F5496", window.Copied.Html); Assert.Contains(@"\fs28", window.Copied.Rtf);
            Assert.Equal(geometry, Geometry(window));
            window.Fail = true; Click(window.FindControl<Button>("CopyWorkflowResult")!); await Until(() => f.Model.HasResultStatusError);
            Assert.Null(f.Model.Error); Assert.Equal("Мой исправленный текст\n", f.Model.ResultText); Assert.Equal(geometry, Geometry(window)); window.Fail = false;
            Frame(window, $"result-ready-{width}-{dark}");
            Click(window.FindControl<Button>("ClearWorkflowResult")!); Assert.Empty(f.Model.ResultText); Assert.True(f.Model.CanUndoClearResult);
            Assert.False(f.Model.CanCopyResult); Assert.Equal(geometry, Geometry(window)); Frame(window, $"result-cleared-{width}-{dark}");
            Click(window.FindControl<Button>("UndoWorkflowResultClear")!); Assert.Equal("Мой исправленный текст\n", f.Model.ResultText); Assert.Equal(geometry, Geometry(window));
            Click(window.FindControl<Button>("GenerateWorkflowResult")!); await Until(() => window.OwnedWindows.OfType<WorkflowResultReplaceWindow>().Any());
            var dialog = window.OwnedWindows.OfType<WorkflowResultReplaceWindow>().Single(); Frame(dialog, $"result-confirm-{width}-{dark}");
            dialog.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null); await Until(() => !dialog.IsVisible); Assert.Equal("Мой исправленный текст\n", f.Model.ResultText);
            Click(window.FindControl<Button>("GenerateWorkflowResult")!); await Until(() => window.OwnedWindows.OfType<WorkflowResultReplaceWindow>().Any());
            dialog = window.OwnedWindows.OfType<WorkflowResultReplaceWindow>().Single();
            f.Model.ResultText = "Изменено во время подтверждения"; Click(dialog.FindControl<Button>("ReplaceWorkflowResult")!); await Until(() => !dialog.IsVisible);
            Assert.Equal("Изменено во время подтверждения", f.Model.ResultText); Assert.True(f.Model.HasResultStatusError);
            Click(window.FindControl<Button>("GenerateWorkflowResult")!); await Until(() => window.OwnedWindows.OfType<WorkflowResultReplaceWindow>().Any());
            dialog = window.OwnedWindows.OfType<WorkflowResultReplaceWindow>().Single(); Click(dialog.FindControl<Button>("ReplaceWorkflowResult")!); await Until(() => !dialog.IsVisible);
            Assert.Contains("Резка:", f.Model.ResultText); Assert.False(f.Model.HasResultStatusError);
            f.Model.OrderTabIndex = 0; Pump(); var current = f.Model.ResultText; Click(window.FindControl<Button>("OpenWorkflowResult")!); Assert.Equal(current, f.Model.ResultText);
            var menu = editor.ContextFlyout!; menu.ShowAt(editor); Pump(); Assert.True(menu.IsOpen);
            window.FindControl<MenuItem>("ResultMenuSelectAll")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Equal(editor.Text, editor.SelectedText); menu.Hide();
            Assert.Equal(before, JsonSerializer.Serialize(await f.Store.ReadAsync(Token))); Assert.Empty(log.Errors);
        }
        finally { foreach (var child in window.OwnedWindows.ToArray()) child.Close(); window.Close(); }
    }

    [Fact]
    public async Task FormattingIsLocalNormalizesOldPreferencesAndDoesNotChangeTextOrBusinessData()
    {
        using var f = new ProjectFixture(); await Ready(f); f.Model.ResultText = "Ручной текст";
        var before = JsonSerializer.Serialize(await f.Store.ReadAsync(Token));
        var prefs = new WorkflowPreferencesStore(Path.Combine(f.DirectoryPath, "preferences.json"));
        f.Model.ResultFontSizePoints = 12; f.Model.ResultTextColor = "#ffffff";
        Assert.Equal("#FFFFFF", f.Model.ResultTextColor); Assert.Equal(Color.Parse("#202124"), Assert.IsAssignableFrom<ISolidColorBrush>(f.Model.ResultPaperBrush).Color);
        var restarted = new WorkflowViewModel(f.Store, new AppUpdatesViewModel(new WorkflowAppUpdater()));
        try { Assert.Equal(f.Model.CaptureResultStyle(), restarted.CaptureResultStyle()); } finally { restarted.Detach(); }
        using (File.Open(Path.Combine(f.DirectoryPath, "preferences.json.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            f.Model.ResultTextColor = "#2F5496";
        Assert.True(f.Model.HasResultStatusError); Assert.Equal("#FFFFFF", f.Model.ResultTextColor);
        Assert.Equal("Ручной текст", f.Model.ResultText); Assert.Equal(before, JsonSerializer.Serialize(await f.Store.ReadAsync(Token)));
        prefs.Update(p => p with { ResultFormatting = new() { FontSizePoints = 1000, ColorHex = "broken" } });
        Assert.Equal(new ResultTextStyle(), prefs.Load().ResultFormatting);
    }

    [AvaloniaFact]
    public async Task EditorUndoDoesNotRestoreTextFromAnotherOrder()
    {
        using var f = new ProjectFixture(); await Ready(f);
        var window = new RecordingWindow(new WorkflowPreferencesStore(Path.Combine(f.DirectoryPath, "preferences.json"))) { DataContext = f.Model };
        try
        {
            window.Show(); await Until(() => !f.Model.IsBusy); f.Model.OpenResultCommand.Execute(null); Pump();
            var editor = window.FindControl<TextBox>("WorkflowResultEditor")!;
            editor.Focus(); editor.SelectAll(); window.KeyTextInput("Ручной текст первой заявки"); Pump();
            Assert.Equal("Ручной текст первой заявки", f.Model.ResultText);
            var original = f.Model.SelectedOrder!.Order.Id;
            f.Model.NewOrderCommand.Execute(null); f.Model.NewManager = "Другой"; f.Model.NewCustomer = "Вторая заявка";
            await f.Model.CreateOrderCommand.ExecuteAsync(null); f.Model.OrderTabIndex = 1; Pump();
            Assert.Empty(f.Model.ResultText); editor.Undo(); Pump(); Assert.Empty(f.Model.ResultText);
            f.Model.SelectedOrder = f.Model.Orders.Single(o => o.Order.Id == original); Pump();
            Assert.Equal("Ручной текст первой заявки", f.Model.ResultText);
        }
        finally { window.Close(); }
    }

    [Fact]
    public async Task CopyUsesSnapshotBlocksDuplicatesAndDoesNotReportLateSuccessOnChangedText()
    {
        using var f = new ProjectFixture(); await Ready(f); f.Model.ResultText = "Исходный текст";
        var started = new TaskCompletionSource(); var finish = new TaskCompletionSource(); ResultClipboardContent? copied = null;
        var action = f.Model.CopyResultAsync(new Clipboard(c => { copied = c; started.SetResult(); return finish.Task; })); await started.Task;
        Assert.False(f.Model.CanEditResult); Assert.False(f.Model.CanCopyCuttingTask); var duplicate = false;
        await f.Model.CopyResultAsync(new Clipboard(_ => { duplicate = true; return Task.CompletedTask; })); Assert.False(duplicate);
        f.Model.ResultText = "Новый текст"; finish.SetResult(); await action;
        Assert.Equal("Исходный текст", copied!.Text); Assert.Equal("Новый текст", f.Model.ResultText); Assert.DoesNotContain("Скопировано", f.Model.ResultStatus); Assert.True(f.Model.CanEditResult);
    }

    [Fact]
    public async Task StyleWritesPreserveOtherPreferencesAndRejectACompetingStyle()
    {
        using var f = new ProjectFixture(); await Ready(f);
        var prefs = new WorkflowPreferencesStore(Path.Combine(f.DirectoryPath, "preferences.json"));
        var other = new WorkflowViewModel(f.Store, new AppUpdatesViewModel(new WorkflowAppUpdater()), preferences: prefs);
        try
        {
            await other.ReloadCommand.ExecuteAsync(null); other.OpenOrderCommand.Execute(null);
            prefs.Update(p => p with { DarkTheme = true, ShopHandoffColorArgb = 0xFF123456 });
            f.Model.ResultFontSizePoints = 12;
            Assert.True(prefs.Load().DarkTheme); Assert.Equal(0xFF123456u, prefs.Load().ShopHandoffColorArgb);
            other.ResultTextColor = "#2F5496"; Assert.True(other.HasResultStatusError);
            Assert.Equal(new ResultTextStyle { FontSizePoints = 12 }, prefs.Load().ResultFormatting);
            other.LoadLocalPreferences(prefs); other.ResultTextColor = "#2F5496";
            Assert.False(other.HasResultStatusError); Assert.Equal(new ResultTextStyle { FontSizePoints = 12, ColorHex = "#2F5496" }, prefs.Load().ResultFormatting);
        }
        finally { other.Detach(); }
    }

    [Fact]
    public async Task GeneratedBaselineSurvivesReloadAndCardNavigationWithoutUnnecessaryConfirmation()
    {
        using var f = new ProjectFixture(); await Ready(f); var original = f.Model.SelectedOrder!.Order.Id;
        f.Model.GenerateResultCommand.Execute(null); var text = f.Model.ResultText;
        await f.Model.ReloadCommand.ExecuteAsync(null); Assert.False(f.Model.PrepareResultGeneration()!.NeedsConfirmation);
        f.Model.NewOrderCommand.Execute(null); f.Model.NewManager = "Другой"; f.Model.NewCustomer = "Другой заказчик"; await f.Model.CreateOrderCommand.ExecuteAsync(null);
        f.Model.SelectedOrder = f.Model.Orders.Single(o => o.Order.Id == original);
        Assert.Equal(text, f.Model.ResultText); Assert.False(f.Model.PrepareResultGeneration()!.NeedsConfirmation);
    }

    [Fact]
    public async Task ClearUndoAndPreparedReplacementNeverOverwriteAnotherCardOrNewInput()
    {
        using var f = new ProjectFixture(); await Ready(f); f.Model.ResultText = "Сохранить правки";
        f.Model.GenerateResultCommand.Execute(null); Assert.Equal("Сохранить правки", f.Model.ResultText);
        f.Model.ClearResultCommand.Execute(null); Assert.True(f.Model.CanUndoClearResult); f.Model.ResultText = "Ввод после очистки";
        f.Model.UndoClearResultCommand.Execute(null); Assert.Equal("Ввод после очистки", f.Model.ResultText);
        var prepared = f.Model.PrepareResultGeneration()!;
        f.Model.NewOrderCommand.Execute(null); f.Model.NewManager = "Другой"; f.Model.NewCustomer = "Другой"; await f.Model.CreateOrderCommand.ExecuteAsync(null);
        Assert.False(f.Model.ApplyResultGeneration(prepared)); Assert.Empty(f.Model.ResultText); Assert.False(f.Model.CanUndoClearResult);
    }

    private static void Pump() => Dispatcher.UIThread.RunJobs();
    private static void PaperState(TextBox editor, Border paper, string background, string text)
    {
        Assert.Equal(Color.Parse(background), Assert.IsAssignableFrom<ISolidColorBrush>(paper.Background).Color);
        var inner = editor.GetVisualDescendants().OfType<Border>().First(b => b.Name == "PART_BorderElement");
        Assert.Equal((byte)0, Assert.IsAssignableFrom<ISolidColorBrush>(inner.Background).Color.A);
        Assert.Equal(new Thickness(0), inner.BorderThickness);
        Assert.Equal(Color.Parse(text), Assert.IsAssignableFrom<ISolidColorBrush>(editor.Foreground).Color);
    }
    private static async Task Until(Func<bool> ready)
    { for (var i = 0; i < 500; i++) { Pump(); if (ready()) return; await Task.Delay(10, Token); } Assert.True(ready()); }
    private static Rect Bounds(Control c, Window w) => new(c.TranslatePoint(default, w)!.Value, c.Bounds.Size);
    private static Rect[] Geometry(Window w) => new[] { "WorkflowResultEditor", "WorkflowResultWorkspace", "WorkflowOrderTotal" }.Select(n => Bounds(w.FindControl<Control>(n)!, w)).ToArray();
    private static void Click(Control c)
    { Pump(); var root = TopLevel.GetTopLevel(c)!; var p = c.TranslatePoint(new Point(c.Bounds.Width / 2, c.Bounds.Height / 2), root)!.Value; root.MouseDown(p, MouseButton.Left); root.MouseUp(p, MouseButton.Left); Pump(); }
    private static void Frame(Window w, string name)
    {
        if (Environment.GetEnvironmentVariable("WORKFLOW_RESULT_SCREENSHOTS") is not { } output) return;
        Directory.CreateDirectory(output); w.CaptureRenderedFrame()!.Save(Path.Combine(output, name + ".png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }
}

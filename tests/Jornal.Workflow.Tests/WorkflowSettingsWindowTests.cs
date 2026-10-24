using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Logging;
using Avalonia.Media;
using Avalonia.VisualTree;
using Jornal.Desktop.Controls;
using Avalonia.Threading;
using Jornal.Workflow.Desktop;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class WorkflowSettingsWindowTests
{
    [AvaloniaTheory]
    [InlineData(false, 1366)] [InlineData(true, 1366)]
    [InlineData(false, 1180)] [InlineData(true, 1180)]
    public async Task ToolbarOpensIndependentCatalogAndSettingsWithoutLosingTheActiveDraft(bool dark, int width)
    {
        using var log = new BindingLog(); using var f = new ProjectFixture(); await f.InitializeAsync();
        var project = await f.AddAsync(); await ProjectFixture.WriteAsync(project); await f.AcceptAsync();
        var preferences = new WorkflowPreferencesStore(Path.Combine(f.DirectoryPath, "preferences.json")); preferences.Save(new(dark) { EmployeeId = Jornal.Workflow.Core.WorkflowWorkspace.Empty.Employees[1].Id });
        var window = new WorkflowWindow(preferences) { DataContext = f.Model, Width = width, Height = 768 };
        try
        {
            window.Show(); while (f.Model.IsBusy) await Task.Delay(10, TestContext.Current.CancellationToken);
            Dispatcher.UIThread.RunJobs();
            var draft = f.Model.SelectedDraft!; draft.Coefficient = "1,3"; draft.Comment = "Открытый черновик";
            var quote = draft.Quote;
            InWindow(window.FindControl<Button>("WorkflowCatalog")!, window);
            InWindow(window.FindControl<Button>("WorkflowSettings")!, window);
            InWindow(window.FindControl<Button>("UpdatesBell")!, window);
            var clock = window.FindControl<HeaderClock>("WorkflowHeaderClock")!;
            InWindow(clock, window);
            Assert.InRange(Math.Abs(clock.TranslatePoint(default, window)!.Value.X + clock.Bounds.Width / 2 - window.ClientSize.Width / 2), 0, 1);
            var badge = window.FindControl<Border>("WorkflowProfileBadge")!; InWindow(badge, window);
            Assert.Empty(badge.GetVisualDescendants().OfType<ComboBox>());
            Click(window, "WorkflowCatalog");
            var catalog = Assert.IsType<WorkflowCatalogWindow>(Assert.Single(window.OwnedWindows));
            var editor = Assert.IsType<WorkflowCatalogViewModel>(catalog.DataContext);
            Assert.Equal("Станки и цены", catalog.Title); Assert.Null(catalog.FindControl<TextBox>("WorkflowProjectRoot"));
            Assert.NotNull(catalog.FindControl<Button>("ImportLegacyWorkflowPrices"));
            editor.SelectedMachine!.Name = "Лазер · цех 1";
            await editor.SaveCommand.ExecuteAsync(null); Assert.Null(editor.Error);
            Assert.Same(draft, f.Model.SelectedDraft); Assert.Equal(quote, draft.Quote);
            Dispatcher.UIThread.RunJobs(); SaveFrame(catalog, dark, width, "workflow-catalog");
            Click(catalog, "ImportLegacyWorkflowPrices");
            var import = Assert.IsType<WorkflowDatabaseWindow>(Assert.Single(catalog.OwnedWindows));
            Assert.Equal("Импорт цен из прежней БД", import.Title);
            import.Close(); Dispatcher.UIThread.RunJobs();
            catalog.Close(); Dispatcher.UIThread.RunJobs();
            Click(window, "WorkflowSettings");
            var settings = Assert.IsType<WorkflowSettingsWindow>(Assert.Single(window.OwnedWindows));
            if (width == 1180) { settings.Width = 800; settings.Height = 580; Dispatcher.UIThread.RunJobs(); }
            var model = Assert.IsType<WorkflowSettingsViewModel>(settings.DataContext);
            Assert.Null(settings.FindControl<ListBox>("WorkflowMachinesList"));
            InWindow(settings.FindControl<Button>("SaveWorkflowSettings")!, settings);
            Assert.Equal(0, model.SelectedSectionIndex);
            InWindow(settings.FindControl<ListBox>("WorkflowSettingsNavigation")!, settings);
            var changeColor = settings.FindControl<Button>("ChangeWorkflowProfileColor")!;
            InWindow(changeColor, settings);
            Assert.False(changeColor.Flyout!.IsOpen);
            var navigation = settings.FindControl<ListBox>("WorkflowSettingsNavigation")!;
            Assert.True(navigation.TranslatePoint(default, settings)!.Value.X + navigation.Bounds.Width <
                settings.FindControl<ScrollViewer>("WorkflowProfilePage")!.TranslatePoint(default, settings)!.Value.X);
            model.ProfileName = "Кузнецов";
            var preset = model.FrequentColors[1];
            var initialColor = model.ProfileColorArgb;
            Click(changeColor); Assert.True(changeColor.Flyout!.IsOpen);
            var palette = settings.FindControl<StackPanel>("WorkflowProfilePalette")!;
            Assert.True(palette.IsVisible); Assert.False(settings.FindControl<StackPanel>("WorkflowPreciseColorMenu")!.IsVisible);
            var swatch = palette.GetVisualDescendants().OfType<Button>().Single(b => ReferenceEquals(b.DataContext, preset));
            Click(swatch); Assert.False(changeColor.Flyout.IsOpen); Assert.True(changeColor.IsFocused);
            Assert.Equal(0xFF42C1FFu, model.ProfileColorArgb); Assert.True(preset.IsSelected);
            Assert.Equal("Голубой", model.ProfileColorName);
            Assert.Equal(0xFF42C1FFu, Assert.IsType<SolidColorBrush>(settings.FindControl<Border>("WorkflowCurrentProfileColor")!.Background).Color.ToUInt32());
            Assert.Equal(initialColor, f.Model.CreateGeneralSettings().ProfileColorArgb);
            Click(changeColor); Assert.True(changeColor.Flyout.IsOpen);
            SaveFrame(TopLevel.GetTopLevel(palette)!, dark, width, "workflow-color-palette");
            Click(settings.FindControl<Button>("MoreWorkflowProfileColors")!);
            var wheel = settings.FindControl<ColorWheel>("WorkflowProfileColor")!;
            Assert.True(wheel.IsEffectivelyVisible); Assert.True(wheel.IsFocused);
            SaveFrame(TopLevel.GetTopLevel(wheel)!, dark, width, "workflow-color-precise");
            Click(settings.FindControl<Button>("BackToWorkflowProfilePalette")!);
            Assert.True(palette.IsVisible); Assert.True(preset.IsSelected);
            Click(settings.FindControl<Button>("MoreWorkflowProfileColors")!);
            var popupRoot = TopLevel.GetTopLevel(wheel)!;
            popupRoot.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null); Dispatcher.UIThread.RunJobs();
            Assert.False(changeColor.Flyout.IsOpen); Assert.True(settings.IsVisible); Assert.True(changeColor.IsFocused);
            Assert.Equal(0xFF42C1FFu, model.ProfileColorArgb);
            await model.SaveCommand.ExecuteAsync(null); Assert.Null(model.Error);
            Assert.Equal("Кузнецов", f.Model.ProfileName); Assert.Same(draft, f.Model.SelectedDraft);
            Dispatcher.UIThread.RunJobs(); SaveFrame(settings, dark, width, "workflow-settings-profile");
            var savedProfile = preferences.Load().EmployeeId; Assert.NotNull(savedProfile);
            window.FindControl<ToggleButton>("ThemeSwitch")!.IsChecked = !dark;
            Assert.Equal(savedProfile, preferences.Load().EmployeeId);
            window.FindControl<ToggleButton>("ThemeSwitch")!.IsChecked = dark;
            model.SelectedSectionIndex = 1; Dispatcher.UIThread.RunJobs();
            var root = Path.Combine(f.DirectoryPath, "new-projects"); model.ProjectRoot = root;
            await model.SaveCommand.ExecuteAsync(null); Assert.Null(model.Error); Assert.False(model.HasChanges);
            Assert.Same(draft, f.Model.SelectedDraft); Assert.Equal(quote, draft.Quote);
            Assert.Equal("Открытый черновик", draft.Comment); Assert.True(f.Model.HasPendingEdits);
            Dispatcher.UIThread.RunJobs(); SaveFrame(settings, dark, width, "workflow-settings-folders");
            Assert.Equal(f.Store.FilePath, settings.FindControl<TextBox>("WorkflowWorkspacePath")!.Text);
            Assert.True(settings.FindControl<TextBox>("WorkflowWorkspacePath")!.IsReadOnly);
            Assert.Equal("Хранилище", model.SectionTitle);
            settings.FindControl<ListBox>("WorkflowSettingsNavigation")!.SelectedIndex = 2; Dispatcher.UIThread.RunJobs();
            Assert.Equal("Программа", model.SectionTitle); Assert.Same(f.Model.Updates, model.Updates);
            Assert.False(settings.FindControl<Button>("SaveWorkflowSettings")!.IsVisible);
            SaveFrame(settings, dark, width, "workflow-settings-program");
            model.SelectedSectionIndex = 0; Dispatcher.UIThread.RunJobs(); Click(changeColor);
            Assert.True(changeColor.Flyout!.IsOpen);
            settings.Close(); Dispatcher.UIThread.RunJobs(); Assert.False(changeColor.Flyout.IsOpen);
            Assert.Equal(root, (await f.Store.ReadAsync(TestContext.Current.CancellationToken)).ProjectRoot);
            Assert.True(File.Exists(project.NativePath));
            SaveFrame(window, dark, width, "workflow-toolbar");
            Assert.True(log.Errors.Count == 0, string.Join("\n", log.Errors.Distinct().Take(12)));
        }
        finally
        {
            foreach (var child in window.OwnedWindows.ToArray()) child.Close();
            window.Close();
        }
    }
    private static void Click(Window window, string name)
    {
        Click(window.FindControl<Control>(name)!);
    }
    private static void Click(Control control)
    {
        var root = TopLevel.GetTopLevel(control)!;
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), root)!.Value;
        root.MouseDown(point, MouseButton.Left); root.MouseUp(point, MouseButton.Left); Dispatcher.UIThread.RunJobs();
    }
    private static void InWindow(Control control, Window window)
    {
        var point = control.TranslatePoint(default, window)!.Value;
        Assert.True(control.IsEffectivelyVisible);
        Assert.InRange(point.X, 0, window.ClientSize.Width); Assert.InRange(point.Y, 0, window.ClientSize.Height);
        Assert.InRange(point.X + control.Bounds.Width, 0, window.ClientSize.Width);
        Assert.InRange(point.Y + control.Bounds.Height, 0, window.ClientSize.Height);
    }
    private static void SaveFrame(TopLevel window, bool dark, int width, string name)
    {
        if (Environment.GetEnvironmentVariable("JOURNAL_WORKFLOW_SCREENSHOTS") is not { } output) return;
        Directory.CreateDirectory(output); var image = window.CaptureRenderedFrame(); Assert.NotNull(image);
        image!.Save(Path.Combine(output, $"{name}-{width}-{(dark ? "dark" : "light")}.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }
    private sealed class BindingLog : ILogSink, IDisposable
    {
        private readonly ILogSink? _previous = Logger.Sink;
        public List<string> Errors { get; } = [];
        public BindingLog() => Logger.Sink = this;
        public void Dispose() => Logger.Sink = _previous;
        public bool IsEnabled(LogEventLevel level, string area) => area == LogArea.Binding && level >= LogEventLevel.Warning;
        public void Log(LogEventLevel level, string area, object? source, string messageTemplate) => Log(level, area, source, messageTemplate, []);
        public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] values) => Errors.Add(messageTemplate + " " + string.Join("; ", values));
    }
}

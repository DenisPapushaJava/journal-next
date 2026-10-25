using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Logging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Jornal.Desktop.Controls;
using Jornal.Workflow.Desktop;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class WorkflowTechnologistWindowTests
{
    [AvaloniaTheory]
    [InlineData(false, 900)] [InlineData(true, 900)] [InlineData(false, 800)] [InlineData(true, 800)]
    public async Task DropdownRemovalDoesNotSelectRowAndPreservesDraftsWithCancelKeyboardAndCurrentProfileProtection(bool dark, int width)
    {
        using var log = new BindingLog(); using var f = new ProjectFixture(); await f.InitializeAsync();
        f.Model.ManagerName = "Только менеджер"; await f.Model.SaveHeaderCommand.ExecuteAsync(null);
        var preferences = new WorkflowPreferencesStore(Path.Combine(f.DirectoryPath, "preferences.json")); preferences.Update(p => p with { DarkTheme = dark });
        var main = new WorkflowWindow(preferences) { DataContext = f.Model, Width = 1366, Height = 768 };
        WorkflowSettingsWindow? settings = null;
        try
        {
            main.Show(); await Until(() => !f.Model.IsBusy); Click(main.FindControl<Button>("WorkflowSettings")!);
            settings = Assert.IsType<WorkflowSettingsWindow>(Assert.Single(main.OwnedWindows));
            settings.Width = width; settings.Height = width == 800 ? 580 : 660; Pump();
            var model = Assert.IsType<WorkflowSettingsViewModel>(settings.DataContext);
            Assert.DoesNotContain("Только менеджер", model.EmployeeSuggestions);
            var combo = settings.FindControl<EntrySuggestionComboBox>("WorkflowProfileName")!;
            model.ProjectRoot = Path.Combine(f.DirectoryPath, "unsaved-root");
            var input = combo.GetVisualDescendants().OfType<TextBox>().Single(); input.Focus();
            OpenList(); Assert.False(RemoveButton(f.Model.CurrentEmployee!.Name).IsEffectivelyEnabled, "The active local identity must be protected.");
            settings.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null); Pump(); input.SelectAll();
            settings.KeyTextInput("С"); Pump();
            Assert.Equal("С", model.ProfileName); Assert.Equal("Сидоров", input.Text);
            var typed = model.ProfileName; var color = model.ProfileColorArgb;
            var bytes = await File.ReadAllBytesAsync(f.Store.FilePath, TestContext.Current.CancellationToken);
            var remove = RemoveButton("Сидоров"); Assert.True(remove.IsEffectivelyEnabled);
            Save(TopLevel.GetTopLevel(remove)!, "list");
            Click(remove); Pump();
            var confirmation = Assert.IsType<WorkflowProjectActionWindow>(Assert.Single(settings.OwnedWindows));
            Assert.False(combo.IsDropDownOpen, "The list must close before its removal dialog opens."); Assert.Equal(typed, model.ProfileName);
            Assert.True(confirmation.FindControl<Button>("CancelProjectAction")!.IsFocused);
            Save(confirmation, "confirmation");
            confirmation.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null); Pump();
            Assert.Empty(settings.OwnedWindows); Assert.Equal(typed, model.ProfileName);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(f.Store.FilePath, TestContext.Current.CancellationToken));
            OpenList(); remove = RemoveButton("Сидоров"); remove.Focus(); Assert.True(remove.IsFocused);
            TopLevel.GetTopLevel(remove)!.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, null); Pump();
            confirmation = Assert.IsType<WorkflowProjectActionWindow>(Assert.Single(settings.OwnedWindows));
            Click(confirmation.FindControl<Button>("ConfirmProjectAction")!);
            await Until(() => !model.IsBusy && !model.EmployeeSuggestions.Contains("Сидоров"));
            Assert.Null(model.Error); Assert.Equal(typed, model.ProfileName); Assert.Equal(color, model.ProfileColorArgb); Assert.True(model.HasRootChanges);
            Assert.Equal("Петров", f.Model.CurrentEmployee!.Name); Assert.Equal(typed, combo.Value);
            Assert.True(combo.GetVisualDescendants().OfType<TextBox>().Single().IsFocused);
            Save(settings, "after"); Assert.True(log.Errors.Count == 0, string.Join("\n", log.Errors));

            void OpenList()
            {
                combo.GetVisualDescendants().OfType<TextBox>().Single().Focus();
                settings.KeyPress(Key.F4, RawInputModifiers.None, PhysicalKey.F4, null); Pump(); Assert.True(combo.IsDropDownOpen);
            }
            Button RemoveButton(string name) => combo.GetRealizedContainers().SelectMany(c => c.GetVisualDescendants()).OfType<Button>()
                .Single(b => b.Classes.Contains("suggestion-remove") && Equals(b.CommandParameter, name));
        }
        finally
        {
            if (settings is { IsVisible: true })
            {
                foreach (var child in settings.OwnedWindows.ToArray()) child.Close();
                Click(settings.FindControl<Button>("CloseWorkflowSettings")!);
            }
            main.Close();
        }

        void Save(TopLevel root, string state)
        {
            if (Environment.GetEnvironmentVariable("JOURNAL_WORKFLOW_SCREENSHOTS") is not { } output) return;
            Directory.CreateDirectory(output); using var frame = root.CaptureRenderedFrame();
            frame?.Save(Path.Combine(output, $"technologists-{state}-{width}-{(dark ? "dark" : "light")}.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        }
    }
    private static void Click(Control control)
    {
        var root = TopLevel.GetTopLevel(control)!; var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), root)!.Value;
        root.MouseDown(point, MouseButton.Left); root.MouseUp(point, MouseButton.Left); Pump();
    }
    private static void Pump() => Dispatcher.UIThread.RunJobs();
    private static async Task Until(Func<bool> done)
    {
        for (var i = 0; i < 500 && !done(); i++) { await Task.Delay(10, TestContext.Current.CancellationToken); Pump(); }
        Pump(); Assert.True(done());
    }
    private sealed class BindingLog : ILogSink, IDisposable
    {
        private readonly ILogSink? _previous = Logger.Sink;
        public List<string> Errors { get; } = [];
        public BindingLog() => Logger.Sink = this;
        public void Dispose() => Logger.Sink = _previous;
        public bool IsEnabled(LogEventLevel level, string area) => area == LogArea.Binding && level >= LogEventLevel.Warning;
        public void Log(LogEventLevel level, string area, object? source, string template) => Errors.Add(template);
        public void Log(LogEventLevel level, string area, object? source, string template, params object?[] values) => Errors.Add(template + " " + string.Join("; ", values));
    }
}

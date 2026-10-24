using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Logging;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Jornal.Desktop.Controls;
using Jornal.Workflow.Desktop;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class WorkflowOrderEntryWindowTests
{
    [AvaloniaTheory]
    [InlineData(false, 1366)] [InlineData(true, 1366)]
    [InlineData(false, 1180)] [InlineData(true, 1180)]
    public async Task CompactFormUsesPersonalEditableListsAndSettingsKeepUnsavedInput(bool dark, int width)
    {
        using var log = new BindingLog(); using var f = new ProjectFixture(); await f.InitializeAsync();
        var preferences = new WorkflowPreferencesStore(Path.Combine(f.DirectoryPath, "preferences.json"));
        preferences.Update(p => p with { DarkTheme = dark, EntrySuggestions = new() { Managers = ["Роман", "Руслан"], Customers = ["ООО Заказчик", "ООО Длинное название заказчика для проверки выпадающего списка"] } });
        var window = new WorkflowWindow(preferences) { DataContext = f.Model, Width = width, Height = 768 };
        try
        {
            window.Show(); while (f.Model.IsBusy) await Task.Delay(10, TestContext.Current.CancellationToken);
            Pump(); var create = window.GetVisualDescendants().OfType<JournalButton>().Single(b => b.Label == "Новая заявка");
            Click(create); Assert.True(f.Model.IsCreating);
            var form = window.FindControl<Border>("WorkflowNewOrderForm")!;
            var manager = window.FindControl<EntrySuggestionComboBox>("NewManagerInput")!;
            var customer = window.FindControl<EntrySuggestionComboBox>("NewCustomerInput")!;
            Assert.Equal(2, form.GetVisualDescendants().OfType<ComboBox>().Count());
            Assert.Equal(600, form.Bounds.Width); Assert.InRange(manager.Bounds.Width, 319, 321);
            Assert.InRange(customer.Bounds.Width, manager.Bounds.Width, 560);
            InWindow(form, window); Assert.Equal(form.TranslatePoint(default, window)!.Value.X + 20 + form.BorderThickness.Left,
                manager.TranslatePoint(default, window)!.Value.X, 1);
            var input = manager.GetVisualDescendants().OfType<TextBox>().Single(); Assert.True(input.IsFocused);
            Assert.Empty(f.Model.NewManager); Assert.Empty(f.Model.NewCustomer);
            window.KeyTextInput("Р"); Pump(); Assert.Equal("Р", f.Model.NewManager); Assert.Equal("Роман", input.Text);
            Assert.Equal(1, input.SelectionStart); Assert.Equal(5, input.SelectionEnd);
            Press(window, Key.Down); Assert.Equal("Руслан", input.Text);
            Press(window, Key.Escape); Assert.Equal("Р", input.Text); Assert.True(f.Model.IsCreating);
            input.SelectAll(); window.KeyTextInput("Р"); Pump(); Press(window, Key.Enter);
            Assert.Equal("Роман", f.Model.NewManager); Assert.True(f.Model.IsCreating); Assert.Single((await f.Store.ReadAsync(TestContext.Current.CancellationToken)).Orders);
            Press(window, Key.F4); Assert.True(manager.IsDropDownOpen); Assert.Equal(2, manager.Items.Count);
            Press(window, Key.Escape); Assert.False(manager.IsDropDownOpen);
            input.SelectAll(); window.KeyTextInput("Ру"); Pump(); Press(window, Key.Tab);
            Assert.Equal("Руслан", f.Model.NewManager);
            var customerInput = customer.GetVisualDescendants().OfType<TextBox>().Single(); Assert.True(customerInput.IsFocused);
            window.KeyTextInput("ООО "); Pump(); Press(window, Key.Enter); Assert.Equal("ООО Заказчик", f.Model.NewCustomer);
            customerInput.SelectAll(); window.KeyTextInput("Свободный заказчик"); Pump();
            Assert.Equal("Свободный заказчик", f.Model.NewCustomer);
            SaveFrame(window, $"workflow-new-order-{width}-{(dark ? "dark" : "light")}");
            Click(window.FindControl<Button>("WorkflowSettings")!);
            var settings = Assert.IsType<WorkflowSettingsWindow>(Assert.Single(window.OwnedWindows));
            if (width == 1180) { settings.Width = 800; settings.Height = 580; Pump(); }
            var parent = Assert.IsType<WorkflowSettingsViewModel>(settings.DataContext);
            Assert.Equal(3, settings.FindControl<ListBox>("WorkflowSettingsNavigation")!.Items.Count);
            Assert.Equal("Общие", parent.SectionTitle);
            var profileInput = settings.FindControl<EntrySuggestionComboBox>("WorkflowProfileName")!;
            var swatch = settings.FindControl<Border>("WorkflowCurrentProfileColor")!;
            var changeColor = settings.FindControl<Button>("ChangeWorkflowProfileColor")!;
            InWindow(profileInput, settings); InWindow(swatch, settings); InWindow(changeColor, settings);
            Assert.InRange(Math.Abs(profileInput.TranslatePoint(default, settings)!.Value.Y - swatch.TranslatePoint(default, settings)!.Value.Y), 0, 2);
            Assert.InRange(Math.Abs(profileInput.TranslatePoint(default, settings)!.Value.Y - changeColor.TranslatePoint(default, settings)!.Value.Y), 0, 2);
            parent.ProfileName = "Несохранённый технолог";
            var originalColor = parent.ProfileColorArgb;
            SaveFrame(settings, $"workflow-settings-general-{width}-{(dark ? "dark" : "light")}");
            Click(settings.FindControl<Button>("WorkflowEntrySuggestionsButton")!);
            var cancelled = Assert.IsType<WorkflowEntrySuggestionsWindow>(Assert.Single(settings.OwnedWindows));
            Assert.IsType<WorkflowEntrySuggestionsViewModel>(cancelled.DataContext).ManagersText = "Не сохранять";
            Press(cancelled, Key.Escape); Assert.False(cancelled.IsVisible);
            Assert.Equal(new[] { "Роман", "Руслан" }, f.Model.ManagerSuggestions);
            Assert.Equal("Несохранённый технолог", parent.ProfileName);
            Assert.True(settings.FindControl<Button>("WorkflowEntrySuggestionsButton")!.IsFocused);
            Click(settings.FindControl<Button>("WorkflowEntrySuggestionsButton")!);
            var lists = Assert.IsType<WorkflowEntrySuggestionsWindow>(Assert.Single(settings.OwnedWindows));
            if (width == 1180) { lists.Width = 680; lists.Height = 440; Pump(); }
            var model = Assert.IsType<WorkflowEntrySuggestionsViewModel>(lists.DataContext);
            var managers = lists.FindControl<TextBox>("WorkflowManagersList")!;
            var customers = lists.FindControl<TextBox>("WorkflowCustomersList")!;
            InWindow(managers, lists); InWindow(customers, lists); InWindow(lists.FindControl<Button>("SaveWorkflowSuggestions")!, lists);
            Assert.True(managers.IsFocused);
            model.ManagersText = new string('Я', 256);
            await model.SaveCommand.ExecuteAsync(null); Assert.NotNull(model.Error); Assert.True(lists.IsVisible);
            Assert.Equal(new string('Я', 256), model.ManagersText);
            model.ManagersText = "Персональный менеджер"; model.CustomersText = "Новый заказчик\nЕщё заказчик";
            SaveFrame(lists, $"workflow-settings-lists-{width}-{(dark ? "dark" : "light")}");
            await model.SaveCommand.ExecuteAsync(null); Pump(); Assert.Null(model.Error); Assert.True(model.WasSaved); Assert.False(lists.IsVisible);
            Assert.True(settings.FindControl<Button>("WorkflowEntrySuggestionsButton")!.IsFocused);
            Assert.Contains("Менеджеры: 1", parent.EntrySuggestionsSummary);
            Assert.Equal("Несохранённый технолог", parent.ProfileName); Assert.Equal(originalColor, parent.ProfileColorArgb); Assert.True(parent.HasProfileChanges);
            Click(settings.FindControl<Button>("CloseWorkflowSettings")!); Pump();
            Assert.Equal("Руслан", f.Model.NewManager); Assert.Equal("Свободный заказчик", f.Model.NewCustomer);
            Assert.Equal(new[] { "Персональный менеджер" }, manager.Suggestions); Assert.Equal(2, customer.Suggestions!.Count);
            Assert.True(f.Model.IsCreating); Assert.Single((await f.Store.ReadAsync(TestContext.Current.CancellationToken)).Orders);
            Click(form.GetVisualDescendants().OfType<JournalButton>().Single(b => b.Label == "Создать"));
            while (f.Model.IsBusy) await Task.Delay(10, TestContext.Current.CancellationToken);
            Pump(); Assert.Null(f.Model.Error); Assert.False(f.Model.IsCreating); Assert.Equal(2, (await f.Store.ReadAsync(TestContext.Current.CancellationToken)).Orders.Length);
            Assert.Empty(log.Errors);
        }
        finally { foreach (var child in window.OwnedWindows.ToArray()) child.Close(); window.Close(); }
    }
    private static void Pump() => Dispatcher.UIThread.RunJobs();
    private static void Press(TopLevel window, Key key)
    { window.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, null); Pump(); }
    private static void Click(Control control)
    {
        var root = TopLevel.GetTopLevel(control)!;
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), root)!.Value;
        root.MouseDown(point, MouseButton.Left); root.MouseUp(point, MouseButton.Left); Pump();
    }
    private static void InWindow(Control control, Window window)
    {
        Assert.True(control.IsEffectivelyVisible); var point = control.TranslatePoint(default, window)!.Value;
        Assert.InRange(point.X, 0, window.ClientSize.Width); Assert.InRange(point.Y, 0, window.ClientSize.Height);
        Assert.InRange(point.X + control.Bounds.Width, 0, window.ClientSize.Width);
        Assert.InRange(point.Y + control.Bounds.Height, 0, window.ClientSize.Height);
    }
    private static void SaveFrame(TopLevel window, string name)
    {
        if (Environment.GetEnvironmentVariable("JOURNAL_WORKFLOW_SCREENSHOTS") is not { } output) return;
        Directory.CreateDirectory(output); using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
        frame.Save(Path.Combine(output, name + ".png"), new PngBitmapEncoderOptions());
    }
    private sealed class BindingLog : ILogSink, IDisposable
    {
        private readonly ILogSink? _previous = Logger.Sink;
        public List<string> Errors { get; } = [];
        public BindingLog() => Logger.Sink = this;
        public void Dispose() => Logger.Sink = _previous;
        public bool IsEnabled(LogEventLevel level, string area) => area == LogArea.Binding && level >= LogEventLevel.Warning;
        public void Log(LogEventLevel level, string area, object? source, string messageTemplate) => Errors.Add(messageTemplate);
        public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] values) => Errors.Add(messageTemplate);
    }
}

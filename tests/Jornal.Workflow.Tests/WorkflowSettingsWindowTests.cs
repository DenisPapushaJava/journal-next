using Avalonia;
using System.Net;
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
using Jornal.Workflow.Infrastructure;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class WorkflowSettingsWindowTests
{
    [AvaloniaFact]
    public void ConnectionChecksExistingEditsBeforeSavingOrPreparingTheNetwork()
    {
        const string problem = "Сохраните изменения настроек перед сменой базы.";
        using var fixture = new ProjectFixture();
        using var settings = fixture.Model.CreateGeneralSettings();
        settings.ProjectRoot = Path.Combine(fixture.DirectoryPath, "unsaved");
        var remembered = 0;
        var window = new WorkflowOfficeConnectionWindow(null, null, settings.OfficeStorageSwitchProblem,
            (_, _) => { remembered++; return Task.CompletedTask; }, (_, _, _, _, _) => throw new InvalidOperationException());
        try
        {
            window.Show();
            window.FindControl<TextBox>("ServerAddress")!.Text = "http://10.0.0.2:60913/";
            window.FindControl<TextBox>("AdministratorKey")!.Text = new('A', 64);
            Dispatcher.UIThread.RunJobs();
            Click(window, "ConnectOffice");
            Assert.Equal(problem, window.FindControl<TextBlock>("ConnectionError")!.Text);
            Assert.Equal(0, remembered);
            Assert.True(settings.HasChanges);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void OfficeConnectionShowsNetworkIdAndRestoresItFromAnOlderLinuxDraft()
    {
        const string id = "0123456789abcdef";
        var draft = new WorkflowOfficeSetupDraft("http://10.0.0.2:60913/", null) { LinuxInterfaceName = id };
        var window = new WorkflowOfficeConnectionWindow(null, draft, () => null, (_, _) => Task.CompletedTask,
            (_, _, _, _, _) => throw new InvalidOperationException());
        try
        {
            window.Show();
            var network = window.FindControl<TextBox>("NetworkId")!;
            Assert.True(network.IsEffectivelyVisible);
            Assert.Equal(id, network.Text);
            Assert.Contains("Network ID", network.PlaceholderText);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData("http://10.0.0.3:60913/", true)]
    [InlineData("http://10.0.0.2:60914/", true)]
    [InlineData("  http://10.0.0.2:60913  ", false)]
    public void OfficeConnectionBindsEnteredKeyAndNetworkToTheServerAddress(string address, bool changed)
    {
        const string id = "0123456789abcdef";
        var key = new string('A', 64);
        var draft = new WorkflowOfficeSetupDraft("http://10.0.0.2:60913/", id);
        var remembered = 0;
        var attempted = 0;
        var window = new WorkflowOfficeConnectionWindow(null, draft, () => null,
            (_, _) => { remembered++; return Task.CompletedTask; },
            (_, _, _, _, _) => { attempted++; return Task.FromResult<string?>("Simulated attempt"); });
        try
        {
            window.Show();
            var keyField = window.FindControl<TextBox>("AdministratorKey")!;
            var networkField = window.FindControl<TextBox>("NetworkId")!;
            keyField.Text = key;
            window.FindControl<TextBox>("ServerAddress")!.Text = address;
            Assert.Equal(changed ? "" : key, keyField.Text ?? "");
            Assert.Equal(changed ? "" : id, networkField.Text ?? "");
            if (changed)
            {
                Click(window, "ConnectOffice");
                Assert.Equal(0, remembered);
                Assert.Equal(0, attempted);
                Assert.Contains("Ключ администратора", window.FindControl<TextBlock>("ConnectionError")!.Text);
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void OfficeConnectionOffersOfficialDownloadAndContinuesAfterInstallation(bool browserOpened)
    {
        const string address = "http://10.0.0.2:60913/";
        const string networkId = "0123456789abcdef";
        const string downloads = "https://www.zerotier.com/download/";
        var key = new string('A', 64);
        var draft = new WorkflowOfficeSetupDraft(address, networkId);
        var localAddress = IPAddress.Parse("10.0.0.3");
        var binding = new ZeroTierBinding(localAddress, "Office", 24);
        var route = new WorkflowOfficeRoute(localAddress, binding.InterfaceAlias, "office-adapter");
        var setup = new WorkflowOfficeSetup((_, _, _) => Task.FromResult(new WorkflowOfficeProbe(WorkflowOfficeHealth.Reachable, route)),
            (_, _) => Task.FromResult(new ZeroTierNetwork("OK", binding)), (_, _) => throw new InvalidOperationException(),
            _ => throw new InvalidOperationException(), TimeSpan.Zero);
        var installed = false;
        Uri? openedPage = null;
        WorkflowOfficeSetupDraft? remembered = null;
        (string? Address, string? Key, WorkflowOfficeRoute? Route)? attempt = null;
        var window = new WorkflowOfficeConnectionWindow(null, draft, () => null,
            (value, _) => { remembered = value; return Task.CompletedTask; },
            (server, adminKey, confirmed, _, _) =>
            {
                attempt = (server, adminKey, confirmed);
                return Task.FromResult<string?>(null);
            }, setup: setup, readZeroTierStatus: () => (true, installed),
            openDownloadPage: uri => { openedPage = uri; return Task.FromResult(browserOpened); });
        try
        {
            window.Show();
            window.FindControl<TextBox>("AdministratorKey")!.Text = key;
            var download = window.FindControl<JournalButton>("DownloadZeroTier")!;
            Assert.False(download.IsVisible);

            Click(window, "ConnectOffice");
            Assert.Equal(draft, remembered);
            Assert.True(download.IsVisible);
            Assert.Equal("Проверить установку", window.FindControl<JournalButton>("ConnectOffice")!.Label);
            Assert.Null(attempt);
            Assert.Null(openedPage);

            Click(download);
            Assert.Equal(downloads, openedPage!.AbsoluteUri);
            Assert.True(window.IsVisible);
            var error = window.FindControl<TextBlock>("ConnectionError")!;
            Assert.Equal(!browserOpened, error.IsVisible);
            if (!browserOpened) Assert.Contains(downloads, error.Text);
            Assert.Equal(address, window.FindControl<TextBox>("ServerAddress")!.Text);
            Assert.Equal(networkId, window.FindControl<TextBox>("NetworkId")!.Text);
            Assert.Equal(key, window.FindControl<TextBox>("AdministratorKey")!.Text);

            Click(window, "ConnectOffice");
            Assert.True(download.IsVisible);
            Assert.Null(attempt);

            installed = true;
            Click(window, "ConnectOffice");
            Assert.False(window.IsVisible);
            Assert.Equal((address, key, route), attempt);
        }
        finally { if (window.IsVisible) window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false)] [InlineData(true)]
    public async Task OfficeConnectionCanBeCancelledUntilSettingsCommit(bool committing)
    {
        var ready = new WorkflowOfficeRoute(IPAddress.Parse("10.0.0.3"), "Office", "office-adapter");
        var setup = new WorkflowOfficeSetup((_, _, _) => Task.FromResult(new WorkflowOfficeProbe(WorkflowOfficeHealth.Reachable, ready)),
            (_, _) => throw new InvalidOperationException(), (_, _) => throw new InvalidOperationException(),
            _ => throw new InvalidOperationException(), TimeSpan.Zero);
        var pending = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var began = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var owner = new Window();
        var saved = new WorkflowOfficeStorageSettings("http://10.0.0.2:60913/", Guid.NewGuid(), Guid.NewGuid(), true)
            { ZeroTierInterfaceId = ready.InterfaceId };
        var window = new WorkflowOfficeConnectionWindow(saved, null, () => null, (_, _) => Task.CompletedTask,
            (_, _, _, token, commit) =>
            {
                if (committing) commit!.Invoke();
                began.SetResult(token);
                return pending.Task.WaitAsync(token);
            }, setup: setup);
        try
        {
            owner.Show();
            var result = window.ShowDialog<bool>(owner);
            Assert.False(window.FindControl<StackPanel>("NetworkFields")!.IsVisible);
            Click(window, "ConnectOffice");
            var cancellation = TestContext.Current.CancellationToken;
            var read = await began.Task.WaitAsync(cancellation);
            Assert.Equal(!committing, window.FindControl<Button>("CancelOfficeConnection")!.IsEnabled);
            window.Close();
            Assert.Equal(committing, window.IsVisible);
            Assert.Equal(!committing, read.IsCancellationRequested);
            pending.SetResult(null);
            Assert.Equal(committing, await result.WaitAsync(cancellation));
        }
        finally
        {
            pending.TrySetResult(null);
            if (window.IsVisible) window.Close();
            if (owner.IsVisible) owner.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData(false)] [InlineData(true)]
    public async Task StorageShowsTheCurrentSourceAndOnlyItsFolderSettings(bool dark)
    {
        using var log = new BindingLog();
        using var fixture = new ProjectFixture();
        var remote = new WorkflowOfficeStorageSettings("http://10.0.0.2:60913/", Guid.NewGuid(), Guid.NewGuid(), true)
            { ZeroTierInterfaceId = "office-adapter" };
        var preferences = new WorkflowPreferencesStore(Path.Combine(fixture.DirectoryPath, "preferences.json"));
        var saved = new WorkflowPreferences(dark) { OfficeStorage = remote, UseOfficeStorage = true };
        preferences.Save(saved);
        var store = new WorkflowMdbStore(Path.Combine(fixture.DirectoryPath, "office.mdb"), databaseId: remote.DatabaseId);
        var journal = new WorkflowViewModel(store, new(new WorkflowAppUpdater()), preferences: preferences, initialPreferences: saved);
        var model = journal.CreateGeneralSettings();
        model.SelectedSectionIndex = 1;
        var window = new WorkflowSettingsWindow { DataContext = model };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            Assert.Equal("Сервер заявок", window.FindControl<TextBlock>("StorageSourceTitle")!.Text);
            Assert.Equal(remote.Address, window.FindControl<TextBox>("WorkflowWorkspacePath")!.Text);
            Assert.Equal("Настроить подключение…", window.FindControl<JournalButton>("OpenOfficeConnection")!.Label);
            Assert.True(Visible("ReturnToLocalStorage"));
            Assert.True(Visible("WorkflowOfficeCopiesRoot"));
            Assert.False(Visible("UseSavedOfficeConnection"));
            Assert.False(Visible("ConnectWorkflowDatabase"));
            Assert.False(Visible("CreateWorkflowDatabase"));
            Assert.False(Visible("WorkflowProjectRoot"));
            Assert.False(Visible("WorkflowShopRoot"));

            Assert.Null(await model.ReturnToLocalStorageAsync());
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("База данных заявок", window.FindControl<TextBlock>("StorageSourceTitle")!.Text);
            Assert.True(Visible("UseSavedOfficeConnection"));
            Assert.True(Visible("ConnectWorkflowDatabase"));
            Assert.True(Visible("CreateWorkflowDatabase"));
            Assert.True(Visible("WorkflowProjectRoot"));
            Assert.True(Visible("WorkflowShopRoot"));
            Assert.False(Visible("ReturnToLocalStorage"));
            Assert.False(Visible("WorkflowOfficeCopiesRoot"));
            Assert.True(log.Errors.Count == 0, string.Join("\n", log.Errors));
        }
        finally { window.Close(); journal.Detach(); }

        bool Visible(string name) => window.FindControl<Control>(name)!.IsEffectivelyVisible;
    }

    [AvaloniaTheory]
    [InlineData(false)] [InlineData(true)]
    public async Task EmptyEmployeeCatalogCanBeConfiguredThroughSettingsInBothThemes(bool dark)
    {
        using var log = new BindingLog(); using var f = new ProjectFixture();
        var preferences = new WorkflowPreferencesStore(Path.Combine(f.DirectoryPath, "preferences.json"));
        preferences.Save(new(dark));
        var window = new WorkflowWindow(preferences) { DataContext = f.Model, Width = 1180, Height = 768 };
        try
        {
            window.Show();
            for (var i = 0; i < 500 && f.Model.IsBusy; i++) { await Task.Delay(10, TestContext.Current.CancellationToken); Dispatcher.UIThread.RunJobs(); }
            Dispatcher.UIThread.RunJobs(); Assert.False(f.Model.IsBusy); Assert.Empty(f.Model.Employees);
            Assert.Equal("Профиль не выбран", f.Model.ProfileName); Click(window, "WorkflowSettings");
            var settings = Assert.IsType<WorkflowSettingsWindow>(Assert.Single(window.OwnedWindows));
            var model = Assert.IsType<WorkflowSettingsViewModel>(settings.DataContext);
            Assert.Empty(model.EmployeeSuggestions); Assert.Empty(model.ProfileName);
            var input = settings.FindControl<EntrySuggestionComboBox>("WorkflowProfileName")!;
            input.GetVisualDescendants().OfType<TextBox>().Single().Focus();
            settings.KeyTextInput("Лебедев"); Dispatcher.UIThread.RunJobs();
            Assert.Equal("Лебедев", model.ProfileName); await model.SaveCommand.ExecuteAsync(null); Assert.Null(model.Error);
            Assert.Equal("Лебедев", Assert.Single(f.Model.Employees).Name); Assert.Equal("Лебедев", f.Model.ProfileName);
            SaveFrame(settings, dark, 800, "workflow-first-profile");
            Assert.True(log.Errors.Count == 0, string.Join("\n", log.Errors));
        }
        finally { foreach (var child in window.OwnedWindows.ToArray()) child.Close(); WorkflowTestWindows.Close(window); }
    }

    [AvaloniaTheory]
    [InlineData(false, 1366)] [InlineData(true, 1366)]
    [InlineData(false, 1180)] [InlineData(true, 1180)]
    public async Task ToolbarOpensIndependentCatalogAndSettingsWithoutLosingTheActiveDraft(bool dark, int width)
    {
        using var log = new BindingLog(); using var f = new ProjectFixture(); await f.InitializeAsync();
        var project = await f.AddAsync(); await ProjectFixture.WriteAsync(project); await f.AcceptAsync();
        var preferences = new WorkflowPreferencesStore(Path.Combine(f.DirectoryPath, "preferences.json")); preferences.Save(new(dark) { EmployeeId = WorkflowTestData.CreateWorkspace().Employees[1].Id });
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
            Assert.Null(settings.FindControl<Button>("OpenOfficePreview"));
            Click(settings, "OpenOfficeConnection");
            var remote = Assert.IsType<WorkflowOfficeConnectionWindow>(Assert.Single(settings.OwnedWindows));
            Assert.Equal(settings.ActualThemeVariant, remote.ActualThemeVariant);
            Assert.NotEqual('\0', remote.FindControl<TextBox>("AdministratorKey")!.PasswordChar);
            Assert.True(remote.FindControl<StackPanel>("NetworkFields")!.IsVisible);
            Assert.False(remote.FindControl<Button>("CopyDeviceId")!.IsVisible);
            Click(remote, "ConnectOffice");
            Assert.Contains("Вставьте приватный адрес", remote.FindControl<TextBlock>("ConnectionError")!.Text!);
            remote.Close(); Dispatcher.UIThread.RunJobs();
            Assert.True(settings.FindControl<Button>("OpenOfficeConnection")!.IsFocused);
            Assert.Equal(root, model.ProjectRoot);
            Assert.Equal(f.Store.FilePath, model.WorkspacePath);
            Assert.Same(draft, f.Model.SelectedDraft);
            await model.SaveCommand.ExecuteAsync(null); Assert.Null(model.Error); Assert.False(model.HasChanges);
            Assert.Same(draft, f.Model.SelectedDraft); Assert.Equal(quote, draft.Quote);
            Assert.Equal("Открытый черновик", draft.Comment); Assert.True(f.Model.HasPendingEdits);
            Dispatcher.UIThread.RunJobs(); SaveFrame(settings, dark, width, "workflow-settings-folders");
            Assert.Equal(f.Store.FilePath, settings.FindControl<TextBox>("WorkflowWorkspacePath")!.Text);
            Assert.True(settings.FindControl<TextBox>("WorkflowWorkspacePath")!.IsReadOnly);
            Assert.Equal("Хранилище", model.SectionTitle);
            settings.FindControl<ListBox>("WorkflowSettingsNavigation")!.SelectedIndex = 2; Dispatcher.UIThread.RunJobs();
            Assert.Equal("Обновления", model.SectionTitle); Assert.Same(f.Model.Updates, model.Updates);
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
            WorkflowTestWindows.Close(window);
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

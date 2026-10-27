using System.IO.Compression;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Jornal.Desktop.Updates;
using Jornal.Desktop.ViewModels;
using Jornal.Infrastructure.Diagnostics;
using Jornal.Workflow.Desktop;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class WorkflowProgramSettingsTests
{
    [AvaloniaTheory]
    [InlineData(false, 1366)] [InlineData(true, 1366)]
    [InlineData(false, 1180)] [InlineData(true, 1180)]
    public async Task BellUsesCompactUpdatesAndSettingsPreserveIdentityAndPersonalLists(bool dark, int width)
    {
        using var bindingLog = new WorkflowWindowTests.BindingLog();
        using var f = new ProjectFixture(); await f.InitializeAsync();
        var preferences = new WorkflowPreferencesStore(Path.Combine(f.DirectoryPath, "preferences.json"));
        preferences.Update(p => p with { DarkTheme = dark, EntrySuggestions = new() { Managers = ["Личный менеджер"] } });
        var window = new WorkflowWindow(preferences) { DataContext = f.Model, Width = width, Height = 768 };
        try
        {
            window.Show(); await ReadyAsync(f.Model); Click(window, "UpdatesBell");
            var updateWindow = Assert.IsType<WorkflowUpdatesWindow>(Assert.Single(window.OwnedWindows));
            Assert.Same(f.Model.Updates, updateWindow.DataContext);
            var panel = updateWindow.FindControl<WorkflowUpdatesPanel>("UpdatesPanel")!;
            Assert.Equal(640, updateWindow.Width);
            Assert.Equal("https://github.com/DenisPapushaJava/journal-next", WorkflowIdentity.UpdatesRepository!.AbsoluteUri);
            Assert.True(panel.FindControl<Button>("OpenWorkflowReleases")!.IsEffectivelyVisible);
            Assert.False(panel.FindControl<Button>("CheckWorkflowUpdates")!.IsEffectivelyEnabled);
            Frame(updateWindow, $"workflow-updates-{width}-{(dark ? "dark" : "light")}");
            Click(window, "UpdatesBell"); Assert.Same(updateWindow, Assert.Single(window.OwnedWindows));
            Click(window, "WorkflowSettings"); Assert.False(updateWindow.IsVisible);
            var settings = Assert.IsType<WorkflowSettingsWindow>(Assert.Single(window.OwnedWindows));
            if (width == 1180) { settings.Width = 800; settings.Height = 580; Pump(); }
            var model = Assert.IsType<WorkflowSettingsViewModel>(settings.DataContext);
            Assert.Equal(0, model.SelectedSectionIndex); Assert.Same(f.Model.Updates, model.Updates);
            Assert.Equal(new[] { "Общие", "Хранилище", "Обновления", "Диагностика", "О программе" }, settings.FindControl<ListBox>("WorkflowSettingsNavigation")!.Items.Cast<ListBoxItem>().Select(i => i.Content));
            for (var i = 0; i < 5; i++)
            {
                model.SelectedSectionIndex = i; Pump();
                Assert.Equal(i < 2, settings.FindControl<Button>("SaveWorkflowSettings")!.IsEffectivelyVisible);
                Frame(settings, $"workflow-settings-{i}-{width}-{(dark ? "dark" : "light")}");
            }
            model.SelectedSectionIndex = 0; Pump();
            var employee = f.Model.CurrentEmployee!.Id;
            settings.FindControl<CheckBox>("WorkflowDarkTheme")!.IsChecked = !dark; Pump();
            Assert.Equal(!dark, f.Model.IsDarkTheme); Assert.Equal(!dark, preferences.Load().DarkTheme);
            Assert.Equal(!dark ? ThemeVariant.Dark : ThemeVariant.Light, settings.ActualThemeVariant);
            Assert.Equal(settings.ActualThemeVariant, window.ActualThemeVariant);
            Assert.Equal(employee, preferences.Load().EmployeeId); Assert.Equal("Личный менеджер", Assert.Single(preferences.Load().EntrySuggestions!.Managers));
            using (var lease = new FileStream(Path.Combine(f.DirectoryPath, "preferences.json.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                settings.FindControl<CheckBox>("WorkflowDarkTheme")!.IsChecked = dark; Pump();
                Assert.NotNull(f.Model.ThemeError); Assert.Equal(f.Model.ThemeError, settings.FindControl<TextBlock>("WorkflowThemeError")!.Text);
                Assert.Equal(!dark, preferences.Load().DarkTheme);
            }
            settings.FindControl<CheckBox>("WorkflowDarkTheme")!.IsChecked = !dark; Pump(); Assert.Null(f.Model.ThemeError);
            Frame(settings, $"workflow-general-theme-{width}-{(dark ? "dark" : "light")}");
            Click(window, "UpdatesBell"); Assert.Same(settings, Assert.Single(window.OwnedWindows)); Assert.Equal(2, model.SelectedSectionIndex);
            Assert.Empty(bindingLog.Errors);
        }
        finally { foreach (var child in window.OwnedWindows.ToArray()) child.Close(); window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false)] [InlineData(true)]
    public async Task UpdateRestartRejectsDraftsAndChildEditorsAndPreservesTextOnSuccessOrFailure(bool failRestart)
    {
        using var f = new ProjectFixture(); await f.InitializeAsync();
        var preferences = new WorkflowPreferencesStore(Path.Combine(f.DirectoryPath, "preferences.json"));
        var legacyPreferences = Path.Combine(f.DirectoryPath, "legacy-update-settings.json");
        await File.WriteAllTextAsync(legacyPreferences, "Не менять настройки прежнего Journal", TestContext.Current.CancellationToken);
        var updater = new InMemoryUpdater { FailRestart = failRestart };
        var updatePreferences = Path.Combine(f.DirectoryPath, "workflow-update-settings.json");
        var updates = new AppUpdatesViewModel(updater, preferencesStore: new JsonAppUpdatePreferencesStore(updatePreferences));
        updates.CheckAutomatically = false;
        var client = new WorkflowViewModel(f.Store, updates);
        var window = new WorkflowWindow(preferences) { DataContext = client, Width = 1180, Height = 768 };
        try
        {
            window.Show(); await ReadyAsync(client);
            client.ResultText = "Ручной текст перед обновлением\n";
            await updates.CheckCommand.ExecuteAsync(null); await updates.DownloadCommand.ExecuteAsync(null); Assert.True(updates.IsDownloaded);
            Click(window, "WorkflowSettings");
            var settings = Assert.IsType<WorkflowSettingsWindow>(Assert.Single(window.OwnedWindows));
            var model = Assert.IsType<WorkflowSettingsViewModel>(settings.DataContext);
            Assert.Same(updates, model.Updates);
            var preview = !updates.IncludePreview; updates.IncludePreview = preview;
            Assert.Equal(preview, new JsonAppUpdatePreferencesStore(updatePreferences).Load().Preferences!.IncludePreview);
            Assert.Equal("Не менять настройки прежнего Journal", await File.ReadAllTextAsync(legacyPreferences, TestContext.Current.CancellationToken));
            await updates.CheckCommand.ExecuteAsync(null); await updates.DownloadCommand.ExecuteAsync(null); Assert.True(updates.CanInstall);
            client.AddLegacySpecificationDraft(); client.SelectedDraft!.Material = "Не потерять ввод";
            Assert.True(client.HasPendingEdits);
            updates.InstallCommand.Execute(null); Assert.Equal(0, updater.Restarts); Assert.True(window.IsVisible);
            client.CancelDraftCommand.Execute(null);
            model.ProfileName = "Несохранённый профиль";
            updates.InstallCommand.Execute(null); Assert.Equal(0, updater.Restarts); Assert.True(settings.IsVisible);
            model.ProfileName = client.ProfileName;
            model.ProjectRoot += "-unsaved";
            updates.InstallCommand.Execute(null); Assert.Equal(0, updater.Restarts);
            model.ProjectRoot = (await f.Store.ReadAsync(TestContext.Current.CancellationToken)).ProjectRoot!;
            model.SelectedSectionIndex = 0; Pump(); Click(settings, "WorkflowEntrySuggestionsButton");
            var lists = Assert.Single(settings.OwnedWindows);
            updates.InstallCommand.Execute(null); Assert.Equal(0, updater.Restarts); Assert.True(lists.IsVisible);
            lists.Close(); Pump(); model.SelectedSectionIndex = 2; Pump();
            updates.InstallCommand.Execute(null); Pump(); Assert.Equal(1, updater.Restarts);
            Assert.Equal(failRestart, window.IsVisible); Assert.Equal(failRestart, settings.IsVisible);
            Assert.Equal("Ручной текст перед обновлением\n", client.ResultText);
            var restart = new WorkflowRestartStore(Path.Combine(f.DirectoryPath, "update-restart.json")).Take();
            Assert.NotNull(restart); Assert.Contains(restart.Results!, result => result.Text == client.ResultText);
            if (failRestart) { Assert.NotNull(updates.Error); Assert.True(model.CanClose); Assert.False(updates.IsApplying); }
        }
        finally { foreach (var child in window.OwnedWindows.ToArray()) child.Close(); window.Close(); client.Detach(); }
    }

    [AvaloniaTheory]
    [InlineData(false)] [InlineData(true)]
    public async Task CompactUpdatesKeepDownloadAcrossReopenAndRestartSafely(bool dark)
    {
        using var f = new ProjectFixture(); await f.InitializeAsync();
        var preferences = new WorkflowPreferencesStore(Path.Combine(f.DirectoryPath, "preferences.json"));
        preferences.Update(p => p with { DarkTheme = dark });
        var updater = new InMemoryUpdater();
        var updates = new AppUpdatesViewModel(updater);
        var client = new WorkflowViewModel(f.Store, updates, preferences: preferences);
        var window = new WorkflowWindow(preferences) { DataContext = client };
        try
        {
            window.Show(); await ReadyAsync(client); Click(window, "UpdatesBell");
            var compact = Assert.IsType<WorkflowUpdatesWindow>(Assert.Single(window.OwnedWindows));
            compact.Width = 600; compact.Height = 540; Pump();
            Assert.Equal(dark ? ThemeVariant.Dark : ThemeVariant.Light, compact.ActualThemeVariant);
            var panel = compact.FindControl<WorkflowUpdatesPanel>("UpdatesPanel")!;
            await updates.CheckCommand.ExecuteAsync(null); Pump();
            Assert.True(panel.FindControl<Button>("DownloadWorkflowUpdate")!.IsEffectivelyVisible);
            Assert.False(panel.FindControl<Button>("InstallWorkflowUpdate")!.IsEffectivelyVisible);
            Frame(compact, $"workflow-update-available-{dark}");
            await updates.DownloadCommand.ExecuteAsync(null); Pump();
            Assert.False(panel.FindControl<Button>("DownloadWorkflowUpdate")!.IsEffectivelyVisible);
            Assert.True(panel.FindControl<Button>("InstallWorkflowUpdate")!.IsEffectivelyVisible);
            compact.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null); Pump(); Assert.Empty(window.OwnedWindows);
            Click(window, "UpdatesBell"); compact = Assert.IsType<WorkflowUpdatesWindow>(Assert.Single(window.OwnedWindows));
            Assert.Same(updates, compact.DataContext); Assert.True(updates.IsDownloaded);
            Frame(compact, $"workflow-update-downloaded-{dark}");
            client.AddLegacySpecificationDraft(); updates.InstallCommand.Execute(null); Assert.Equal(0, updater.Restarts);
            client.CancelDraftCommand.Execute(null); updates.InstallCommand.Execute(null); Pump();
            Assert.Equal(1, updater.Restarts); Assert.False(compact.IsVisible); Assert.False(window.IsVisible);
        }
        finally { foreach (var child in window.OwnedWindows.ToArray()) child.Close(); window.Close(); client.Detach(); }
    }

    [AvaloniaTheory]
    [InlineData(false)] [InlineData(true)]
    public async Task CompactUpdatesConstrainLongNotesAndKeepCancellationAndErrorsVisible(bool dark)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var updater = new InMemoryUpdater { DownloadGate = gate, Notes = string.Join("\n", Enumerable.Range(1, 100).Select(i => $"{i}. Проверка длинного описания изменений.")) };
        var updates = new AppUpdatesViewModel(updater);
        var window = new WorkflowUpdatesWindow { DataContext = updates, Width = 600, Height = 540, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
        try
        {
            window.Show(); await updates.CheckCommand.ExecuteAsync(null);
            var downloading = updates.DownloadCommand.ExecuteAsync(null); Pump();
            var panel = window.FindControl<WorkflowUpdatesPanel>("UpdatesPanel")!;
            var cancel = panel.FindControl<Button>("CancelWorkflowDownload")!;
            Assert.True(cancel.IsEffectivelyVisible); Assert.True(cancel.IsEffectivelyEnabled);
            var point = cancel.TranslatePoint(default, window)!.Value;
            Assert.InRange(point.X + cancel.Bounds.Width, 0, window.ClientSize.Width);
            Assert.InRange(point.Y + cancel.Bounds.Height, 0, window.ClientSize.Height);
            Frame(window, $"workflow-update-downloading-{dark}");
            updates.DownloadCancelCommand.Execute(null); await downloading;
            Assert.False(updates.IsDownloading); Assert.False(updates.IsDownloaded);
            gate.TrySetResult(); updater.FailDownload = true;
            await updates.DownloadCommand.ExecuteAsync(null); Pump();
            Assert.NotNull(updates.Error); Assert.False(cancel.IsEffectivelyVisible);
            Assert.True(panel.FindControl<Button>("DownloadWorkflowUpdate")!.IsEffectivelyVisible);
            Assert.True(panel.FindControl<Button>("DownloadWorkflowUpdate")!.IsEffectivelyEnabled);
            Frame(window, $"workflow-update-error-{dark}");
        }
        finally { updates.DownloadCommand.Cancel(); gate.TrySetResult(); window.Close(); }
    }

    [Fact]
    public async Task DiagnosticsExportUsesOwnServiceAndBlocksClosingAndRestartUntilItFinishes()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync();
        var ownDirectory = Path.Combine(f.DirectoryPath, "workflow-diagnostics");
        var legacyFile = Path.Combine(f.DirectoryPath, "legacy-report.txt");
        await File.WriteAllTextAsync(legacyFile, "Legacy sentinel", TestContext.Current.CancellationToken);
        using var http = new HttpClient();
        using var service = new DiagnosticService(new(ownDirectory), http, null, WorkflowIdentity.Version);
        var code = service.Capture(DiagnosticOperation.Unexpected, new InvalidOperationException("Synthetic Workflow error"));
        var client = new WorkflowViewModel(f.Store, new AppUpdatesViewModel(new WorkflowAppUpdater()), service);
        using var settings = client.CreateGeneralSettings(); Assert.True(settings.HasDiagnostics);
        Assert.Equal(WorkflowIdentity.DiagnosticsDirectory, settings.DiagnosticsDirectory); Assert.False(service.Configured);
        var destination = new TaskCompletionSource<Stream?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var path = Path.Combine(f.DirectoryPath, "Workflow-report.zip");
        var export = settings.ExportDiagnosticsAsync(_ => destination.Task, TestContext.Current.CancellationToken);
        Assert.True(settings.IsExporting); Assert.False(settings.CanClose); Assert.False(settings.CanRestart); Assert.False(settings.CanEdit);
        destination.SetResult(File.Create(path)); await export.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(settings.CanClose); Assert.True(settings.CanRestart); Assert.Contains(WorkflowIdentity.Name, settings.DiagnosticsStatus);
        using var archive = ZipFile.OpenRead(path); var entry = Assert.Single(archive.Entries);
        using var reader = new StreamReader(entry.Open());
        var report = System.Text.Json.JsonSerializer.Deserialize<DiagnosticReport>(await reader.ReadToEndAsync(TestContext.Current.CancellationToken), DiagnosticReport.Json);
        Assert.NotNull(report); Assert.Equal(code, report.Code);
        Assert.StartsWith("events/", entry.FullName);
        Assert.Equal("Legacy sentinel", await File.ReadAllTextAsync(legacyFile, TestContext.Current.CancellationToken));
        await settings.ExportDiagnosticsAsync(_ => Task.FromResult<Stream?>(null), TestContext.Current.CancellationToken);
        Assert.False(settings.IsExporting); Assert.True(settings.CanClose); client.Detach();
    }
    private sealed class InMemoryUpdater : IAppUpdater
    {
        public string CurrentVersion => "2026.1.0-workflow.dev";
        public bool IsInstalled => true;
        public bool FailRestart { get; init; }
        public int Restarts { get; private set; }
        public TaskCompletionSource? DownloadGate { get; init; }
        public bool FailDownload { get; set; }
        public string Notes { get; init; } = "Проверка интерфейса обновлений Workflow";
        public Task<AppUpdate?> CheckAsync(bool preview, CancellationToken cancellationToken) => Task.FromResult<AppUpdate?>(new("2026.1.0-workflow.next", Notes, 1));
        public async Task DownloadAsync(AppUpdate update, IProgress<int> progress, CancellationToken cancellationToken)
        {
            if (DownloadGate is { } gate) await gate.Task.WaitAsync(cancellationToken);
            if (FailDownload) throw new IOException("Synthetic download failure");
        }
        public void ScheduleRestart(AppUpdate update)
        { Restarts++; if (FailRestart) throw new IOException("Synthetic restart failure"); }
    }
    private static async Task ReadyAsync(WorkflowViewModel model)
    { while (model.IsBusy) await Task.Delay(10, TestContext.Current.CancellationToken); Pump(); }
    private static void Pump() => Dispatcher.UIThread.RunJobs();
    private static void Click(Window window, string name)
    {
        var control = window.FindControl<Control>(name)!;
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Pump();
    }
    private static void Frame(TopLevel window, string name)
    {
        if (Environment.GetEnvironmentVariable("JOURNAL_WORKFLOW_SCREENSHOTS") is not { } output) return;
        Directory.CreateDirectory(output); using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
        frame.Save(Path.Combine(output, name + ".png"), new PngBitmapEncoderOptions());
    }
}

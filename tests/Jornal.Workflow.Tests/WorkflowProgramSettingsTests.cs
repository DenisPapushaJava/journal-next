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
    public async Task BellAndGearUseOneSettingsWindowAndThemeKeepsIdentityAndPersonalLists(bool dark, int width)
    {
        using var f = new ProjectFixture(); await f.InitializeAsync();
        var preferences = new WorkflowPreferencesStore(Path.Combine(f.DirectoryPath, "preferences.json"));
        preferences.Update(p => p with { DarkTheme = dark, EntrySuggestions = new() { Managers = ["Личный менеджер"] } });
        var window = new WorkflowWindow(preferences) { DataContext = f.Model, Width = width, Height = 768 };
        try
        {
            window.Show(); await ReadyAsync(f.Model); Click(window, "UpdatesBell");
            var settings = Assert.IsType<WorkflowSettingsWindow>(Assert.Single(window.OwnedWindows));
            if (width == 1180) { settings.Width = 800; settings.Height = 580; Pump(); }
            var model = Assert.IsType<WorkflowSettingsViewModel>(settings.DataContext);
            Assert.Equal(2, model.SelectedSectionIndex); Assert.Same(f.Model.Updates, model.Updates);
            Assert.Equal(new[] { "Общие", "Хранилище", "Программа" }, settings.FindControl<ListBox>("WorkflowSettingsNavigation")!.Items.Cast<ListBoxItem>().Select(i => i.Content));
            Assert.True(model.IsUpdateSourceConfigured); Assert.Equal("https://github.com/DenisPapushaJava/journal-next", WorkflowIdentity.UpdatesRepository!.AbsoluteUri);
            Assert.True(settings.FindControl<Button>("OpenWorkflowReleases")!.IsEffectivelyVisible);
            Assert.Contains(WorkflowIdentity.Name, settings.FindControl<TextBlock>("WorkflowUpdateSetupHint")!.Text);
            Assert.False(settings.FindControl<Button>("CheckWorkflowUpdates")!.IsEffectivelyEnabled);
            Frame(settings, $"workflow-program-{width}-{(dark ? "dark" : "light")}");
            Click(window, "WorkflowSettings"); Assert.Same(settings, Assert.Single(window.OwnedWindows));
            Assert.Equal(0, model.SelectedSectionIndex);
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
            Click(window, "UpdatesBell");
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
        public Task<AppUpdate?> CheckAsync(bool preview, CancellationToken cancellationToken) => Task.FromResult<AppUpdate?>(new("2026.1.0-workflow.next", "Проверка интерфейса обновлений Workflow", 1));
        public Task DownloadAsync(AppUpdate update, IProgress<int> progress, CancellationToken cancellationToken) => Task.CompletedTask;
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

using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Headless;
using Jornal.Desktop.ViewModels;
using Jornal.Workflow.Core;
using Jornal.Workflow.Desktop;
using Jornal.Workflow.Infrastructure;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class WorkflowDatabaseConnectionTests
{
    [Fact]
    public async Task ConnectingUsesMdbPersistsOnlyLocalConnectionAndKeepsOriginalPrototype()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var original = await File.ReadAllBytesAsync(f.Store.FilePath, TestContext.Current.CancellationToken);
        var memory = new MemoryMdb(); var path = Path.Combine(f.DirectoryPath, "shared.mdb");
        var preferences = new WorkflowPreferencesStore(Path.Combine(f.DirectoryPath, "preferences.json"));
        var model = new WorkflowViewModel(f.Store, new(new WorkflowAppUpdater()), preferences: preferences, databases: memory);
        await model.ReloadCommand.ExecuteAsync(null);
        var settings = model.CreateGeneralSettings(); settings.SelectedSectionIndex = 1;
        await settings.ChooseDatabaseAsync(path, true, TestContext.Current.CancellationToken);
        Assert.Null(settings.Error); Assert.Contains("создана", settings.Status); Assert.Equal(path, settings.WorkspacePath);
        Assert.True(model.IsMdb); Assert.Empty(model.Orders); Assert.Null(model.CurrentEmployee);
        var saved = preferences.Load(); Assert.Equal(path, saved.DatabasePath); Assert.Equal(memory.Id, saved.DatabaseId); Assert.Null(saved.EmployeeId);
        Assert.Equal(original, await File.ReadAllBytesAsync(f.Store.FilePath, TestContext.Current.CancellationToken));
        settings.SelectedSectionIndex = 0; settings.ProfileName = "Свой технолог";
        await settings.SaveCommand.ExecuteAsync(null); Assert.Null(settings.Error); Assert.Equal("Свой технолог", model.ProfileName);
        Assert.Equal(memory.Book.Employees.Single().Id, preferences.Load().DatabaseEmployees![memory.Id.ToString("N")]);
        model.NewOrderCommand.Execute(null); model.NewManager = "Менеджер"; model.NewCustomer = "Новый заказчик";
        await model.CreateOrderCommand.ExecuteAsync(null); Assert.Null(model.Error); Assert.Single(memory.Book.Orders);
        Assert.Equal(original, await File.ReadAllBytesAsync(f.Store.FilePath, TestContext.Current.CancellationToken));
        var restarted = new WorkflowViewModel(new WorkflowMdbStore(saved.DatabasePath!, memory, saved.DatabaseId), new(new WorkflowAppUpdater()), preferences: preferences);
        await restarted.ReloadCommand.ExecuteAsync(null); Assert.Null(restarted.Error); Assert.Equal("Свой технолог", restarted.ProfileName); Assert.Single(restarted.Orders);
        model.Detach(); restarted.Detach();
    }
    [Fact]
    public async Task FailedConnectionAndDirtySettingsKeepTheSourceAndInput()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var preferences = new WorkflowPreferencesStore(Path.Combine(f.DirectoryPath, "preferences.json"));
        var memory = new MemoryMdb { FailOpen = true };
        var model = new WorkflowViewModel(f.Store, new(new WorkflowAppUpdater()), preferences: preferences, databases: memory);
        await model.ReloadCommand.ExecuteAsync(null); var settings = model.CreateGeneralSettings(); settings.SelectedSectionIndex = 1;
        var before = preferences.Load(); var path = Path.Combine(f.DirectoryPath, "shared.mdb");
        settings.ProjectRoot = Path.Combine(f.DirectoryPath, "unsaved"); await settings.ChooseDatabaseAsync(path, true, TestContext.Current.CancellationToken);
        Assert.Contains("настроек", settings.Error); Assert.True(settings.HasRootChanges); Assert.Empty(memory.Requests);
        settings.ProjectRoot = (await f.Store.ReadAsync(TestContext.Current.CancellationToken)).ProjectRoot!;
        await settings.ChooseDatabaseAsync(path, false, TestContext.Current.CancellationToken); Assert.Contains("недоступен", settings.Error);
        Assert.Equal(f.Store.FilePath, model.WorkspacePath); Assert.Equal(before, preferences.Load()); Assert.Single(model.Orders);
        model.Detach();
    }
    [Fact]
    public async Task ConfiguredMissingDatabaseDoesNotCreateOrReadPrototypeAndKeepsThePath()
    {
        using var f = new ProjectFixture(); var memory = new MemoryMdb { FailOpen = true };
        var preferences = new WorkflowPreferencesStore(Path.Combine(f.DirectoryPath, "preferences.json"));
        var path = Path.Combine(f.DirectoryPath, "missing.mdb");
        var transport = new MissingTransport();
        preferences.Save(new() { DatabasePath = path, DatabaseId = memory.Id });
        var model = new WorkflowViewModel(new WorkflowMdbStore(path, transport, memory.Id), new(new WorkflowAppUpdater()), preferences: preferences);
        await model.ReloadCommand.ExecuteAsync(null); Assert.Contains("не найден", model.Error);
        Assert.Equal(path, model.WorkspacePath); Assert.False(File.Exists(f.Store.FilePath)); Assert.Empty(model.Orders);
        Assert.All(transport.Requests, r => Assert.Equal(WorkflowAccessOperation.Inspect, r.Operation)); model.Detach();
    }
    [Fact]
    public async Task MissingConfiguredDatabaseCanBeReplacedThroughSettings()
    {
        using var f = new ProjectFixture(); var memory = new MemoryMdb();
        var preferences = new WorkflowPreferencesStore(Path.Combine(f.DirectoryPath, "preferences.json"));
        var oldPath = Path.Combine(f.DirectoryPath, "missing.mdb"); var oldId = Guid.NewGuid();
        preferences.Save(new() { DatabasePath = oldPath, DatabaseId = oldId });
        var model = new WorkflowViewModel(new WorkflowMdbStore(oldPath, new MissingTransport(), oldId), new(new WorkflowAppUpdater()),
            preferences: preferences, databases: memory);
        await model.ReloadCommand.ExecuteAsync(null); Assert.NotNull(model.Error);
        var target = Path.Combine(f.DirectoryPath, "new.mdb");
        Assert.Null(await model.ConnectDatabaseAsync(target, true, TestContext.Current.CancellationToken));
        Assert.Equal(target, model.WorkspacePath); Assert.Equal(memory.Id, preferences.Load().DatabaseId);
        Assert.False(File.Exists(f.Store.FilePath)); model.Detach();
    }
    private sealed class MissingTransport : IWorkflowAccessTransport
    {
        public List<WorkflowAccessRequest> Requests { get; } = [];
        public Task<WorkflowAccessResponse> SendAsync(WorkflowAccessRequest request, CancellationToken token = default)
        { Requests.Add(request); throw new WorkflowValidationException("Файл не найден; проверьте сеть."); }
    }
    [Fact]
    public async Task SourceSwitchDoesNotOverwriteAnotherInstancesConnectionSelection()
    {
        using var f = new ProjectFixture(); var preferences = new WorkflowPreferencesStore(Path.Combine(f.DirectoryPath, "preferences.json"));
        var memory = new MemoryMdb(); var model = new WorkflowViewModel(f.Store, new(new WorkflowAppUpdater()), preferences: preferences, databases: memory);
        await model.ReloadCommand.ExecuteAsync(null);
        preferences.Update(p => p with { DatabasePath = Path.Combine(f.DirectoryPath, "other.mdb"), DatabaseId = Guid.NewGuid() });
        var error = await model.ConnectDatabaseAsync(Path.Combine(f.DirectoryPath, "new.mdb"), false, TestContext.Current.CancellationToken);
        Assert.Contains("другом окне", error); Assert.Equal(f.Store.FilePath, model.WorkspacePath);
        Assert.EndsWith("other.mdb", preferences.Load().DatabasePath); model.Detach();
    }
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StorageExposesCreateAndConnectInExistingPageAndPlatformGate(bool dark)
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); f.Model.IsDarkTheme = dark;
        using var settings = f.Model.CreateGeneralSettings(); settings.SelectedSectionIndex = 1;
        var window = new WorkflowSettingsWindow { DataContext = settings };
        try
        {
            window.Show();
            var create = window.FindControl<Jornal.Desktop.Controls.JournalButton>("CreateWorkflowDatabase")!;
            var connect = window.FindControl<Jornal.Desktop.Controls.JournalButton>("ConnectWorkflowDatabase")!;
            Assert.True(create.IsVisible); Assert.True(connect.IsVisible);
            Assert.Equal(OperatingSystem.IsWindows(), create.IsEnabled); Assert.Equal(create.IsEnabled, connect.IsEnabled);
            Assert.Equal(f.Store.FilePath, window.FindControl<TextBox>("WorkflowWorkspacePath")!.Text);
            Assert.True(create.IsEffectivelyVisible); Assert.True(connect.IsEffectivelyVisible);
            if (Environment.GetEnvironmentVariable("JOURNAL_WORKFLOW_SCREENSHOTS") is { } output)
            {
                Directory.CreateDirectory(output);
                using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
                frame.Save(Path.Combine(output, $"workflow-mdb-storage-{(dark ? "dark" : "light")}.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            }
        }
        finally { window.Close(); }
    }
}

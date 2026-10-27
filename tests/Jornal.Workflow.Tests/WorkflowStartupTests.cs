using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Jornal.Workflow.Core;
using Jornal.Workflow.Desktop;
using Jornal.Workflow.Infrastructure;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class WorkflowStartupTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    [AvaloniaTheory]
    [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task FirstFrameHasLocalPreferencesAndProfileAndLateDatabaseCannotRevertUserTheme(bool dark, bool fail)
    {
        using var f = new ProjectFixture();
        var memory = new MemoryMdb { Book = WorkflowTestData.CreateWorkspace() };
        var employee = memory.Book.Employees[0] with { ColorArgb = 0xFF80FF80 }; memory.Book = memory.Book with { Employees = [employee] };
        var preferences = new WorkflowPreferencesStore(Path.Combine(f.DirectoryPath, "preferences.json"));
        var path = Path.Combine(f.DirectoryPath, "shared.mdb");
        var initial = new WorkflowPreferences(dark) { DatabasePath = path, DatabaseId = memory.Id, EmployeeId = employee.Id,
            EntrySuggestions = new() { Managers = ["Личный менеджер"], Customers = ["Личный заказчик"] }, ResultPricing = new(false, 700) };
        preferences.Save(initial);
        new WorkflowStartupProfileStore(preferences.ProfileCachePath).Save(new(1, path, memory.Id, employee.Id, employee.Name, employee.ColorArgb));
        var wire = new DelayedStartup(memory) { Fail = fail };
        var model = new WorkflowViewModel(new WorkflowMdbStore(path, wire, memory.Id), new(new WorkflowAppUpdater()), preferences: preferences, initialPreferences: initial);
        var window = new WorkflowWindow(preferences, initial);
        try
        {
            Assert.Equal(dark ? ThemeVariant.Dark : ThemeVariant.Light, window.RequestedThemeVariant);
            Assert.Equal(dark, model.IsDarkTheme); Assert.Equal(employee.Name, model.ProfileName); Assert.Null(model.CurrentEmployee);
            Assert.Equal(["Личный менеджер"], model.ManagerSuggestions); Assert.Equal(["Личный заказчик"], model.CustomerSuggestions);
            Assert.Equal(new WorkflowResultPricing(false, 700), model.ResultPricing);
            var transitions = new List<ThemeVariant>();
            window.PropertyChanged += (_, e) => { if (e.Property.Name == nameof(Window.ActualThemeVariant)) transitions.Add(window.ActualThemeVariant); };
            window.DataContext = model; window.Show(); await Until(() => wire.Started.Task.IsCompleted);
            var expected = dark ? ThemeVariant.Dark : ThemeVariant.Light;
            Assert.Equal(expected, window.ActualThemeVariant); Assert.All(transitions, t => Assert.Equal(expected, t));
            Assert.Equal(employee.Name, model.ProfileName); Assert.Equal(employee.ColorArgb, Assert.IsType<SolidColorBrush>(model.ProfileBrush).Color.ToUInt32());
            Assert.Null(model.CurrentEmployee); Assert.True(model.IsBusy); Assert.Empty(model.Orders);
            Assert.DoesNotContain(memory.Requests, r => r.Operation == WorkflowAccessOperation.Apply);
            SaveFrame(window, $"startup-first-frame-{dark}-{fail}");
            window.FindControl<ToggleButton>("ThemeSwitch")!.IsChecked = !dark; Pump();
            Assert.Equal(!dark, preferences.Load().DarkTheme);
            SaveFrame(window, $"startup-pending-{dark}-{fail}");
            wire.Release.TrySetResult(); await Until(() => !model.IsBusy);
            Assert.Equal(!dark, model.IsDarkTheme); Assert.Equal(!dark ? ThemeVariant.Dark : ThemeVariant.Light, window.ActualThemeVariant);
            Assert.Equal(employee.Name, model.ProfileName);
            if (fail) { Assert.NotNull(model.Error); Assert.Null(model.CurrentEmployee); Assert.Contains("ожидает подтверждения", model.ProfileHint); }
            else { Assert.Null(model.Error); Assert.Equal(employee, model.CurrentEmployee); Assert.DoesNotContain("ожидает подтверждения", model.ProfileHint); }
            SaveFrame(window, $"startup-finished-{dark}-{fail}");
        }
        finally { wire.Release.TrySetResult(); await Until(() => !model.IsBusy); window.Close(); model.Detach(); }
    }

    [Theory]
    [InlineData("employee")] [InlineData("database")] [InlineData("path")] [InlineData("schema")]
    public void ProfileCacheCannotImpersonateAnotherSourceOrEmployee(string mismatch)
    {
        using var f = new ProjectFixture(); var memory = new MemoryMdb(); var path = Path.Combine(f.DirectoryPath, "shared.mdb");
        var employee = Guid.NewGuid(); var prefs = new WorkflowPreferencesStore(Path.Combine(f.DirectoryPath, "preferences.json"));
        prefs.Save(new(true) { DatabasePath = path, DatabaseId = memory.Id, EmployeeId = employee });
        var cache = new WorkflowStartupProfile(1, path, memory.Id, employee, "Снимок", 0xFF80FF80);
        cache = mismatch switch { "employee" => cache with { EmployeeId = Guid.NewGuid() }, "database" => cache with { DatabaseId = Guid.NewGuid() },
            "path" => cache with { WorkspacePath = path + ".other" }, _ => cache with { Schema = 2 } };
        File.WriteAllText(prefs.ProfileCachePath, JsonSerializer.Serialize(cache));
        var model = new WorkflowViewModel(new WorkflowMdbStore(path, memory, memory.Id), new(new WorkflowAppUpdater()), preferences: prefs);
        try
        { Assert.True(model.IsDarkTheme); Assert.Equal("Загрузка профиля…", model.ProfileName); Assert.Null(model.CurrentEmployee); }
        finally { model.Detach(); }
    }

    [Fact]
    public async Task ConfirmedReadRefreshesOnlyCacheAndArchivedEmployeeClearsIt()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync();
        var preferences = new WorkflowPreferencesStore(Path.Combine(f.DirectoryPath, "preferences.json"));
        var employee = f.Model.CurrentEmployee!;
        var before = await File.ReadAllBytesAsync(Path.Combine(f.DirectoryPath, "preferences.json"), Token);
        var book = await f.Store.ReadAsync(Token);
        var updated = employee with { Name = "Новая фамилия", ColorArgb = 0xFFFF9DFF };
        await f.Store.WriteAsync(book.Version, b => b with { Employees = b.Employees.Select(e => e.Id == employee.Id ? updated : e).ToArray() }, Token);
        await f.Model.ReloadCommand.ExecuteAsync(null); Assert.Null(f.Model.Error);
        var cache = new WorkflowStartupProfileStore(preferences.ProfileCachePath).Load(); Assert.NotNull(cache);
        Assert.Equal(updated.Name, cache.Name); Assert.Equal(updated.ColorArgb, cache.ColorArgb);
        Assert.Equal(before, await File.ReadAllBytesAsync(Path.Combine(f.DirectoryPath, "preferences.json"), Token));
        book = await f.Store.ReadAsync(Token);
        await f.Store.WriteAsync(book.Version, b => b with { Employees = b.Employees.Select(e => e.Id == employee.Id ? e with { IsTechnologistArchived = true } : e).ToArray() }, Token);
        await f.Model.ReloadCommand.ExecuteAsync(null); Assert.Null(f.Model.CurrentEmployee); Assert.Equal("Профиль не выбран", f.Model.ProfileName);
        Assert.Null(new WorkflowStartupProfileStore(preferences.ProfileCachePath).Load());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task BrokenOrUnwritableCacheNeverBlocksLocalSettingsOrSuccessfulRead(bool directory)
    {
        using var f = new ProjectFixture(); await f.InitializeAsync();
        var prefs = new WorkflowPreferencesStore(Path.Combine(f.DirectoryPath, "preferences.json")); prefs.Update(p => p with { DarkTheme = true });
        File.Delete(prefs.ProfileCachePath);
        if (directory) Directory.CreateDirectory(prefs.ProfileCachePath); else await File.WriteAllTextAsync(prefs.ProfileCachePath, "{broken", Token);
        var model = new WorkflowViewModel(f.Store, new(new WorkflowAppUpdater()), preferences: prefs);
        try
        {
            Assert.True(model.IsDarkTheme); Assert.Equal("Загрузка профиля…", model.ProfileName);
            await model.ReloadCommand.ExecuteAsync(null); Assert.Null(model.Error); Assert.Equal(f.Model.CurrentEmployee, model.CurrentEmployee);
            Assert.Equal(f.Model.ProfileName, model.ProfileName);
        }
        finally { model.Detach(); }
    }

    [Fact]
    public async Task OptionalCacheFailureDoesNotTurnAConfirmedDatabaseReadIntoAnError()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync();
        var prefs = new WorkflowPreferencesStore(Path.Combine(f.DirectoryPath, "preferences.json"));
        File.Delete(prefs.ProfileCachePath);
        var model = new WorkflowViewModel(f.Store, new(new WorkflowAppUpdater()), preferences: prefs, initialPreferences: prefs.Load());
        try
        {
            // Settings may become unreadable while the database is loading. No write/retry may be caused by the cache.
            await File.WriteAllTextAsync(Path.Combine(f.DirectoryPath, "preferences.json"), JsonSerializer.Serialize(new WorkflowPreferences { EmployeeId = Guid.Empty }), Token);
            await model.ReloadCommand.ExecuteAsync(null);
            Assert.Null(model.Error); Assert.Equal(f.Model.CurrentEmployee, model.CurrentEmployee);
            Assert.False(File.Exists(prefs.ProfileCachePath));
        }
        finally { model.Detach(); }
    }

    private sealed class DelayedStartup(MemoryMdb memory) : IWorkflowAccessTransport
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Fail { get; set; }
        public async Task<WorkflowAccessResponse> SendAsync(WorkflowAccessRequest request, CancellationToken token = default)
        {
            Started.TrySetResult(); await Release.Task.WaitAsync(token);
            if (Fail) throw new IOException("Database unavailable");
            return await memory.SendAsync(request, token);
        }
    }
    private static void Pump() => Dispatcher.UIThread.RunJobs();
    private static async Task Until(Func<bool> done)
    { for (var i = 0; i < 500 && !done(); i++) { await Task.Delay(10, Token); Pump(); } Pump(); Assert.True(done()); }
    private static void SaveFrame(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("JOURNAL_WORKFLOW_SCREENSHOTS") is not { } output) return;
        Directory.CreateDirectory(output); using var frame = window.CaptureRenderedFrame(); frame?.Save(Path.Combine(output, name + ".png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }
}

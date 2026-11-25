using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using System.Net;
using Jornal.Workflow.Core;
using Jornal.Workflow.Desktop;
using Jornal.Workflow.Infrastructure;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed partial class WorkflowOfficeStorageTests
{
    [Fact]
    public async Task FolderOpeningUsesOwnedCopyDuringAnOutageAndKeepsBothVersionsOnReconnect()
    {
        using var fixture = new WorkingFiles();
        var token = TestContext.Current.CancellationToken;
        const string name = "Проект.rcam";
        fixture.Office.Add(name, "office");
        var copy = await fixture.OpenAsync(token);
        var local = Path.Combine(copy.Folder, name);
        await File.WriteAllTextAsync(local, "home", token);
        fixture.Offline = true;

        var offline = await fixture.Client.OpenWorkingCopyAsync(fixture.Order, fixture.Project, token, allowOffline: true);
        Assert.True(offline.IsOffline);
        Assert.Equal(copy.Folder, offline.Folder);
        Assert.Null(offline.HasPendingFiles);
        Assert.Equal("home", await File.ReadAllTextAsync(local, token));
        await Assert.ThrowsAsync<WorkflowValidationException>(() => fixture.OpenAsync(token));
        await Assert.ThrowsAsync<WorkflowValidationException>(() =>
            fixture.Client.OpenWorkingCopyAsync(fixture.Order, Guid.NewGuid(), token, allowOffline: true));

        fixture.Office[name] = "new office";
        fixture.Offline = false;
        var fresh = await fixture.Client.OpenWorkingCopyAsync(fixture.Order, fixture.Project, token, allowOffline: true);
        Assert.False(fresh.IsOffline);
        Assert.Equal(name, Assert.Single(fresh.Conflicts).Path);
        Assert.Equal("home", await File.ReadAllTextAsync(local, token));
        Assert.Equal("new office", fixture.Office[name]);
        Assert.Empty(fixture.Uploads);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task FolderOpeningDoesNotTreatPermissionOrMissingOfficeProjectAsConnectionLoss(HttpStatusCode status)
    {
        using var fixture = new WorkingFiles();
        var token = TestContext.Current.CancellationToken;
        fixture.Office.Add("Проект.rcam", "office");
        var copy = await fixture.OpenAsync(token);
        fixture.ManifestStatus = status;
        var error = await Record.ExceptionAsync(() =>
            fixture.Client.OpenWorkingCopyAsync(fixture.Order, fixture.Project, token, allowOffline: true));
        Assert.NotNull(error);
        Assert.IsNotType<WorkflowOfficeConnectionLostException>(error);
        Assert.True(Directory.Exists(copy.Folder));
        Assert.Empty(fixture.Uploads);
    }

    [AvaloniaTheory]
    [InlineData(false, "кнопка")]
    [InlineData(true, "Enter")]
    [InlineData(false, "двойной щелчок")]
    [InlineData(true, "папка подменена")]
    [InlineData(false, "копий нет")]
    [InlineData(false, "ключ отклонён")]
    public async Task OfflineStartupOffersDownloadedFoldersOnlyWhenTheJournalCannotConnect(bool dark, string scenario)
    {
        using var fixture = new WorkingFiles();
        using var log = new WorkflowWindowTests.BindingLog();
        var token = TestContext.Current.CancellationToken;
        fixture.Office.Add("Проект.rcam", "office");
        const string native = @"D:\Проекты\2026\11\24\2026-11-24-150000-0123abcd Заказчик\6 мм -- ЛАЗЕР\Проект.rcam";
        var folder = scenario == "копий нет" ? null :
            (await fixture.Client.OpenWorkingCopyAsync(fixture.Order, fixture.Project, token, nativePath: native)).Folder;
        var database = new MemoryMdb();
        var offline = true;
        var requests = 0;
        fixture.ReadDatabase = request =>
        {
            requests++;
            if (offline) throw new HttpRequestException("Связь с офисом потеряна.");
            return database.SendAsync(request with { DatabaseId = database.Id }, token).GetAwaiter().GetResult()
                with { DatabaseId = fixture.Client.Settings.DatabaseId };
        };
        if (scenario == "ключ отклонён") fixture.DatabaseStatus = HttpStatusCode.Unauthorized;
        var prefs = new WorkflowPreferences(dark) { OfficeStorage = fixture.Client.Settings, UseOfficeStorage = true };
        var preferences = new WorkflowPreferencesStore(Path.Combine(fixture.DirectoryPath, "preferences.json"));
        preferences.Save(prefs);
        var model = new WorkflowViewModel(fixture.Client.CreateStore(), new(new WorkflowAppUpdater()), preferences: preferences,
            initialPreferences: prefs, remoteTransport: fixture.Client);
        var window = new OfflineFolderTestWindow(preferences, prefs) { DataContext = model };
        try
        {
            window.Show();
            await WaitUntil(() => !model.IsBusy && model.HasError);
            var button = window.FindControl<Button>("OfflineOfficeFoldersButton")!;
            if (scenario is "копий нет" or "ключ отклонён")
            {
                Assert.False(button.IsEffectivelyVisible);
                Assert.Contains(scenario == "копий нет" ? "пока нет скачанных папок" : "Ключ подключения истёк", model.Error);
            }
            else
            {
                Assert.True(button.IsEffectivelyVisible);
                Assert.Contains("Нет связи с офисом", model.Error);
                Assert.True(model.IsRemoteStorage);
                var before = requests;
                Click(window, "OfflineOfficeFoldersButton");
                await WaitUntil(() => window.OwnedWindows.OfType<WorkflowOfflineFoldersWindow>().Any());
                var dialog = Assert.Single(window.OwnedWindows.OfType<WorkflowOfflineFoldersWindow>());
                var list = dialog.FindControl<ListBox>("OfflineFoldersList")!;
                var item = Assert.IsType<WorkflowOfflineFolder>(Assert.Single(list.Items));
                Assert.Equal("6 мм -- ЛАЗЕР", item.ProjectName);
                Assert.Equal("2026-11-24-150000-0123abcd Заказчик", item.OrderName);
                Assert.Equal(folder, item.Path);
                if (scenario == "папка подменена")
                {
                    var owner = Path.Combine(folder!, ".office-copy-owner");
                    File.Delete(owner);
                    File.WriteAllText(owner, "другой проект");
                }
                if (scenario == "Enter")
                {
                    await WaitUntil(() => list.IsKeyboardFocusWithin);
                    dialog.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
                }
                else if (scenario == "двойной щелчок")
                {
                    await WaitUntil(() => list.ContainerFromIndex(0) is { Bounds.Width: > 0 });
                    var row = list.ContainerFromIndex(0)!;
                    var point = row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), dialog)!.Value;
                    dialog.MouseDown(point, MouseButton.Left);
                    dialog.MouseUp(point, MouseButton.Left);
                    dialog.MouseDown(point, MouseButton.Left);
                    if (dialog.IsVisible) dialog.MouseUp(point, MouseButton.Left);
                }
                else Click(dialog, "OpenOfflineFolder");
                await WaitUntil(() => window.OwnedWindows.Count == 0 &&
                    (window.OpenedFolder is not null || model.Error?.Contains("папку открыть не удалось", StringComparison.Ordinal) == true));
                Assert.Equal(scenario == "папка подменена" ? null : folder, window.OpenedFolder);
                Assert.Equal(before, requests);
                Assert.Empty(fixture.Uploads);
            }
            offline = false;
            fixture.DatabaseStatus = null;
            await model.ReloadCommand.ExecuteAsync(null);
            Assert.Null(model.Error);
            Assert.False(button.IsEffectivelyVisible);
            Assert.True(model.IsRemoteStorage);
            Assert.Empty(log.Errors);
        }
        finally { WorkflowTestWindows.Close(window); }
    }

    [Fact]
    public async Task DownloadedFolderIndexRejectsForeignCopiesAndRetainsUsableLegacyNames()
    {
        using var fixture = new WorkingFiles();
        var token = TestContext.Current.CancellationToken;
        fixture.Office.Add("4 мм ЛАЗЕР.rcam", "office");
        var normal = await fixture.OpenAsync(token);
        var id = Guid.NewGuid();
        var custom = Directory.CreateDirectory(Path.Combine(fixture.DirectoryPath, "Локальные копии")).FullName;
        var foreign = await fixture.Client.OpenWorkingCopyAsync(fixture.Order, id, token, custom);
        // Preserve the recorded path while simulating a folder later taken over by a different database.
        var owner = Path.Combine(foreign.Folder, ".office-copy-owner");
        File.Delete(owner);
        await File.WriteAllTextAsync(owner, $"{Guid.NewGuid():N}/{id:N}", token);
        var otherDatabase = Guid.NewGuid();
        var misplaced = Directory.CreateDirectory(Path.Combine(fixture.DirectoryPath, "office-projects", otherDatabase.ToString("N"), fixture.Project.ToString("N")));
        await File.WriteAllTextAsync(Path.Combine(misplaced.FullName, ".office-copy.json"), "{}", token);
        await File.WriteAllTextAsync(Path.Combine(misplaced.FullName, ".office-copy-owner"), $"{otherDatabase:N}/{fixture.Project:N}", token);
        fixture.Offline = true;
        var result = fixture.Client.ListDownloadedFolders(token);
        var selected = Assert.Single(result.Items);
        Assert.True(result.Incomplete);
        Assert.Equal("4 мм ЛАЗЕР", selected.ProjectName);
        Assert.Null(selected.OrderName);
        Assert.Equal(normal.Folder, fixture.Client.VerifyDownloadedFolder(selected));
        Assert.Null(fixture.Client.VerifyDownloadedFolder(selected with { Path = misplaced.FullName }));
        Assert.Empty(fixture.Uploads);
    }

    private sealed class OfflineFolderTestWindow(WorkflowPreferencesStore store, WorkflowPreferences prefs) : WorkflowWindow(store, prefs)
    {
        internal string? OpenedFolder { get; private set; }
        protected override Task<bool> OpenFolderAsync(string path)
        {
            OpenedFolder = path;
            return Task.FromResult(Directory.Exists(path));
        }
    }
}

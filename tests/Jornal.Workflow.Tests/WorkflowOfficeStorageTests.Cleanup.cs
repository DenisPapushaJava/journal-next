using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Jornal.Workflow.Core;
using Jornal.Workflow.Desktop;
using Jornal.Workflow.Infrastructure;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed partial class WorkflowOfficeStorageTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CleanupFromRemoteStorageSettingsWaitsForConfirmation(bool confirm)
    {
        using var fixture = new WorkingFiles();
        using var log = new WorkflowWindowTests.BindingLog();
        var token = TestContext.Current.CancellationToken;
        var order = CleanupOrder(fixture);
        var database = fixture.Client.Settings.DatabaseId;
        fixture.ReadDatabase = request => new(1, database, Orders: [order], PageIds: [order.Id],
            Catalog: request.KnownCatalogVersion is null ? WorkflowCatalog.From(WorkflowWorkspace.Empty) : null);
        fixture.Office.Add("Проект.rcam", "office");
        var copy = await fixture.OpenAsync(token);
        var prefs = new WorkflowPreferences(confirm) { OfficeStorage = fixture.Client.Settings, UseOfficeStorage = true };
        var preferences = new WorkflowPreferencesStore(Path.Combine(fixture.DirectoryPath, "preferences.json"));
        preferences.Save(prefs);
        var journal = new WorkflowViewModel(fixture.Client.CreateStore(), new(new WorkflowAppUpdater()), preferences: preferences,
            initialPreferences: prefs, remoteTransport: fixture.Client);
        var model = journal.CreateGeneralSettings();
        model.SelectedSectionIndex = 1;
        var window = new WorkflowSettingsWindow { DataContext = model };
        try
        {
            window.Show();
            if (!OperatingSystem.IsWindows())
            {
                Dispatcher.UIThread.RunJobs();
                Assert.False(window.FindControl<Button>("FreeOfficeCopySpace")!.IsEffectivelyVisible);
                Click(window, "FreeOfficeCopySpace");
                Assert.Empty(window.OwnedWindows);
                Assert.True(Directory.Exists(copy.Folder));
                return;
            }
            Click(window, "FreeOfficeCopySpace");
            var dialog = Assert.IsType<WorkflowOfficeCleanupWindow>(Assert.Single(window.OwnedWindows));
            var action = dialog.FindControl<Button>("ConfirmCleanup")!;
            await WaitUntil(() => action.IsEffectivelyVisible);
            Assert.True(Directory.Exists(copy.Folder));
            if (confirm)
            {
                Click(dialog, "ConfirmCleanup");
                await WaitUntil(() => dialog.FindControl<TextBlock>("CleanupSummary")!.Text?.StartsWith("Удалено локальных копий: 1.", StringComparison.Ordinal) == true);
            }
            Click(dialog, "CancelCleanup");
            await WaitUntil(() => window.OwnedWindows.Count == 0);
            Assert.Equal(!confirm, Directory.Exists(copy.Folder));
            Assert.Empty(fixture.Uploads);
            Assert.Empty(log.Errors);
        }
        finally
        {
            foreach (var child in window.OwnedWindows.ToArray()) child.Close();
            window.Close();
            journal.Detach();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CleanupRemovesOnlyConfirmedLocalCopyAndOpeningDownloadsItAgain(bool custom)
    {
        using var fixture = new WorkingFiles();
        var token = TestContext.Current.CancellationToken;
        var order = CleanupOrder(fixture);
        var root = custom ? Directory.CreateDirectory(Path.Combine(fixture.DirectoryPath, "Домашние проекты")).FullName : null;
        const string name = "Проект.rcam";
        fixture.Office.Add(name, "office");
        var copy = await fixture.Client.OpenWorkingCopyAsync(order.Id, fixture.Project, token, root, order.Projects[0].NativePath);
        if (custom && OperatingSystem.IsWindows())
        {
            var index = Path.Combine(fixture.DirectoryPath, "office-projects", fixture.Client.Settings.DatabaseId.ToString("N"), fixture.Project.ToString("N"));
            Assert.True(File.GetAttributes(index + ".location").HasFlag(FileAttributes.Hidden));
            Assert.True(File.GetAttributes(index + ".lock").HasFlag(FileAttributes.Hidden));
        }
        var other = Directory.CreateDirectory(Path.Combine(fixture.DirectoryPath, "office-projects",
            Guid.NewGuid().ToString("N"), fixture.Project.ToString("N")));
        var unrelated = Path.Combine(other.FullName, name);
        await File.WriteAllTextAsync(unrelated, "Другая база", token);

        var plan = await fixture.Client.PreviewCleanupAsync([order], new HashSet<Guid>(), null, null, token);
        Assert.Equal(6, Assert.Single(plan.Copies).Bytes);
        Assert.Empty(plan.Skipped);
        Assert.True(File.Exists(Path.Combine(copy.Folder, name)));

        var result = await fixture.Client.RemoveCleanCopiesAsync(plan, new HashSet<Guid>(), null, null, token);
        Assert.Empty(result.Skipped);
        Assert.Equal(1, result.Removed);
        Assert.Equal(6, result.Bytes);
        Assert.False(Directory.Exists(copy.Folder));
        Assert.Equal("office", fixture.Office[name]);
        Assert.Empty(fixture.Uploads);
        Assert.Equal("Другая база", await File.ReadAllTextAsync(unrelated, token));

        fixture.Office[name] = "new office version";
        var reopened = await fixture.Client.OpenWorkingCopyAsync(order.Id, fixture.Project, token, root, order.Projects[0].NativePath);
        Assert.Equal(copy.Folder, reopened.Folder);
        Assert.Equal("new office version", await File.ReadAllTextAsync(Path.Combine(reopened.Folder, name), token));
    }

    [Theory]
    [InlineData("добавлен файл", "последней синхронизации")]
    [InlineData("изменён файл", "есть изменения")]
    [InlineData("удалён файл", "удалены файлы")]
    [InlineData("черновик на диске", "черновики")]
    [InlineData("черновик в карточке", "черновик расчёта")]
    [InlineData("сохранённая версия", "версии для восстановления")]
    [InlineData("заблокирован файл", "Закройте открытые файлы")]
    [InlineData("офисная версия изменилась", "Офисная копия отличается")]
    [InlineData("общая папка", "отдельной локальной папки")]
    [InlineData("папка цеха", "отдельной локальной папки")]
    [InlineData("чужая папка", "проект недоступен в офисе")]
    public async Task CleanupSkipsCopiesThatCannotBeSafelyDownloadedAgain(string scenario, string reason)
    {
        using var fixture = new WorkingFiles();
        var token = TestContext.Current.CancellationToken;
        var order = CleanupOrder(fixture);
        const string name = "Проект.rcam";
        fixture.Office.Add(name, "office");
        var (folder, _, _) = await fixture.OpenAsync(token);
        var path = Path.Combine(folder, name);
        var extra = scenario switch
        {
            "добавлен файл" => Path.Combine(folder, "Новая УП.nc"),
            "черновик на диске" => Path.Combine(folder, ".workflow-history", "draft.pdf"),
            "сохранённая версия" => Path.Combine(folder, ".office-sync", "previous.pdf"),
            _ => null
        };
        if (extra is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(extra)!);
            await File.WriteAllTextAsync(extra, "Только дома", token);
        }
        if (scenario == "изменён файл") await File.WriteAllTextAsync(path, "home", token);
        if (scenario == "удалён файл") File.Delete(path);
        if (scenario == "офисная версия изменилась") fixture.Office[name] = "updated";
        if (scenario == "чужая папка")
            fixture.ReadDatabase = request => new(1, fixture.Client.Settings.DatabaseId, Orders: [],
                Catalog: request.KnownCatalogVersion is null ? WorkflowCatalog.From(WorkflowWorkspace.Empty) : null);
        using var locked = scenario == "заблокирован файл" ? File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None) : null;
        var protectedProjects = scenario == "черновик в карточке" ? new HashSet<Guid> { fixture.Project } : [];
        var plan = await fixture.Client.PreviewCleanupAsync([order], protectedProjects,
            scenario == "общая папка" ? Path.GetDirectoryName(folder) : null,
            scenario == "папка цеха" ? Path.GetDirectoryName(folder) : null, token);

        Assert.Empty(plan.Copies);
        Assert.Contains(reason, Assert.Single(plan.Skipped));
        Assert.True(Directory.Exists(folder));
        Assert.Empty(fixture.Uploads);
        if (extra is not null) Assert.Equal("Только дома", await File.ReadAllTextAsync(extra, token));
    }

    [Theory]
    [InlineData("новый файл")]
    [InlineData("правка")]
    [InlineData("нет связи")]
    [InlineData("офисная правка")]
    [InlineData("занята после сверки")]
    public async Task CleanupRechecksAfterConfirmationAndKeepsTheCopyIfSomethingChanged(string scenario)
    {
        using var fixture = new WorkingFiles();
        var token = TestContext.Current.CancellationToken;
        var order = CleanupOrder(fixture);
        const string name = "Проект.rcam";
        fixture.Office.Add(name, "office");
        var root = Directory.CreateDirectory(Path.Combine(fixture.DirectoryPath, "Дом")).FullName;
        var copy = await fixture.Client.OpenWorkingCopyAsync(order.Id, fixture.Project, token, root, order.Projects[0].NativePath);
        var plan = await fixture.Client.PreviewCleanupAsync([order], new HashSet<Guid>(), null, null, token);
        Assert.Single(plan.Copies);
        var path = Path.Combine(copy.Folder, scenario == "новый файл" ? "Добавлено.nc" : name);
        if (scenario == "нет связи") fixture.ManifestUnavailable = true;
        else if (scenario == "офисная правка") fixture.Office[name] = "Только в офисе";
        else if (scenario != "занята после сверки") await File.WriteAllTextAsync(path, "Только дома", token);

        WorkflowOfficeCleanupResult result;
        using (var open = scenario == "занята после сверки" ? File.Open(path, FileMode.Open, FileAccess.Write,
            OperatingSystem.IsWindows() ? FileShare.ReadWrite | FileShare.Delete : FileShare.None) : null)
            result = await fixture.Client.RemoveCleanCopiesAsync(plan, new HashSet<Guid>(), null, null, token);

        Assert.Equal(0, result.Removed);
        Assert.Single(result.Skipped);
        Assert.Equal(scenario is "новый файл" or "правка" ? "Только дома" : "office", await File.ReadAllTextAsync(path, token));
        Assert.Empty(Directory.EnumerateDirectories(Path.GetDirectoryName(copy.Folder)!, ".office-cleanup-*"));
        Assert.Empty(fixture.Uploads);
    }

    [Theory]
    [InlineData(false, "после перемещения")]
    [InlineData(true, "до перемещения")]
    [InlineData(true, "после перемещения")]
    [InlineData(true, "после перемещения в контейнер")]
    [InlineData(false, "проверка после перемещения")]
    [InlineData(true, "частично удалены")]
    [InlineData(true, "удалены служебные файлы")]
    [InlineData(true, "папка удалена")]
    [InlineData(true, "индекс удалён")]
    [InlineData(true, "место занято")]
    public async Task InterruptedCleanupRestoresFilesOrFinalizesTheIndex(bool custom, string scenario)
    {
        using var fixture = new WorkingFiles();
        var token = TestContext.Current.CancellationToken;
        var order = CleanupOrder(fixture);
        var project = Assert.Single(order.Projects);
        const string name = "Проект.rcam";
        fixture.Office.Add(name, "office");
        var root = custom ? Directory.CreateDirectory(Path.Combine(fixture.DirectoryPath, "Дом")).FullName : null;
        var copy = await fixture.Client.OpenWorkingCopyAsync(order.Id, project.Id, token, root, project.NativePath);
        var index = Path.Combine(fixture.DirectoryPath, "office-projects", fixture.Client.Settings.DatabaseId.ToString("N"), project.Id.ToString("N"));
        var journal = index + ".cleanup.json";
        var location = index + ".location";
        var operation = Guid.NewGuid();
        var inContainer = scenario == "после перемещения в контейнер";
        var container = Path.Combine(Path.GetDirectoryName(copy.Folder)!, ".office-cleanup-" + operation.ToString("N"));
        var detached = inContainer ? Path.Combine(container, "copy") : container;
        if (inContainer)
        {
            Directory.CreateDirectory(container);
            WorkflowServiceFiles.TryHide(container);
        }
        var removed = scenario is "удалены служебные файлы" or "папка удалена" or "индекс удалён";
        var move = inContainer
            ? JsonSerializer.SerializeToUtf8Bytes(new { folder = copy.Folder, operation, inContainer, workingFilesRemoved = removed })
            : JsonSerializer.SerializeToUtf8Bytes(new { folder = copy.Folder, operation, workingFilesRemoved = removed });
        await File.WriteAllBytesAsync(journal, move, token);
        var pending = "Домашняя правка.nc";
        var keepsEdit = scenario is not ("папка удалена" or "индекс удалён");
        if (keepsEdit) await File.WriteAllTextAsync(Path.Combine(copy.Folder, pending), "Изменено дома", token);
        if (scenario != "до перемещения") Directory.Move(copy.Folder, detached);
        if (scenario == "частично удалены") File.Delete(Path.Combine(detached, name));
        if (scenario == "удалены служебные файлы")
        {
            File.Delete(Path.Combine(detached, name));
            File.Delete(Path.Combine(detached, ".office-copy-owner"));
            File.Delete(Path.Combine(detached, ".office-copy.json"));
        }
        if (scenario is "папка удалена" or "индекс удалён") Directory.Delete(detached, recursive: true);
        if (scenario == "индекс удалён") File.Delete(location);

        if (scenario == "место занято")
        {
            Directory.CreateDirectory(copy.Folder);
            await File.WriteAllTextAsync(Path.Combine(copy.Folder, pending), "Другой файл", token);
            var conflict = await Assert.ThrowsAsync<WorkflowValidationException>(() =>
                fixture.Client.OpenWorkingCopyAsync(order.Id, project.Id, token, root, project.NativePath));
            Assert.Contains(detached, conflict.Message);
            Assert.Contains(copy.Folder, conflict.Message);
            Assert.Equal("Другой файл", await File.ReadAllTextAsync(Path.Combine(copy.Folder, pending), token));
            Assert.Equal("Изменено дома", await File.ReadAllTextAsync(Path.Combine(detached, pending), token));
            Assert.True(File.Exists(journal));
            return;
        }

        if (scenario == "проверка после перемещения")
        {
            var plan = await fixture.Client.PreviewCleanupAsync([order], new HashSet<Guid>(), null, null, token);
            Assert.Empty(plan.Copies);
            Assert.Contains("последней синхронизации", Assert.Single(plan.Skipped));
        }
        var reopened = await fixture.Client.OpenWorkingCopyAsync(order.Id, project.Id, token, root, project.NativePath);
        Assert.False(Directory.Exists(container));
        if (OperatingSystem.IsWindows()) Assert.False(File.GetAttributes(reopened.Folder).HasFlag(FileAttributes.Hidden));
        Assert.Equal(copy.Folder, reopened.Folder);
        Assert.Equal("office", await File.ReadAllTextAsync(Path.Combine(reopened.Folder, name), token));
        if (keepsEdit) Assert.Equal("Изменено дома", await File.ReadAllTextAsync(Path.Combine(reopened.Folder, pending), token));
        Assert.False(Directory.Exists(detached));
        Assert.False(File.Exists(journal));
        Assert.Empty(fixture.Uploads);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task CleanupStopsOnTheFirstSharedFailureAndKeepsPartialResults(bool duringPreview, bool expired)
    {
        using var fixture = new WorkingFiles();
        var token = TestContext.Current.CancellationToken;
        var settings = fixture.Client.Settings;
        var order = CleanupOrder(fixture);
        order = order with { Projects = Enumerable.Range(0, 3).Select(_ => order.Projects[0] with { Id = Guid.NewGuid() }).ToArray() };
        var remaining = int.MaxValue;
        var attempts = 0;
        var contents = "office"u8.ToArray();
        var files = new[] { new { path = "Проект.rcam", length = contents.Length, sha256 = Convert.ToHexString(SHA256.HashData(contents)) } };
        using var http = new HttpClient(new Handler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            HttpResponseMessage response;
            if (path.EndsWith("/database", StringComparison.Ordinal))
                response = new(HttpStatusCode.OK) { Content = JsonContent.Create(new WorkflowAccessResponse(1, settings.DatabaseId, Orders: [order])) };
            else if (path.EndsWith("/files", StringComparison.Ordinal))
            {
                attempts++;
                if (--remaining >= 0)
                    response = new(HttpStatusCode.OK) { Content = JsonContent.Create(new { directories = Array.Empty<string>(), files }) };
                else if (expired) response = new(HttpStatusCode.Unauthorized);
                else throw new HttpRequestException("Office connection lost.");
            }
            else
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                response = new(HttpStatusCode.OK) { Content = new ByteArrayContent(contents) };
            }
            return Task.FromResult(response);
        }));
        var client = new WorkflowOfficeStorageClient(settings, StorageToken, fixture.DirectoryPath, http, http);
        foreach (var project in order.Projects) await client.OpenWorkingCopyAsync(order.Id, project.Id, token);
        Task<WorkflowOfficeCleanupPlan> Preview() => client.PreviewCleanupAsync([order], new HashSet<Guid>(), null, null, token);
        if (duringPreview)
        {
            remaining = 1;
            attempts = 0;
            await Assert.ThrowsAnyAsync<WorkflowOfficeSyncUnavailableException>(Preview);
            Assert.Equal(2, attempts);
            return;
        }
        var plan = await Preview();
        remaining = 1;
        attempts = 0;
        var result = await client.RemoveCleanCopiesAsync(plan, new HashSet<Guid>(), null, null, token);
        Assert.Equal(2, attempts);
        Assert.Equal(1, result.Removed);
        Assert.Equal(6, result.Bytes);
        Assert.True(result.Stopped);
        Assert.Contains(expired ? "истёк" : "недоступен", result.StopReason);
        Assert.Empty(result.Skipped);
        Assert.False(Directory.Exists(plan.Copies[0].Folder));
        Assert.All(plan.Copies.Skip(1), copy => Assert.True(Directory.Exists(copy.Folder)));
    }

    [AvaloniaFact]
    public void CleanupExplainsHowToReconnectWhenSavedKeyIsMissing()
    {
        using var fixture = new WorkingFiles();
        var remote = fixture.Client.Settings;
        var prefs = new WorkflowPreferences { OfficeStorage = remote, UseOfficeStorage = true };
        var preferences = new WorkflowPreferencesStore(Path.Combine(fixture.DirectoryPath, "preferences.json"));
        preferences.Save(prefs);
        var journal = new WorkflowViewModel(new WorkflowUnavailableStore(fixture.DirectoryPath, "Нет сохранённого ключа."),
            new(new WorkflowAppUpdater()), preferences: preferences, initialPreferences: prefs);
        var model = journal.CreateGeneralSettings();
        model.SelectedSectionIndex = 1;
        var window = new WorkflowSettingsWindow { DataContext = model };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("Настроить подключение", journal.OfficeCleanupUnavailableReason);
            if (OperatingSystem.IsWindows())
            {
                Click(window, "FreeOfficeCopySpace");
                Assert.Equal(journal.OfficeCleanupUnavailableReason, model.Error);
                Assert.Empty(window.OwnedWindows);
            }
        }
        finally { window.Close(); journal.Detach(); }
    }

    private static WorkflowOrder CleanupOrder(WorkingFiles fixture)
    {
        var now = DateTimeOffset.UtcNow;
        var order = new WorkflowOrder(fixture.Order, 1, Guid.Empty, Guid.Empty, Guid.Empty, null, 1, now, now, [])
        {
            Projects = [new(fixture.Project, Guid.NewGuid(), 1, Guid.Empty, 4m, "Проект", @"D:\Projects\2026\11\16\Заявка\Проект\Проект.rcam", null, now)]
        };
        fixture.ReadDatabase = request =>
        {
            Assert.Equal(WorkflowAccessOperation.ReadOrders, request.Operation);
            Assert.Equal(order.Id, Assert.Single(request.OrderIds!));
            return new(1, fixture.Client.Settings.DatabaseId, Orders: [order]);
        };
        return order;
    }
}

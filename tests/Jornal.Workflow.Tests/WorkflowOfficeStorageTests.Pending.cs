using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.Threading;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Jornal.Workflow.Core;
using Jornal.Workflow.Desktop;
using Jornal.Workflow.Infrastructure;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed partial class WorkflowOfficeStorageTests
{
    [AvaloniaTheory]
    [InlineData("отправить", false)]
    [InlineData("конфликт", true)]
    [InlineData("закрыть без отправки", true)]
    [InlineData("остановить", false)]
    [InlineData("нет правок", false)]
    [InlineData("пропустить сверку", true)]
    [InlineData("отменить выход", false)]
    [InlineData("проверка из уведомления", true)]
    public async Task RemoteExitOffersCopiesOutsideTheFilterAndKeepsFilesOnFailure(string scenario, bool dark)
    {
        using var fixture = new WorkingFiles();
        using var log = new WorkflowWindowTests.BindingLog();
        var token = TestContext.Current.CancellationToken;
        var remote = fixture.Client.Settings;
        var other = new Guid("00000000-0000-0000-0000-000000000001");
        fixture.Office.Add("Раскрой.nc", "Офис");
        var defaults = await fixture.OpenAsync(token);
        var customRoot = Directory.CreateDirectory(Path.Combine(fixture.DirectoryPath, "Домашние проекты")).FullName;
        var custom = await fixture.Client.OpenWorkingCopyAsync(fixture.Order, other, token, customRoot,
            @"D:\Projects\2026\11\16\Заявка\Проект\Проект.rcam");
        if (scenario != "нет правок")
        {
            await File.WriteAllTextAsync(Path.Combine(custom.Folder, "Раскрой.nc"), "Исправлено дома", token);
            await File.WriteAllTextAsync(Path.Combine(defaults.Folder, "Новая УП.nc"), "Добавлено дома", token);
        }
        if (scenario == "конфликт") fixture.Office["Раскрой.nc"] = "Исправлено в офисе";

        var unrelated = Path.Combine(fixture.DirectoryPath, "office-projects", Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(unrelated);
        await File.WriteAllTextAsync(Path.Combine(unrelated, ".office-copy.json"), "{}", token);
        await File.WriteAllTextAsync(Path.Combine(unrelated, "Чужой файл.txt"), "Другая база", token);

        var today = DateTimeOffset.UtcNow;
        var head = new WorkflowOrder(Guid.NewGuid(), 2, Guid.Empty, Guid.Empty, Guid.Empty, null, 1, today, today, []);
        var work = head with { Id = fixture.Order, Number = 1, UpdatedAt = today.AddDays(-1), Projects =
        [
            new(other, Guid.NewGuid(), 1, Guid.Empty, 4m, "Старый проект", "first.rcam", null, today),
            new(fixture.Project, Guid.NewGuid(), 1, Guid.Empty, 6m, "Второй проект", "second.rcam", null, today)
        ] };
        var lookup = false;
        var archivedPages = 0;
        fixture.ReadDatabase = request =>
        {
            var empty = new WorkflowAccessResponse(1, remote.DatabaseId, Orders: [], PageIds: [],
                Catalog: request.KnownCatalogVersion is null ? WorkflowCatalog.From(WorkflowWorkspace.Empty) : null);
            if (!lookup || request.Operation != WorkflowAccessOperation.Read) return empty;
            archivedPages++;
            return request.Query!.BeforeUpdated is null ? empty with { Orders = [head], PageIds = [head.Id], HasMore = true } :
                empty with { Orders = [work], PageIds = [work.Id] };
        };
        var prefs = new WorkflowPreferences(dark) { OfficeStorage = remote, UseOfficeStorage = true };
        var store = new WorkflowPreferencesStore(Path.Combine(fixture.DirectoryPath, "preferences.json"));
        store.Save(prefs);
        var model = new WorkflowViewModel(fixture.Client.CreateStore(), new(new WorkflowAppUpdater()), preferences: store,
            initialPreferences: prefs, remoteTransport: fixture.Client);
        var window = new WorkflowWindow(store, prefs) { DataContext = model, Width = 1180, Height = 768 };
        var uploadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.BeforeUpload = async uploadToken =>
        {
            uploadStarted.TrySetResult();
            await release.Task.WaitAsync(uploadToken);
        };
        try
        {
            window.Show();
            if (scenario == "нет правок")
            {
                await WaitUntil(() => !model.IsBusy);
                window.Close();
                await WaitUntil(() => !window.IsVisible);
                Assert.Empty(window.OwnedWindows);
                Assert.Empty(fixture.Uploads);
                Assert.Equal(0, archivedPages);
                return;
            }
            await WaitUntil(() => model.OfficePendingFilesNotice == "Неотправленные файлы · 2" && !model.IsBusy);
            Assert.Empty(model.Orders);
            Assert.True(window.FindControl<Button>("WorkflowOfficePendingNotice")!.IsEffectivelyVisible);
            if (scenario is "пропустить сверку" or "отменить выход" or "проверка из уведомления")
            {
                using var blocked = CancellationTokenSource.CreateLinkedTokenSource(token);
                var publishing = fixture.Client.PublishCopyAsync(fixture.Order, fixture.Project, blocked.Token);
                try
                {
                    await WaitUntil(() => uploadStarted.Task.IsCompleted);
                    if (scenario == "проверка из уведомления")
                    {
                        Click(window, "WorkflowOfficePendingNotice");
                        var cancel = window.FindControl<Button>("SkipOfficeFilesCheck")!;
                        await WaitUntil(() => cancel.IsEffectivelyVisible && Equals(cancel.Content, "Отменить проверку"));
                        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                        await WaitUntil(() => !cancel.IsEffectivelyVisible);
                        Click(window, "WorkflowOfficePendingNotice");
                        await WaitUntil(() => cancel.IsEffectivelyVisible && Equals(cancel.Content, "Отменить проверку"));
                    }
                    window.Close();
                    await WaitUntil(() => window.FindControl<Button>("SkipOfficeFilesCheck")!.IsEffectivelyVisible);
                    if (scenario is "пропустить сверку" or "проверка из уведомления")
                    {
                        await WaitUntil(() => Equals(window.FindControl<Button>("SkipOfficeFilesCheck")!.Content, "Закрыть без отправки"));
                        Click(window, "SkipOfficeFilesCheck");
                        await WaitUntil(() => !window.IsVisible);
                    }
                    else
                    {
                        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                        await WaitUntil(() => !window.FindControl<Button>("SkipOfficeFilesCheck")!.IsEffectivelyVisible);
                        Assert.True(window.IsVisible);
                    }
                    Assert.Empty(fixture.Uploads);
                }
                finally
                {
                    blocked.Cancel();
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => publishing);
                }
                return;
            }
            if (scenario == "отправить")
            {
                Click(window, "WorkflowOfficePendingNotice");
                await WaitUntil(() => window.OwnedWindows.OfType<WorkflowOfficePendingWindow>().Any());
                var fromHeader = Assert.Single(window.OwnedWindows.OfType<WorkflowOfficePendingWindow>());
                Assert.False(fromHeader.FindControl<Button>("ExitWithoutSending")!.IsVisible);
                Click(fromHeader, "KeepWorking");
                await WaitUntil(() => window.OwnedWindows.Count == 0);
            }
            window.Close();
            await WaitUntil(() => window.OwnedWindows.OfType<WorkflowOfficePendingWindow>().Any());
            var prompt = Assert.Single(window.OwnedWindows.OfType<WorkflowOfficePendingWindow>());
            Assert.Contains("2", prompt.FindControl<TextBlock>("PendingSummary")!.Text);
            Click(prompt, "KeepWorking");
            await WaitUntil(() => window.OwnedWindows.Count == 0);
            Assert.True(window.IsVisible);
            Assert.Empty(fixture.Uploads);
            Assert.Equal(0, archivedPages);

            window.Close();
            await WaitUntil(() => window.OwnedWindows.OfType<WorkflowOfficePendingWindow>().Any());
            prompt = Assert.Single(window.OwnedWindows.OfType<WorkflowOfficePendingWindow>());
            if (scenario == "закрыть без отправки")
            {
                Click(prompt, "ExitWithoutSending");
                await WaitUntil(() => !window.IsVisible);
                Assert.Empty(fixture.Uploads);
                Assert.Equal(0, archivedPages);
                return;
            }
            lookup = true;
            Click(prompt, "SendPendingFiles");
            await WaitUntil(() => uploadStarted.Task.IsCompleted);
            Assert.True(prompt.FindControl<ProgressBar>("PendingProgress")!.IsEffectivelyVisible);
            Assert.Equal(2, archivedPages);
            if (scenario == "остановить")
            {
                Click(prompt, "KeepWorking");
                await WaitUntil(() => window.OwnedWindows.Count == 0);
                Assert.True(window.IsVisible);
                Assert.Empty(fixture.Uploads);
                return;
            }
            release.TrySetResult();
            if (scenario == "конфликт")
            {
                await WaitUntil(() => !string.IsNullOrEmpty(prompt.FindControl<TextBlock>("PendingProblems")!.Text));
                Assert.True(prompt.FindControl<ScrollViewer>("PendingDetails")!.IsEffectivelyVisible);
                Assert.Contains("Старый проект", prompt.FindControl<TextBlock>("PendingProblems")!.Text);
                Assert.Contains("Раскрой.nc", prompt.FindControl<TextBlock>("PendingProblems")!.Text);
                Assert.True(window.IsVisible);
                Assert.Equal("Исправлено в офисе", fixture.Office["Раскрой.nc"]);
                Assert.Equal("Исправлено дома", await File.ReadAllTextAsync(Path.Combine(custom.Folder, "Раскрой.nc"), token));
                Assert.Equal("Новая УП.nc", Assert.Single(fixture.Uploads));
                Assert.Equal("Неотправленные файлы · 1", model.OfficePendingFilesNotice);
                Click(prompt, "ExitWithoutSending");
                await WaitUntil(() => !window.IsVisible);
            }
            else
            {
                await WaitUntil(() => !window.IsVisible);
                Assert.Equal(new[] { "Раскрой.nc", "Новая УП.nc" }, fixture.Uploads);
            }
            Assert.Empty(log.Errors);
        }
        finally
        {
            release.TrySetResult();
            WorkflowTestWindows.Close(window);
        }
    }

    [Theory]
    [InlineData("спецификация")]
    [InlineData("занятый PDF")]
    [InlineData("уже переданная версия")]
    [InlineData("офисная правка")]
    [InlineData("свежий расчёт в офисе")]
    public async Task ProjectPublishesIndependentFilesAndKeepsTheRestForReview(string scenario)
    {
        using var fixture = new WorkingFiles();
        using var map = new MapSigningFixture();
        var token = TestContext.Current.CancellationToken;
        const string report = "Исходная спецификация.html";
        const string drawing = "Чертёж.pdf";
        const string program = "УП.nc";
        fixture.Office.Add(report, "Ранее принят в расчёт");
        fixture.Office.Add(drawing, "Чертёж уже есть в офисе");
        var home = (await fixture.OpenAsync(token)).Folder;
        fixture.ManifestUnavailable = true;
        await File.WriteAllTextAsync(Path.Combine(home, program), "Программа подготовлена дома", token);
        if (scenario is "спецификация" or "свежий расчёт в офисе")
            await File.WriteAllTextAsync(Path.Combine(home, report), "Изменена дома, ещё не рассчитана", token);
        if (scenario == "уже переданная версия") fixture.Office[program] = "Программа подготовлена дома";
        if (scenario == "офисная правка")
        {
            fixture.Office[drawing] = "Офисная правка";
            await File.WriteAllTextAsync(Path.Combine(home, drawing), "Домашняя правка", token);
        }
        using var locked = scenario == "занятый PDF"
            ? File.Open(Path.Combine(home, drawing), FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null;
        var saved = map.Order.Specifications.Single();
        var order = map.Order with
        {
            Id = fixture.Order,
            Specifications = [saved with { Id = fixture.Project, Current = saved.Current with { SourcePath = @"D:\Заказ\Исходная спецификация.html" } }],
            Projects = [map.Project with { Id = fixture.Project }]
        };
        var reads = 0;
        fixture.ReadDatabase = request =>
        {
            Assert.Equal(WorkflowAccessOperation.ReadOrders, request.Operation);
            Assert.Equal(order.Id, Assert.Single(request.OrderIds!));
            var current = ++reads == 1 && scenario == "свежий расчёт в офисе" ? order with { Specifications = [] } : order;
            return new(1, fixture.Client.Settings.DatabaseId, Orders: [current]);
        };
        var pending = await fixture.Client.ScanPendingFilesAsync(token);
        var result = await fixture.Client.PublishPendingFilesAsync(pending.Projects, [order with { Specifications = [] }], new Progress<int>(), token);
        Assert.Equal("Ранее принят в расчёт", fixture.Office[report]);
        Assert.Equal("Программа подготовлена дома", fixture.Office[program]);
        if (scenario == "уже переданная версия")
        {
            Assert.Equal(0, result.Sent);
            Assert.Equal(1, result.Completed);
            Assert.Empty(fixture.Uploads);
            Assert.Empty(result.Problems);
            Assert.False(await fixture.PendingAsync(token));
        }
        else
        {
            Assert.Equal(1, result.Sent);
            Assert.Equal(program, Assert.Single(fixture.Uploads));
            Assert.Equal(0, result.Completed);
            Assert.Contains(scenario is "спецификация" or "свежий расчёт в офисе" ? "Рассчитать" : drawing, Assert.Single(result.Problems));
            if (scenario == "офисная правка") Assert.Equal("Офисная правка", fixture.Office[drawing]);
            Assert.NotEqual(false, await fixture.PendingAsync(token));
        }
    }

    [Theory]
    [InlineData("правка во время загрузки")]
    [InlineData("другой хеш в ответе")]
    [InlineData("истёк ключ")]
    public async Task FilePublicationConfirmsSnapshotAndStopsOnSharedFailure(string scenario)
    {
        using var fixture = new ProjectFixture();
        var token = TestContext.Current.CancellationToken;
        var attempts = 0;
        string? folder = null;
        var staged = new List<string>();
        using var http = new HttpClient(new Handler(async (request, cancellation) =>
        {
            if (request.Method == HttpMethod.Get)
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { directories = Array.Empty<string>(), files = Array.Empty<object>() }) };
            Assert.Equal("1", Assert.Single(request.Headers.GetValues(WorkflowOfficeProjectPaths.WorkingFileUploadHeader)));
            var name = Uri.UnescapeDataString(request.RequestUri!.Query["?path=".Length..]);
            if (++attempts == 2 && scenario == "истёк ключ") return new(HttpStatusCode.Unauthorized);
            if (scenario == "правка во время загрузки")
                await File.WriteAllTextAsync(Path.Combine(folder!, name), "Новая локальная версия", cancellation);
            var bytes = await request.Content!.ReadAsByteArrayAsync(cancellation);
            staged.Add(System.Text.Encoding.UTF8.GetString(bytes));
            return new(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { path = name, sha256 = scenario == "другой хеш в ответе"
                    ? new string('A', 64) : Convert.ToHexString(SHA256.HashData(bytes)) })
            };
        }));
        var client = new WorkflowOfficeStorageClient(new(Address, Guid.NewGuid(), Guid.NewGuid(), true),
            StorageToken, fixture.DirectoryPath, http, http);
        var order = Guid.NewGuid(); var project = Guid.NewGuid();
        folder = await client.OpenCopyAsync(order, project, token);
        for (var i = 0; i < (scenario == "истёк ключ" ? 3 : 1); i++)
            await File.WriteAllTextAsync(Path.Combine(folder, $"{i}.nc"), "Подготовленная версия", token);
        var result = await client.PublishCopyAsync(order, project, token);
        Assert.Equal("Подготовленная версия", Assert.Single(staged));
        Assert.True(await client.HasPendingFilesAsync(project, null, null, token));
        if (scenario == "другой хеш в ответе")
        {
            Assert.Equal(0, result.Sent);
            Assert.Contains("не подтвердил", Assert.Single(result.Problems));
        }
        else
        {
            Assert.Equal(1, result.Sent);
            Assert.Empty(result.Problems);
            if (scenario == "истёк ключ")
            {
                Assert.Equal(2, attempts);
                Assert.Contains("подключитесь заново", result.StopReason);
            }
        }
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(folder, ".office-sync"), "*.tmp"));
    }

    private static void Click(Window window, string button) =>
        window.FindControl<Button>(button)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static async Task WaitUntil(Func<bool> ready)
    {
        for (var i = 0; i < 500; i++)
        {
            Dispatcher.UIThread.RunJobs();
            if (ready()) return;
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        Assert.True(ready());
    }
}

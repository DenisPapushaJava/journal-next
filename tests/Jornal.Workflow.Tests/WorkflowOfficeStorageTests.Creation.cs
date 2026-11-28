using System.Net;
using System.Net.Http.Json;
using Jornal.Workflow.Core;
using Jornal.Workflow.Desktop;
using Jornal.Workflow.Infrastructure;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed partial class WorkflowOfficeStorageTests
{
    [Theory]
    [InlineData("запись не началась")]
    [InlineData("обрыв до чтения")]
    [InlineData("маршрут пропал до записи")]
    [InlineData("офисная MDB недоступна при записи")]
    [InlineData("ответ потерян")]
    [InlineData("заявка в корзине")]
    [InlineData("заявка удалена окончательно")]
    [InlineData("база изменилась до записи")]
    public async Task RemoteOrderCreationRecoversAfterRestart(string failure)
    {
        using var fixture = new ProjectFixture();
        await fixture.InitializeAsync();
        var token = TestContext.Current.CancellationToken;
        var original = await fixture.Store.ReadAsync(token);
        var actor = Assert.Single(original.Orders).TechnologistId;
        var office = new MemoryMdb { Book = original };
        var attempts = new List<Guid>();
        var failRead = failure == "обрыв до чтения";
        Guid? idFromFailedRead = null;
        using var http = new HttpClient(new Handler(async (message, cancellation) =>
        {
            Assert.EndsWith("/database", message.RequestUri!.AbsolutePath);
            var request = (await message.Content!.ReadFromJsonAsync<WorkflowAccessRequest>(cancellation))!;
            if (failRead && request is { Operation: WorkflowAccessOperation.ReadOrders, OrderIds: [var id] })
            {
                failRead = false;
                idFromFailedRead = id;
                throw new HttpRequestException("Связь потеряна до чтения пробы.");
            }
            if (request.Operation == WorkflowAccessOperation.Apply)
            {
                attempts.Add(Assert.Single(request.Patch!.Orders).Id);
                if (attempts.Count == 1 && idFromFailedRead is null)
                {
                    Assert.Equal(office.Book.Version, request.Patch!.ExpectedVersion);
                    if (failure == "маршрут пропал до записи")
                        throw new HttpRequestException("Маршрут пропал.", new WorkflowOfficeConnectException("Проверьте адаптер", true));
                    if (failure == "офисная MDB недоступна при записи")
                        return new(HttpStatusCode.ServiceUnavailable);
                    if (failure is not ("запись не началась" or "база изменилась до записи"))
                        await office.SendAsync(request, cancellation);
                    throw new HttpRequestException("Ответ потерян.");
                }
            }
            return Reply(await office.SendAsync(request, cancellation));
        }));
        var profile = Directory.CreateDirectory(Path.Combine(fixture.DirectoryPath, "home")).FullName;
        var first = RemoteCreationModel(office, actor, profile, http);
        try
        {
            await first.ReloadCommand.ExecuteAsync(null);
            first.NewOrderCommand.Execute(null);
            first.NewManager = "Иванов";
            first.NewCustomer = "Заказчик при потере связи";
            await first.CreateOrderCommand.ExecuteAsync(null);
            Assert.False(first.IsCreating);
            Assert.Equal(failure is "обрыв до чтения" or "маршрут пропал до записи" ? "Ждёт связи" : "Ожидает подтверждения",
                Assert.Single(first.QueuedOfficeOrderRows).Status);
        }
        finally { first.Detach(); }

        var id = idFromFailedRead ?? Assert.Single(attempts);
        var saved = office.Book.Orders.SingleOrDefault(order => order.Id == id);
        if (failure == "заявка удалена окончательно")
        {
            Assert.NotNull(saved);
            office.Book = WorkflowChanges.MoveOrderToTrash(office.Book, id, saved.Version, actor, DateTimeOffset.UtcNow);
            await office.SendAsync(new(1, WorkflowAccessOperation.Purge, "test.mdb", office.Id,
                TrashTargets: [new(id, saved.Version + 1)], ActorId: actor), token);
        }
        else if (failure is "база изменилась до записи" or "обрыв до чтения" or "маршрут пропал до записи")
        {
            var originalOrder = Assert.Single(office.Book.Orders);
            office.Book = office.Book with
            {
                Version = office.Book.Version + 1,
                Orders = [originalOrder with { Blcb = "010029", Version = originalOrder.Version + 1 }]
            };
        }
        else if (saved is not null)
        {
            var at = saved.UpdatedAt.AddMinutes(1);
            office.Book = office.Book with
            {
                Version = office.Book.Version + 1,
                Orders = office.Book.Orders.Select(order => order.Id != id ? order : order with
                {
                    Version = order.Version + 1, Blcb = "010021", UpdatedAt = at,
                    DeletedAt = failure == "заявка в корзине" ? at : null,
                    DeletedBy = failure == "заявка в корзине" ? actor : null
                }).ToArray()
            };
        }

        var resumed = RemoteCreationModel(office, actor, profile, http);
        try
        {
            await resumed.ReloadCommand.ExecuteAsync(null);
            Assert.Equal(idFromFailedRead is null ? 1 : 0, attempts.Count);
            Assert.Equal("Заказчик при потере связи", Assert.Single(resumed.QueuedOfficeOrderRows).Customer);
            await resumed.SynchronizeOfficeOrdersAsync(DateTimeOffset.UtcNow, token);
            Assert.False(resumed.IsCreating);
            Assert.Null(resumed.Error);
            if (failure is "заявка удалена окончательно" or "база изменилась до записи")
            {
                var pending = Assert.Single(resumed.QueuedOfficeOrderRows);
                Assert.Equal("Требуется проверка", pending.Status);
                Assert.Contains("база изменилась", pending.Details);
                Assert.Single(attempts);
                Assert.DoesNotContain(office.Book.Orders, order => order.Id == id);
                await resumed.SynchronizeOfficeOrdersAsync(DateTimeOffset.UtcNow.AddMinutes(1), token);
                Assert.Single(attempts);
                Assert.True(resumed.CanStartNewOrder);
                await resumed.ReviewQueuedOrderAsync(pending, token);
                Assert.True(resumed.IsCreating);
                Assert.Equal("Заказчик при потере связи", resumed.NewCustomer);
                await resumed.CreateOrderCommand.ExecuteAsync(null);
                Assert.Empty(resumed.QueuedOfficeOrderRows);
                Assert.Equal(2, attempts.Count);
                Assert.NotEqual(id, attempts[1]);
                return;
            }
            Assert.Empty(resumed.QueuedOfficeOrderRows);
            var confirmed = Assert.Single(office.Book.Orders, order => order.Id == id);
            if (failure is "запись не началась" or "обрыв до чтения" or "маршрут пропал до записи" or "офисная MDB недоступна при записи")
                Assert.Equal(idFromFailedRead is null ? [id, id] : new[] { id }, attempts);
            else
            {
                Assert.Equal(id, Assert.Single(attempts));
                Assert.Equal("010021", confirmed.Blcb);
                Assert.Equal(failure == "заявка в корзине", confirmed.IsDeleted);
            }
            Assert.Equal(2, office.Book.Orders.Length);
        }
        finally { resumed.Detach(); }
    }

    [Theory]
    [InlineData("потерян ответ с заготовкой", true)]
    [InlineData("запись не началась", true)]
    [InlineData("потерян ответ с записью", true)]
    [InlineData("потерян ответ с записью", false)]
    [InlineData("временный отказ после потери ответа с заготовкой", true)]
    public async Task RemoteProjectCreationReusesItsIdentityAfterDisconnection(string failure, bool restart)
    {
        using var fixture = new ProjectFixture();
        await fixture.InitializeAsync();
        var token = TestContext.Current.CancellationToken;
        var original = await fixture.Store.ReadAsync(token);
        Directory.CreateDirectory(original.ProjectRoot!);
        var order = Assert.Single(original.Orders);
        var office = new MemoryMdb { Book = original };
        var receipts = Path.Combine(original.ProjectRoot!, ".workflow-names");
        var prepared = new List<CuttingProject>();
        var operations = new List<WorkflowAccessOperation>();
        var writes = 0;
        var rejectRetry = failure == "временный отказ после потери ответа с заготовкой";
        var losePreparationReply = rejectRetry || failure == "потерян ответ с заготовкой";
        using var http = new HttpClient(new Handler(async (message, cancellation) =>
        {
            var path = message.RequestUri!.AbsolutePath;
            if (path.EndsWith("/capabilities", StringComparison.Ordinal))
                return Reply(new { guardedCalculation = true, plannedBending = true });
            if (path.EndsWith("/prepare", StringComparison.Ordinal))
            {
                var request = (await message.Content!.ReadFromJsonAsync<WorkflowOfficeProjectPreparation>(cancellation))!;
                var target = Assert.Single(office.Book.Orders);
                Assert.Equal(target.Version, request.OrderVersion);
                if (rejectRetry && prepared.Count == 1)
                {
                    rejectRetry = false;
                    return new(HttpStatusCode.UnprocessableEntity)
                    {
                        Content = JsonContent.Create(new { error = "Временный отказ до подготовки.", projectPreparationRejected = true })
                    };
                }
                var machine = office.Book.Machines.Single(m => m.Id == request.MachineId);
                var project = await WorkflowProjectFiles.PrepareRemoteAsync(original.ProjectRoot!, target, "Заказчик", machine,
                    request.ThicknessMm, request.ItemId, request.ProjectId, DateTimeOffset.Now, cancellation, request.HasPlannedBending);
                prepared.Add(project);
                if (losePreparationReply && prepared.Count == 1)
                    throw new HttpRequestException("Ответ с заготовкой потерян.");
                return Reply(project);
            }
            Assert.EndsWith("/database", path);
            var access = (await message.Content!.ReadFromJsonAsync<WorkflowAccessRequest>(cancellation))!;
            operations.Add(access.Operation);
            if (access.Operation == WorkflowAccessOperation.Apply && ++writes == 1 && !losePreparationReply)
            {
                if (failure == "потерян ответ с записью") await office.SendAsync(access, cancellation);
                throw new HttpRequestException("Ответ MDB потерян.");
            }
            return Reply(await office.SendAsync(access, cancellation));
        }));
        var profile = Directory.CreateDirectory(Path.Combine(fixture.DirectoryPath, "home")).FullName;
        async Task<WorkflowViewModel> OpenOrderAsync()
        {
            var model = RemoteCreationModel(office, order.TechnologistId, profile, http);
            await model.ReloadCommand.ExecuteAsync(null);
            Assert.Null(model.Error);
            model.SelectedOrder = Assert.Single(model.Orders);
            model.OpenOrderCommand.Execute(null);
            return model;
        }

        var first = await OpenOrderAsync();
        try
        {
            first.AddProjectCommand.Execute(null);
            first.PlanMachine = first.ProjectMachines.Single(m => m.Name == "Лазер");
            first.PlanThickness = "6";
            first.PlanHasBending = true;
            await first.CreateProjectCommand.ExecuteAsync(null);
            Assert.NotNull(first.Error);
        }
        finally { if (restart) first.Detach(); }

        var before = Assert.Single(prepared);
        await File.WriteAllTextAsync(before.NativePath!, "Рабочий файл изменён в офисе.", token);
        var later = before with { Id = Guid.NewGuid(), ItemId = Guid.NewGuid(), Name = "Новый офисный проект", NativePath = null };
        var current = Assert.Single(office.Book.Orders);
        var alreadySaved = failure == "потерян ответ с записью";
        var expected = alreadySaved ? before with { HasPlannedBending = false } : before;
        var updated = WorkflowProjects.Add(office.Book, current.Id, current.Version, later, order.TechnologistId, DateTimeOffset.UtcNow);
        var newer = Assert.Single(updated.Orders);
        office.Book = updated with
        {
            Version = updated.Version + 1,
            Orders = [newer with { Blcb = "010021", Projects = newer.Projects.Select(p => p.Id == before.Id ? expected : p).ToArray() }]
        };

        var resumed = restart ? await OpenOrderAsync() : first;
        try
        {
            var prepBefore = prepared.Count;
            var writesBefore = writes;
            if (restart) resumed.AddProjectCommand.Execute(null);
            Assert.True(resumed.PlanHasBending);
            Assert.Equal("6", resumed.PlanThickness);
            Assert.Equal(prepBefore, prepared.Count);
            Assert.Equal(writesBefore, writes);
            Assert.True(resumed.CreateProjectCommand.CanExecute(null));
            if (rejectRetry)
            {
                await resumed.CreateProjectCommand.ExecuteAsync(null);
                Assert.Contains("Временный отказ", resumed.Error);
                Assert.True(resumed.IsPlanning);
                Assert.Single(prepared);
                Assert.Equal(writesBefore, writes);
            }
            operations.Clear();
            await resumed.CreateProjectCommand.ExecuteAsync(null);
            Assert.Null(resumed.Error);
            if (alreadySaved)
                Assert.Equal([restart ? WorkflowAccessOperation.ReadOrders : WorkflowAccessOperation.Read], operations);
            Assert.Equal(before.Id, resumed.SelectedProject?.Project.Id);
            Assert.False(resumed.IsPlanning);
            var latest = Assert.Single(office.Book.Orders);
            Assert.Equal("010021", latest.Blcb);
            Assert.Equal(2, latest.Projects.Length);
            Assert.Contains(later, latest.Projects);
            Assert.Contains(expected, latest.Projects);
            Assert.Equal(alreadySaved ? 1 : 2, prepared.Count);
            Assert.Equal(writesBefore + (alreadySaved ? 0 : 1), writes);
            Assert.All(prepared, project => Assert.Equal(before.NativePath, project.NativePath));
            Assert.Equal("Рабочий файл изменён в офисе.", await File.ReadAllTextAsync(before.NativePath!, token));
            Assert.Single(Directory.EnumerateFiles(receipts, "remote-*.json"));
        }
        finally { resumed.Detach(); }
    }

    [Theory]
    [InlineData("возможность отсутствует")]
    [InlineData("известна старая схема MDB")]
    [InlineData("сервер подтвердил отказ до подготовки")]
    public async Task CanCorrectProjectAfterDefiniteRejection(string rejection)
    {
        using var fixture = new ProjectFixture();
        await fixture.InitializeAsync();
        var token = TestContext.Current.CancellationToken;
        var original = await fixture.Store.ReadAsync(token);
        var order = Assert.Single(original.Orders);
        var office = new MemoryMdb { Book = original };
        var receipts = Path.Combine(original.ProjectRoot!, ".workflow-names");
        var profile = Directory.CreateDirectory(Path.Combine(fixture.DirectoryPath, "home")).FullName;
        var prepares = new List<WorkflowOfficeProjectPreparation>();
        using var http = new HttpClient(new Handler(async (message, cancellation) =>
        {
            var path = message.RequestUri!.AbsolutePath;
            if (path.EndsWith("/capabilities", StringComparison.Ordinal))
                return Reply(new { guardedCalculation = true, plannedBending = rejection != "возможность отсутствует" });
            if (path.EndsWith("/prepare", StringComparison.Ordinal))
            {
                var request = (await message.Content!.ReadFromJsonAsync<WorkflowOfficeProjectPreparation>(cancellation))!;
                prepares.Add(request);
                if (request.HasPlannedBending)
                    return new(HttpStatusCode.UnprocessableEntity)
                    {
                        Content = JsonContent.Create(new { error = "Гибка отклонена до подготовки.", projectPreparationRejected = true })
                    };
                var machine = office.Book.Machines.Single(m => m.Id == request.MachineId);
                return Reply(await WorkflowProjectFiles.PrepareRemoteAsync(original.ProjectRoot!, order,
                    "Заказчик", machine, request.ThicknessMm, request.ItemId, request.ProjectId, DateTimeOffset.Now, cancellation));
            }
            var access = (await message.Content!.ReadFromJsonAsync<WorkflowAccessRequest>(cancellation))!;
            var response = await office.SendAsync(access, cancellation);
            return Reply(response with { OfficeSchemaVersion = rejection == "известна старая схема MDB" ? 7 : 8 });
        }));
        var model = RemoteCreationModel(office, order.TechnologistId, profile, http);
        try
        {
            await model.ReloadCommand.ExecuteAsync(null);
            model.SelectedOrder = Assert.Single(model.Orders);
            model.OpenOrderCommand.Execute(null);
            model.AddProjectCommand.Execute(null);
            model.PlanMachine = model.ProjectMachines.Single(m => m.Name == "Лазер");
            model.PlanThickness = "6";
            model.PlanHasBending = true;
            await model.CreateProjectCommand.ExecuteAsync(null);
            Assert.NotNull(model.Error);
            Assert.Empty(Assert.Single(office.Book.Orders).Projects);
            Assert.False(Directory.Exists(receipts));
            Assert.Equal(rejection == "сервер подтвердил отказ до подготовки" ? 1 : 0, prepares.Count);

            model.PlanHasBending = false;
            await model.CreateProjectCommand.ExecuteAsync(null);
            Assert.Null(model.Error);
            var saved = Assert.Single(Assert.Single(office.Book.Orders).Projects);
            Assert.False(saved.HasPlannedBending);
            Assert.Equal(saved.Id, prepares.Last().ProjectId);
            if (prepares.Count > 1) Assert.NotEqual(prepares[0].ProjectId, saved.Id);
        }
        finally { model.Detach(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueuedOrderResumesAfterOfficeAccessIsRestored(bool expiredKey)
    {
        using var fixture = new ProjectFixture();
        await fixture.InitializeAsync();
        var token = TestContext.Current.CancellationToken;
        var original = await fixture.Store.ReadAsync(token);
        var actor = Assert.Single(original.Orders).TechnologistId;
        var office = new MemoryMdb { Book = original };
        var permission = false;
        var attempts = 0;
        using var http = new HttpClient(new Handler(async (message, cancellation) =>
        {
            var request = (await message.Content!.ReadFromJsonAsync<WorkflowAccessRequest>(cancellation))!;
            if (!permission && expiredKey && request is { Operation: WorkflowAccessOperation.ReadOrders, OrderIds.Length: 1 })
                return new(HttpStatusCode.Unauthorized);
            if (request.Operation == WorkflowAccessOperation.Apply)
            {
                attempts++;
                if (!permission)
                    return new(HttpStatusCode.Forbidden) { Content = JsonContent.Create(new { error = "remote-writes-disabled" }) };
            }
            return Reply(await office.SendAsync(request, cancellation));
        }));
        var profile = Directory.CreateDirectory(Path.Combine(fixture.DirectoryPath, "review")).FullName;
        var first = RemoteCreationModel(office, actor, profile, http);
        try
        {
            await first.ReloadCommand.ExecuteAsync(null);
            first.NewOrderCommand.Execute(null);
            first.NewManager = "Иванов";
            first.NewCustomer = "Чья отправка ждёт";
            await first.CreateOrderCommand.ExecuteAsync(null);
            var pending = Assert.Single(first.QueuedOfficeOrderRows);
            Assert.Equal("Ждёт доступа", pending.Status);
            Assert.Contains(expiredKey ? "Ключ подключения" : "Удалённое сохранение", pending.Details);
        }
        finally { first.Detach(); }
        permission = true;
        var old = Assert.Single(office.Book.Orders);
        office.Book = office.Book with { Version = office.Book.Version + 1,
            Orders = [old with { Version = old.Version + 1, Blcb = "010283" }] };
        var resumed = RemoteCreationModel(office, actor, profile, http);
        try
        {
            await resumed.ReloadCommand.ExecuteAsync(null);
            await resumed.SynchronizeOfficeOrdersAsync(DateTimeOffset.UtcNow.AddMinutes(1), token);
            Assert.Empty(resumed.QueuedOfficeOrderRows);
            Assert.Equal(expiredKey ? 1 : 2, attempts);
            Assert.Equal("010283", office.Book.Orders.Single(order => order.Id == old.Id).Blcb);
            Assert.Contains(office.Book.Orders, order => order.Id != old.Id);
        }
        finally { resumed.Detach(); }
    }

    [Fact]
    public async Task LateSuccessfulResponseDoesNotReintroducePurgedOrder()
    {
        using var fixture = new ProjectFixture();
        await fixture.InitializeAsync();
        var token = TestContext.Current.CancellationToken;
        var original = await fixture.Store.ReadAsync(token);
        var actor = Assert.Single(original.Orders).TechnologistId;
        var office = new MemoryMdb { Book = original };
        var held = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var offline = false;
        using var http = new HttpClient(new Handler(async (message, cancellation) =>
        {
            var request = (await message.Content!.ReadFromJsonAsync<WorkflowAccessRequest>(cancellation))!;
            if (offline) throw new HttpRequestException("Офис отключён.");
            var response = await office.SendAsync(request, cancellation);
            if (request.Operation == WorkflowAccessOperation.Apply)
            {
                held.SetResult(Assert.Single(request.Patch!.Orders).Id);
                await release.Task.WaitAsync(cancellation);
            }
            return Reply(response);
        }));
        var profile = Directory.CreateDirectory(Path.Combine(fixture.DirectoryPath, "review")).FullName;
        var model = RemoteCreationModel(office, actor, profile, http);
        try
        {
            await model.ReloadCommand.ExecuteAsync(null);
            model.NewOrderCommand.Execute(null);
            model.NewManager = "Иванов";
            model.NewCustomer = "Удалён до ответа";
            var creation = model.CreateOrderCommand.ExecuteAsync(null);
            var id = await held.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
            var saved = Assert.Single(office.Book.Orders, order => order.Id == id);
            office.Book = WorkflowChanges.MoveOrderToTrash(office.Book, id, saved.Version, actor, DateTimeOffset.UtcNow);
            await office.SendAsync(new(1, WorkflowAccessOperation.Purge, "test.mdb", office.Id,
                TrashTargets: [new(id, saved.Version + 1)], ActorId: actor), token);
            await model.ReloadCommand.ExecuteAsync(null);
            Assert.DoesNotContain(model.Orders, row => row.Order.Id == id);
            release.SetResult();
            await creation;
            Assert.DoesNotContain(office.Book.Orders, order => order.Id == id);
            Assert.DoesNotContain(model.Orders, row => row.Order.Id == id);
            offline = true;
            await model.ReloadCommand.ExecuteAsync(null);
            Assert.DoesNotContain(model.OfflineJournal!.Orders, row => row.Id == id);
        }
        finally { release.TrySetResult(); model.Detach(); }
    }

    private static WorkflowViewModel RemoteCreationModel(MemoryMdb office, Guid actor, string profile, HttpClient http)
    {
        var settings = new WorkflowOfficeStorageSettings(Address, office.Id, Guid.NewGuid(), true);
        var client = new WorkflowOfficeStorageClient(settings, StorageToken, profile, http);
        var preferences = new WorkflowPreferencesStore(Path.Combine(profile, "preferences.json"));
        var prefs = new WorkflowPreferences { UseOfficeStorage = true, OfficeStorage = settings, EmployeeId = actor };
        preferences.Save(prefs);
        return new WorkflowViewModel(client.CreateStore(), new(new WorkflowAppUpdater()), preferences: preferences,
            initialPreferences: prefs, remoteTransport: client);
    }
}

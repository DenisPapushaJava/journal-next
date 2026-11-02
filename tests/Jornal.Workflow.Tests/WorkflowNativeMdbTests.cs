using Jornal.Workflow.Core;
using Jornal.Workflow.Infrastructure;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class WorkflowNativeMdbTests
{
    [Fact]
    public async Task RealWindowsHostCreatesValidDatabaseAndRunsOrderProjectCalculationTrashCycle()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Skip("Native Jet/ADOX MDB lifecycle requires Windows and the x86 AccessHost."); return; }
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Journal.Next.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var executable = Environment.GetEnvironmentVariable("WORKFLOW_TEST_ACCESS_HOST") ?? Path.Combine(root.FullName,
            "src", "Jornal.Workflow.Desktop", "bin", "Release", "net10.0", "AccessHost", "Jornal.AccessHost.exe");
        Assert.True(File.Exists(executable), "Build the Windows Desktop project to prepare its self-contained x86 AccessHost.");
        using (var probe = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(executable, "--probe")
        { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true })!)
        {
            var error = probe.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            var output = probe.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            await probe.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.True(probe.ExitCode == 0, "Native host startup: " + await error + await output);
        }
        var directory = Path.Combine(Path.GetTempPath(), "JournalNext-native-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "JournalNext.mdb"); var client = new WorkflowAccessClient(executable);
            var created = await client.SendAsync(new(1, WorkflowAccessOperation.Create, path), TestContext.Current.CancellationToken);
            Assert.True(new FileInfo(path).Length > 0); Assert.NotEqual(Guid.Empty, created.DatabaseId);
            Assert.Empty(created.Orders!); Assert.Empty(created.Catalog!.Employees); Assert.Empty(created.Catalog.Rates);
            var bytes = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<WorkflowValidationException>(() => client.SendAsync(new(1, WorkflowAccessOperation.Create, path), TestContext.Current.CancellationToken));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
            var store = new WorkflowMdbStore(path, client, created.DatabaseId); await store.InspectAsync(TestContext.Current.CancellationToken);
            var book = await store.SaveEmployeeProfileAsync(null, "Технолог native test", 0xFF628DB2, TestContext.Current.CancellationToken); var actor = book.Employees.Single().Id;
            // A complete legacy import writes decimal thicknesses into both rates
            // and import keys; an integer-thickness tariff alone misses that path.
            var incoming = LegacyImportFixture();
            var importedAt = new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
            var sourcePath = Path.Combine(directory, "Исходные цены.mdb");
            book = await store.WriteAsync(book.Version, w => LegacyPriceImport.Apply(w, sourcePath, incoming, importedAt), TestContext.Current.CancellationToken);
            Assert.Equal(152, book.Rates.Length); Assert.Equal(152, book.PriceSource!.ImportedKeys.Length);
            Assert.Equal(sourcePath, book.PriceSource.Path); Assert.Equal(importedAt, book.PriceSource.LoadedAt);
            Assert.Equal(0xFF628DB2, book.Employees.Single().ColorArgb); Assert.Equal(actor, book.Employees.Single().Id);
            foreach (var expected in incoming.Rates)
            {
                var actual = Assert.Single(book.Rates, r => LegacyPriceImport.Key(r) == expected.Key);
                Assert.Equal(expected.PricePerMeter, actual.PricePerMeter); Assert.Equal(1m, actual.PricePerPierce);
            }
            var importedIds = book.Rates.Select(r => r.Id).Order().ToArray();
            book = await store.WriteAsync(book.Version, w => LegacyPriceImport.Apply(w, sourcePath, incoming, importedAt.AddSeconds(1)), TestContext.Current.CancellationToken);
            Assert.Equal(importedIds, book.Rates.Select(r => r.Id).Order());
            Assert.Equal(152, book.PriceSource!.ImportedKeys.Length);
            // Exercise UPDATE as well as INSERT without converting exact decimals to Double.
            var precise = book.Rates.First();
            foreach (var value in new[] { 0.0000000001m, 123456789012345678.1234567890m, 13.1234567891m })
            {
                precise = precise with { PricePerMeter = value, PricePerPierce = value };
                book = await store.WriteAsync(book.Version, w => WorkflowChanges.SetRate(w, precise), TestContext.Current.CancellationToken);
                var saved = Assert.Single(book.Rates, r => r.Id == precise.Id);
                Assert.Equal(value, saved.PricePerMeter); Assert.Equal(value, saved.PricePerPierce);
            }
            var result = await store.CreateOrderFromNamesAsync("Менеджер native", "Заказчик native", actor, DateTimeOffset.Now, TestContext.Current.CancellationToken);
            var order = Assert.Single(result.Workspace.Orders);
            var machine = result.Workspace.Machines.First(); var at = new DateTimeOffset(2026, 10, 25, 1, 40, 10, TimeSpan.FromHours(3));
            var name = WorkflowProjectNames.Name(machine, 6, at); var folder = Path.Combine(directory, name);
            Directory.CreateDirectory(folder); var native = Path.Combine(folder, name + WorkflowProjectNames.Extension(machine));
            await File.WriteAllBytesAsync(native, [], TestContext.Current.CancellationToken);
            var project = new CuttingProject(Guid.NewGuid(), Guid.NewGuid(), 1, machine.Id, 6,
                name, native, machine.ImportProfile, at);
            book = await store.WriteOrderAsync(order.Id, order.Version, w => WorkflowProjects.Add(w, order.Id, order.Version, project, actor, at), TestContext.Current.CancellationToken);
            order = book.Orders.Single();
            book = await new WorkflowProjectMaintenance(store).RenameAsync(order.Id, order.Version, project.Id, machine.Id, 8,
                actor, TestContext.Current.CancellationToken);
            order = book.Orders.Single(); var renamed = order.Projects.Single();
            Assert.Equal(WorkflowProjectNames.Name(machine, 8, at), renamed.Name);
            Assert.Null(renamed.Rename); Assert.True(File.Exists(renamed.NativePath)); Assert.False(Directory.Exists(folder));
            var parameters = new CuttingParameters(machine.Name, "Ст3", "Базовая", 8, "1500×3000", 1, 12.3456789m, 3, null, null, 1.2m, true, "")
            { MachineId = machine.Id, HasChamfer = true };
            var rate = new CuttingRate(Guid.NewGuid(), machine.Name, "Базовая", 8, 111.25m, 1.5m) { MachineId = machine.Id };
            book = await store.WriteAsync(book.Version, w => WorkflowChanges.SetRate(w, rate), TestContext.Current.CancellationToken);
            book = await store.WriteOrderAsync(order.Id, order.Version, w => WorkflowChanges.SaveSpecification(w, order.Id, order.Version,
                project.Id, parameters, "native-1", new(2026, 10, 25), actor, at, null, machine.ImportProfile, rate), TestContext.Current.CancellationToken);
            order = book.Orders.Single(); Assert.Equal(CuttingCalculation.Calculate(parameters, rate), order.Specifications.Single().Current.Calculation);
            Assert.True(order.Specifications.Single().Current.Parameters.HasChamfer);
            book = await store.WriteOrderAsync(order.Id, order.Version, w => WorkflowChanges.UpdateOrder(w, order.Id, order.Version,
                order.ManagerId, order.TechnologistId, "009337", actor, at), TestContext.Current.CancellationToken);
            order = book.Orders.Single();
            await MakeEarlierSchemaFixture(path);
            var versionOneBytes = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
            store = new WorkflowMdbStore(path, client, created.DatabaseId);
            using (var writer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
            {
                await Assert.ThrowsAsync<WorkflowValidationException>(() => store.InspectAsync(TestContext.Current.CancellationToken));
                Assert.Empty(Directory.GetFiles(directory, "*.schema-v1-*.bak"));
                Assert.Empty(Directory.GetFiles(directory, ".Workflow-upgrade-*.mdb"));
            }
            Assert.Equal(versionOneBytes, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
            await store.InspectAsync(TestContext.Current.CancellationToken);
            var backup = Assert.Single(Directory.GetFiles(directory, "*.schema-v1-*.bak"));
            Assert.Equal(versionOneBytes, await File.ReadAllBytesAsync(backup, TestContext.Current.CancellationToken));
            var upgraded = await store.ReadOrderAsync(order.Id, TestContext.Current.CancellationToken);
            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(order), System.Text.Json.JsonSerializer.Serialize(upgraded.Orders.Single()));
            await store.InspectAsync(TestContext.Current.CancellationToken); Assert.Single(Directory.GetFiles(directory, "*.schema-v1-*.bak"));
            var marked = await store.RecordShopHandoffAsync(order.Id, order.Version, actor, at, TestContext.Current.CancellationToken);
            Assert.True(marked.Orders.Single().HasCurrentShopHandoff); Assert.Equal(order.Version, marked.Orders.Single().Version);
            Assert.Equal(marked.Version, await store.ReadBackgroundRevisionAsync(TestContext.Current.CancellationToken));
            var background = await store.PrepareBackgroundReadAsync(TestContext.Current.CancellationToken);
            Assert.True(store.TryAcceptBackgroundRead(background));
            Assert.True(background.Workspace.Orders.Single().HasCurrentShopHandoff);
            Assert.Single(Directory.GetFiles(directory, "*.schema-v1-*.bak"));
            await MakeEarlierSchemaFixture(path, 2);
            var versionTwoBytes = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
            await store.InspectAsync(TestContext.Current.CancellationToken);
            Assert.Equal(versionTwoBytes, await File.ReadAllBytesAsync(Assert.Single(Directory.GetFiles(directory, "*.schema-v2-*.bak")), TestContext.Current.CancellationToken));
            var v2 = (await store.ReadOrderAsync(order.Id, TestContext.Current.CancellationToken)).Orders.Single();
            Assert.NotNull(v2.ShopHandoff); Assert.Null(v2.ShopHandoff.ContentHash); Assert.True(v2.HasCurrentShopHandoff);
            Assert.All(v2.Projects, p => Assert.Null(p.ShopHandoff));
            await MakeEarlierSchemaFixture(path, 3);
            var versionThreeBytes = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
            await store.InspectAsync(TestContext.Current.CancellationToken);
            Assert.Equal(versionThreeBytes, await File.ReadAllBytesAsync(Assert.Single(Directory.GetFiles(directory, "*.schema-v3-*.bak")), TestContext.Current.CancellationToken));
            var v3 = (await store.ReadOrderAsync(order.Id, TestContext.Current.CancellationToken)).Orders.Single();
            Assert.Equal(v2.ShopHandoff, v3.ShopHandoff); Assert.All(v3.Projects, p => Assert.Null(p.ShopHandoff));
            var refreshed = (await store.RecordShopHandoffAsync(v3.Id, v3.Version, actor, at, TestContext.Current.CancellationToken)).Orders.Single();
            Assert.All(refreshed.Projects, p => Assert.NotNull(p.ShopHandoff!.ProjectInputs));
            await MakeEarlierSchemaFixture(path, 4);
            var versionFourBytes = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
            await store.InspectAsync(TestContext.Current.CancellationToken);
            Assert.Equal(versionFourBytes, await File.ReadAllBytesAsync(Assert.Single(Directory.GetFiles(directory, "*.schema-v4-*.bak")), TestContext.Current.CancellationToken));
            var v4 = (await store.ReadOrderAsync(order.Id, TestContext.Current.CancellationToken)).Orders.Single();
            Assert.Equal(refreshed.ShopHandoff, v4.ShopHandoff);
            Assert.All(v4.Projects, p => { Assert.NotNull(p.ShopHandoff); Assert.Null(p.ShopHandoff.ProjectInputs); });
            refreshed = (await store.RecordShopHandoffAsync(v4.Id, v4.Version, actor, at, TestContext.Current.CancellationToken)).Orders.Single();
            Assert.All(refreshed.Projects, p => Assert.NotNull(p.ShopHandoff!.ProjectInputs));
            Assert.All(refreshed.Projects.Where(p => refreshed.Items.Any(i => i.IsIncluded && i.CurrentProjectId == p.Id)), p => Assert.Equal(WorkflowShopHandoff.ProjectHash(refreshed, p.Id), p.ShopHandoff!.ContentHash));
            Assert.Equal(WorkflowShopHandoff.Hash(refreshed), refreshed.ShopHandoff!.ContentHash);
            // A prepared calculation patch may cross a metadata-only transfer commit.
            var latest = await client.SendAsync(new(1, WorkflowAccessOperation.ReadOrders, path, created.DatabaseId, OrderIds: [refreshed.Id]), TestContext.Current.CancellationToken);
            var raced = await client.SendAsync(new(1, WorkflowAccessOperation.Apply, path, created.DatabaseId,
                Patch: new(null, latest.CatalogVersion, null, [new(refreshed.Id, refreshed.Version, v3 with { Version = v3.Version + 1, Blcb = "009338" })])), TestContext.Current.CancellationToken);
            var savedAfterTransfer = raced.Orders!.Single();
            Assert.Equal(refreshed.ShopHandoff, savedAfterTransfer.ShopHandoff);
            Assert.Equal(refreshed.Projects.Single().ShopHandoff, savedAfterTransfer.Projects.Single().ShopHandoff);
            refreshed = savedAfterTransfer;
            var unmarked = (await store.ClearShopHandoffAsync(refreshed.Id, refreshed.Version, WorkflowHandoffClearExpected.From(refreshed), actor, TestContext.Current.CancellationToken)).Orders.Single();
            Assert.Null(unmarked.ShopHandoff); Assert.Equal(refreshed.Version, unmarked.Version);
            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(refreshed.Specifications), System.Text.Json.JsonSerializer.Serialize(unmarked.Specifications));
            var afterClear = await client.SendAsync(new(1, WorkflowAccessOperation.Apply, path, created.DatabaseId,
                Patch: new(null, raced.CatalogVersion, null, [new(unmarked.Id, unmarked.Version, refreshed with { Version = unmarked.Version + 1 })])), TestContext.Current.CancellationToken);
            unmarked = afterClear.Orders!.Single(); Assert.Null(unmarked.ShopHandoff); Assert.All(unmarked.Projects, p => Assert.Null(p.ShopHandoff));
            await store.RecordShopHandoffAsync(unmarked.Id, unmarked.Version, actor, at.AddMinutes(1), TestContext.Current.CancellationToken);
            book = await new WorkflowMdbStore(path, client, created.DatabaseId).ReadAsync(TestContext.Current.CancellationToken);
            Assert.True(book.Orders.Single().HasCurrentShopHandoff); order = book.Orders.Single();
            var secondName = WorkflowProjectNames.Name(machine, 8, at.AddSeconds(1)); var secondFolder = Path.Combine(directory, secondName);
            Directory.CreateDirectory(secondFolder); var secondNative = Path.Combine(secondFolder, secondName + WorkflowProjectNames.Extension(machine));
            await File.WriteAllBytesAsync(secondNative, [], TestContext.Current.CancellationToken);
            var second = renamed with { Id = Guid.NewGuid(), Variant = 2, Name = secondName, NativePath = secondNative, CreatedAt = at.AddSeconds(1) };
            book = await store.WriteOrderAsync(order.Id, order.Version, w => WorkflowProjects.Add(w, order.Id, order.Version, second, actor, at), TestContext.Current.CancellationToken);
            order = book.Orders.Single();
            book = await store.WriteOrderAsync(order.Id, order.Version, w => WorkflowChanges.SaveSpecification(w, order.Id, order.Version,
                second.Id, parameters, "native-2", new(2026, 10, 25), actor, at, null, machine.ImportProfile, rate), TestContext.Current.CancellationToken);
            order = book.Orders.Single();
            book = await store.WriteOrderAsync(order.Id, order.Version, w => WorkflowProjects.Select(w, order.Id, order.Version, second.Id, actor, at), TestContext.Current.CancellationToken);
            order = book.Orders.Single(); Assert.Equal(second.Id, order.Items.Single().CurrentProjectId);
            Assert.Equal(second.Id, Assert.Single(order.ActiveSpecifications).Id);
            store.Query = new(Search: "Заказчик Ст3"); book = await store.ReadAsync(TestContext.Current.CancellationToken); Assert.Single(book.Orders);
            // Exercise actual Jet EXISTS/correlations, not the managed MemoryMdb filter.
            async Task CheckFilter(WorkflowOrderFilter filter, int expected)
            {
                var response = await client.SendAsync(new(1, WorkflowAccessOperation.Read, path, created.DatabaseId,
                    Query: new(Limit: 1, Filter: filter)), TestContext.Current.CancellationToken);
                Assert.Equal(expected, response.TotalCount); Assert.Equal(expected, response.PageIds!.Length);
            }
            await CheckFilter(new(actor, WorkflowWorkFilter.InWork, true, true), 1);
            await CheckFilter(new(Guid.NewGuid()), 0);
            await CheckFilter(new(Work: WorkflowWorkFilter.NotSent), 0);
            var outsideDay = order.CreatedAt.AddDays(1);
            var outside = await client.SendAsync(new(1, WorkflowAccessOperation.Read, path, created.DatabaseId,
                Query: new(outsideDay, outsideDay.AddDays(1), order.ReferenceNumber, Filter: new(WithBending: true))), TestContext.Current.CancellationToken);
            Assert.Equal(0, outside.TotalCount); Assert.Empty(outside.PageIds!);
            book = await store.WriteOrderAsync(order.Id, order.Version, w => WorkflowProjects.SetIncluded(w, order.Id, order.Version,
                second.ItemId, false, actor, at), TestContext.Current.CancellationToken); order = book.Orders.Single();
            await CheckFilter(new(WithBending: true), 0); await CheckFilter(new(WithChamfer: true), 0);
            book = await store.WriteOrderAsync(order.Id, order.Version, w => WorkflowProjects.SetIncluded(w, order.Id, order.Version,
                second.ItemId, true, actor, at), TestContext.Current.CancellationToken); order = book.Orders.Single();
            book = await store.WriteOrderAsync(order.Id, order.Version, w => WorkflowChanges.SaveSpecification(w, order.Id, order.Version,
                second.Id, parameters with { HasBending = false, HasChamfer = false }, "native-2", new(2026, 10, 25), actor, at, null, machine.ImportProfile, rate), TestContext.Current.CancellationToken);
            order = book.Orders.Single();
            await CheckFilter(new(WithBending: true), 0); await CheckFilter(new(WithChamfer: true), 0); // The old variant still has both operations.
            await CheckFilter(new(Work: WorkflowWorkFilter.InWork), 1); // Changed handoff remains in work.
            book = await store.WriteOrderAsync(order.Id, order.Version, w => WorkflowChanges.MoveOrderToTrash(w, order.Id, order.Version, actor, at), TestContext.Current.CancellationToken);
            order = book.Orders.Single(); Assert.True(order.IsDeleted);
            book = await store.ReadTrashAsync(TestContext.Current.CancellationToken); Assert.Single(book.Orders, o => o.IsDeleted);
            await store.PurgeTrashedOrdersAsync([new(order.Id, order.Version)], actor, TestContext.Current.CancellationToken);
            Assert.Empty((await store.ReadAsync(TestContext.Current.CancellationToken)).Orders);
            Assert.Empty(Directory.EnumerateFiles(directory, ".JournalNext-create-*"));
            await Assert.ThrowsAsync<WorkflowValidationException>(() => new WorkflowMdbStore(path, client, Guid.NewGuid()).InspectAsync(TestContext.Current.CancellationToken));
        }
        finally { Directory.Delete(directory, true); }
    }

    private static async Task MakeEarlierSchemaFixture(string path, int schema = 1)
    {
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SysWOW64", "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new System.Diagnostics.ProcessStartInfo(powershell) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true };
        start.Environment["WORKFLOW_SCHEMA_FIXTURE"] = path;
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-Command");
        var sql = schema switch { 1 => "DROP TABLE [order_shop_handoffs]", 2 => "ALTER TABLE [order_shop_handoffs] DROP COLUMN [content_hash]", _ => null };
        start.ArgumentList.Add("$ErrorActionPreference='Stop'; $c=New-Object -ComObject ADODB.Connection; try { $c.Open('Provider=Microsoft.Jet.OLEDB.4.0;Data Source=' + $env:WORKFLOW_SCHEMA_FIXTURE); $null=$c.Execute('DROP TABLE [project_shop_handoff_inputs]'); " + (schema < 4 ? "$null=$c.Execute('DROP TABLE [project_shop_handoffs]'); " : "") + (sql is null ? "" : "$null=$c.Execute('" + sql + "'); ") + "$null=$c.Execute('UPDATE [app_metadata] SET [schema_version]=" + schema + " WHERE [singleton]=1'); } finally { if ($c.State -ne 0) {$c.Close()}; [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($c) }");
        using var process = System.Diagnostics.Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.True(process.ExitCode == 0, "Create isolated earlier-schema MDB fixture: " + await error + await output);
    }

    private static LegacyPriceCatalog LegacyImportFixture()
    {
        // Synthetic prices only; no business data from the reported MDB.
        decimal[] thicknesses = [0.5m, 0.8m, 1, 1.2m, 1.5m, 2, 2.5m, 3, 4, 5, 6, 8, 10, 12, 14, 15, 16, 18, 20, 22, 25, 28, 30, 32, 36, 40, 50, 55, 60];
        (string Machine, string Group, int Count)[] bindings =
        [
            ("Лазер", "Базовая", 29), ("Плазма", "Базовая", 17), ("Газо-кислородная", "Базовая", 19),
            ("HansLaser", "Базовая", 29), ("HansLaser", "Нержавеющая сталь", 29), ("HansLaser", "Цветные металлы", 29)
        ];
        return new(WorkflowMachine.Defaults().Where(m => m.LegacyKey is not null).ToArray(),
            bindings.SelectMany(b => thicknesses.Take(b.Count).Select((t, i) =>
                new LegacyMeterRate(new(b.Machine, b.Group, t), i == 0 ? 0 : 13.123456789m + i))).ToArray(), []);
    }
}

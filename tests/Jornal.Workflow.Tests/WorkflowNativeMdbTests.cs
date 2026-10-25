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
}

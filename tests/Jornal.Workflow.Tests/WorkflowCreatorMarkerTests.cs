using Avalonia.Media;
using Jornal.Workflow.Core;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class WorkflowCreatorMarkerTests
{
    [Fact]
    public async Task CreatorColorFollowsCreationActorRatherThanResponsibleEmployeeOrLastEditor()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync();
        var initial = Assert.Single(f.Model.Orders);
        Assert.Equal("Петров", initial.Creator!.Name); Assert.Null(initial.CreatorBrush);
        Assert.Contains("цвет не выбран", initial.NumberHint);
        var book = await f.Store.ReadAsync(TestContext.Current.CancellationToken);
        var creator = book.Employees.Single(e => e.Name == "Петров");
        var responsible = book.Employees.Single(e => e.Name == "Иванов");
        var editor = book.Employees.Single(e => e.Name == "Сидоров");
        book = await f.Store.SaveEmployeeProfileAsync(creator, creator.Name, 0xFF42C1FF, TestContext.Current.CancellationToken);
        book = await f.Store.SaveEmployeeProfileAsync(responsible, responsible.Name, 0xFF80FF80, TestContext.Current.CancellationToken);
        book = await f.Store.SaveEmployeeProfileAsync(editor, editor.Name, 0xFFFF9DFF, TestContext.Current.CancellationToken);
        var order = book.Orders.Single();
        await f.Store.WriteOrderAsync(order.Id, order.Version, w => WorkflowChanges.UpdateOrder(w, order.Id, order.Version,
            responsible.Id, responsible.Id, "123", 2026, editor.Id, DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
        await f.Model.ReloadCommand.ExecuteAsync(null);
        var profile = f.Model.CreateGeneralSettings(); profile.ProfileName = editor.Name;
        await profile.SaveCommand.ExecuteAsync(null); Assert.Null(profile.Error);
        var row = Assert.Single(f.Model.Orders);
        Assert.Equal(creator.Id, row.Creator!.Id); Assert.Equal("Иванов", row.Technologist);
        Assert.Equal(editor.Id, f.Model.CurrentEmployee!.Id);
        Assert.Equal(editor.Id, row.Order.Events[^1].ActorId);
        Assert.Equal(0xFF42C1FFu, Assert.IsType<SolidColorBrush>(row.CreatorBrush).Color.ToUInt32());
        Assert.Equal("Создал: Петров", row.CreatorDescription);

        creator = (await f.Store.ReadAsync(TestContext.Current.CancellationToken)).Employees.Single(e => e.Id == creator.Id);
        await f.Store.SaveEmployeeProfileAsync(creator, creator.Name, 0xFF0596BA, TestContext.Current.CancellationToken);
        await f.Model.ReloadCommand.ExecuteAsync(null);
        var refreshed = Assert.Single(f.Model.Orders);
        Assert.Equal(creator.Id, refreshed.Creator!.Id);
        Assert.Equal(0xFF0596BAu, Assert.IsType<SolidColorBrush>(refreshed.CreatorBrush).Color.ToUInt32());
        Assert.Equal(row.Order.Version, refreshed.Order.Version); Assert.Equal(row.Order.Events, refreshed.Order.Events);
    }
}

using System.Text.Json;
using System.Text.Json.Nodes;
using Jornal.Workflow.Core;
using Jornal.Workflow.Desktop;
using Jornal.Workflow.Infrastructure;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class WorkflowProjectHandoffReasonTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static WorkflowWorkspace Mark(WorkflowWorkspace book)
    {
        var order = book.Orders.Single();
        return WorkflowChanges.RecordShopHandoff(book, order.Id, order.Version, order.TechnologistId, DateTimeOffset.UtcNow);
    }

    [Fact]
    public void ReasonsDescribeWorkChangesAndIgnorePriceInternalNotesAndNumericScale()
    {
        using var f = new ShopFixture(); var marked = Mark(f.Book); var order = marked.Orders.Single();
        var baseline = order.Projects.Single().ShopHandoff!.ProjectInputs!;
        var spec = order.Specifications.Single();
        var ignored = order with { Version = order.Version + 1, UpdatedAt = order.UpdatedAt.AddMinutes(5),
            Items = [order.Items.Single() with { IsIncluded = false }],
            Specifications = [spec with { Current = spec.Current with { Parameters = spec.Current.Parameters with
            { ManualCoefficient = 1.5m, Comment = "внутренний текст", ThicknessMm = 6.000m, CutLengthMeters = 100 },
                Calculation = spec.Current.Calculation with { PricePerMeter = 500 } } }] };
        Assert.Empty(baseline.Changes(WorkflowProjectHandoffInputs.Capture(ignored, f.Project.Id)));
        var changed = order with { Blcb = "009338", Specifications = [spec with { Current = spec.Current with
        { Parameters = spec.Current.Parameters with { Material = "09Г2С", BlankSize = "1500×3000", BlankQuantity = 2, HasChamfer = true, HasBending = true } } }] };
        var reasons = baseline.Changes(WorkflowProjectHandoffInputs.Capture(changed, f.Project.Id));
        Assert.Equal(new[] { "БЛЦБ: 009337 → 009338.", "Материал: Ст3 → 09Г2С.", "Заготовка: Лист → 1500×3000.",
            "Количество заготовок: 1 → 2.", "Добавлена фаска.", "Добавлена гибка." }, reasons);
        Assert.Empty(baseline.Changes(WorkflowProjectHandoffInputs.Capture(order, f.Project.Id))); // Undo restores the solid mark.
        var reverse = WorkflowProjectHandoffInputs.Capture(changed, f.Project.Id).Changes(baseline);
        Assert.Contains("Снята отметка фаски.", reverse); Assert.Contains("Снята отметка гибки.", reverse);
    }

    [Fact]
    public void HeaderProjectAndReportReasonsAreSpecificAndCalculationThicknessNeverShowsAnEqualPair()
    {
        using var f = new ShopFixture(); var baseline = WorkflowProjectHandoffInputs.Capture(f.Order, f.Project.Id);
        var reasons = baseline.Changes(baseline with { ManagerId = Guid.NewGuid(), CustomerId = Guid.NewGuid(), MachineId = Guid.NewGuid(),
            CalculationThicknessMm = 2.5m, ProjectName = "Другое имя", ImportProfile = "другой формат", ReportHash = new string('A', 64) });
        Assert.Equal(new[] { "Изменён менеджер.", "Изменён заказчик.", "Изменён станок.", "Толщина: 6 → 2,5 мм.",
            "Изменено имя проекта.", "Изменён формат спецификации.", "Обновлена спецификация." }, reasons);
        Assert.Empty((baseline with { ReportHash = new string('A', 64) }).Changes(baseline with { ReportHash = new string('a', 64) }));
    }

    [Fact]
    public async Task BaselineIsSharedRoundtripsAsScalarColumnsAndSurvivesJsonRestartAndResending()
    {
        using var f = new ShopFixture(); var wire = new MemoryMdb { Book = f.Book }; var store = new WorkflowMdbStore(Path.Combine(f.Root, "test.mdb"), wire, wire.Id);
        await store.InspectAsync(Token); var order = f.Order;
        var book = await store.RecordShopHandoffAsync(order.Id, order.Version, order.TechnologistId, DateTimeOffset.UtcNow, Token);
        var marked = book.Orders.Single().Projects.Single().ShopHandoff!; Assert.NotNull(marked.ProjectInputs);
        var colleague = new WorkflowMdbStore(store.FilePath, wire, wire.Id);
        Assert.Equal(marked, (await colleague.ReadAsync(Token)).Orders.Single().Projects.Single().ShopHandoff);
        var rows = WorkflowMdbMapping.Flatten(book);
        Assert.Single(rows["project_shop_handoff_inputs"]); Assert.Equal("Ст3", rows["project_shop_handoff_inputs"][f.Project.Id].Values["material"]);
        Assert.Equal(marked, WorkflowMdbMapping.Orders(rows).Single().Projects.Single().ShopHandoff);
        var path = Path.Combine(f.Root, "restart.json"); await File.WriteAllTextAsync(path, JsonSerializer.Serialize(book), Token);
        var restarted = await new LocalWorkflowWorkspaceStore(path).ReadAsync(Token); Assert.Equal(marked, restarted.Orders.Single().Projects.Single().ShopHandoff);
        var changed = order with { Blcb = "009338", Projects = restarted.Orders.Single().Projects, Version = order.Version + 1 };
        var sentAgain = Mark(restarted with { Orders = [changed] }).Orders.Single().Projects.Single().ShopHandoff!;
        Assert.Empty(sentAgain.ProjectInputs!.Changes(WorkflowProjectHandoffInputs.Capture(changed, f.Project.Id)));
        Assert.NotEmpty(marked.ProjectInputs!.Changes(sentAgain.ProjectInputs)); // The old immutable baseline was not edited.
    }

    [Fact]
    public async Task OldProjectMarksUpgradeWithExactBackupAndHonestTooltipUntilNextTransfer()
    {
        using var f = new ShopFixture(); var marked = Mark(f.Book); var json = JsonSerializer.SerializeToNode(marked)!;
        json["SchemaVersion"] = 14; json["Orders"]![0]!["Projects"]![0]!["ShopHandoff"]!.AsObject().Remove("ProjectInputs");
        var path = Path.Combine(f.Root, "old.json"); var bytes = System.Text.Encoding.UTF8.GetBytes(json.ToJsonString()); await File.WriteAllBytesAsync(path, bytes, Token);
        var upgraded = await new LocalWorkflowWorkspaceStore(path).ReadAsync(Token); var order = upgraded.Orders.Single();
        Assert.Equal(15, upgraded.SchemaVersion); Assert.Null(order.Projects.Single().ShopHandoff!.ProjectInputs);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Assert.Single(Directory.GetFiles(f.Root, "old.json.*.bak")), Token));
        order = order with { Blcb = "009338", Version = order.Version + 1 };
        var p = order.Projects.Single(); var row = new WorkflowProjectRow(p, 1, "Лазер", true, order.Specifications.Single().Current);
        row.ApplyShopHandoff(order, upgraded.Employees, 0xFF80FFFF);
        Assert.True(row.HasShopHandoffChanges); Assert.Contains("Точные отличия не сохранены", row.ShopHandoffHint); Assert.DoesNotContain("Материал:", row.ShopHandoffHint);
        var resent = Mark(upgraded with { Orders = [order] }).Orders.Single(); row.ApplyShopHandoff(resent, upgraded.Employees, 0xFF80FFFF);
        Assert.False(row.HasShopHandoffChanges); Assert.NotNull(row.Project.ShopHandoff!.ProjectInputs);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void UpgradeAddsOnlyTheMissingBaselineTableAndDoesNotBackfillUnknownOldInputs(int from)
    {
        var sql = WorkflowMdbSchema.UpgradeSql(from).ToArray();
        Assert.Single(sql, s => s.StartsWith("CREATE TABLE [project_shop_handoff_inputs]", StringComparison.Ordinal));
        Assert.Contains(sql, s => s.Contains("FOREIGN KEY ([id]) REFERENCES [project_shop_handoffs]", StringComparison.Ordinal));
        Assert.DoesNotContain(sql, s => s.StartsWith("UPDATE ", StringComparison.Ordinal) || s.StartsWith("INSERT ", StringComparison.Ordinal) || s.StartsWith("DELETE ", StringComparison.Ordinal));
        if (from == 4) Assert.DoesNotContain(sql, s => s.StartsWith("CREATE TABLE [project_shop_handoffs]", StringComparison.Ordinal));
        Assert.Contains(WorkflowMdbSchema.CreateSql(), s => s.StartsWith("CREATE TABLE [project_shop_handoff_inputs]", StringComparison.Ordinal));
    }
}

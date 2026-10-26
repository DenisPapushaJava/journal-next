using System.Globalization;
using Jornal.Workflow.Core;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class WorkflowCuttingTaskTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 26, 10, 15, 7, TimeSpan.FromHours(3));
    private static (WorkflowWorkspace Book, WorkflowOrder Order) Sample()
    {
        var book = WorkflowTestData.CreateWorkspace();
        var customer = new Customer(Guid.NewGuid(), "ЗПТО");
        book = book with { Customers = [customer], Employees = [book.Employees[0] with { Name = "Юрченко" }] };
        var order = new WorkflowOrder(Guid.NewGuid(), 1, customer.Id, book.Employees[0].Id, book.Employees[0].Id,
            "БЛЦБ-009337", 1, At, At, []) { ReferenceNumber = "2026-10-26-101507" };
        return (book, order);
    }
    private static WorkflowSpecification Spec(WorkflowWorkspace book, decimal thickness, string machine = "Лазер", string material = "Ст3", bool bending = false, bool chamfer = false)
    {
        var m = book.Machines.Single(m => m.Name == machine);
        var p = new CuttingParameters(m.Name, material, "Базовая", thickness, "Лист", 1, 1, 1, null, null, 1, bending, "Не включать комментарий")
        { MachineId = m.Id, HasChamfer = chamfer };
        return new(Guid.NewGuid(), new("1026-101507", new(2026, 10, 26), At, book.Employees[0].Id, p, new(Guid.NewGuid(), 100, 1, 100, 1, 101), null, null));
    }
    private static CuttingTaskPreview Prepare(WorkflowWorkspace b, WorkflowOrder o) => WorkflowCuttingTask.Prepare(o, b.Employees, b.Customers, b.Machines);

    [Fact]
    public void MessageUsesSavedOperationsExactThicknessAndCurrentMachineNames()
    {
        var (book, order) = Sample();
        order = order with { Specifications = [Spec(book, 12, material: "09Г2С"), Spec(book, 8, bending: true), Spec(book, 6, chamfer: true), Spec(book, 4, bending: true, chamfer: true)] };
        var text = Prepare(book, order);
        Assert.True(text.IsReady);
        Assert.Equal("9337  Юрченко (ЗПТО)\n4 мм Ст3 ГИБКА, ФАСКА\n6 мм Ст3 ФАСКА\n8 мм Ст3 ГИБКА\n12 мм 09Г2С   на Лазер\n", text.Text);
        var laser = book.Machines.Single(m => m.Name == "Лазер");
        book = book with { Machines = book.Machines.Select(m => m.Id == laser.Id ? m with { Name = "Новый лазер" } : m).ToArray() };
        Assert.Contains("на Новый лазер\n", Prepare(book, order).Text);
        Assert.DoesNotContain("Не включать", text.Text);
    }

    [Theory]
    [InlineData("БЛЦБ-009337", "9337")]
    [InlineData("000000", "0")]
    [InlineData("12", "12")]
    [InlineData("БЛЦБ-ABC", "БЛЦБ-ABC")]
    public void NumberShortensOnlyForMessage(string stored, string displayed)
    {
        var (book, order) = Sample(); order = order with { Blcb = stored, Specifications = [Spec(book, .123456789m)] };
        var old = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            Assert.StartsWith(displayed + "  Юрченко", Prepare(book, order).Text);
            Assert.Contains("0,123456789 мм", Prepare(book, order).Text); Assert.Equal(stored, order.Blcb);
        }
        finally { CultureInfo.CurrentCulture = old; }
    }

    [Fact]
    public void IncludedCurrentProjectsOnlyAndUnreviewedVariantBlocksWholeMessage()
    {
        var (book, order) = Sample(); var a = Spec(book, 4); var b = Spec(book, 6, "Плазма"); var c = Spec(book, 8); var d = Spec(book, 12);
        CuttingProject P(WorkflowSpecification s, Guid item, int variant = 1) => new(s.Id, item, variant, s.Current.Parameters.MachineId,
            s.Current.Parameters.ThicknessMm, "Проект", null, null, At);
        var item = Guid.NewGuid(); var excluded = Guid.NewGuid(); var deleted = Guid.NewGuid();
        order = order with { Specifications = [a, b, c, d], Projects = [P(a, item), P(b, item, 2), P(c, excluded), P(d, deleted) with { DeletedAt = At, DeletedBy = book.Employees[0].Id }],
            Items = [new(item, 1, a.Id), new(excluded, 2, c.Id) { IsIncluded = false }, new(deleted, 3, d.Id)] };
        var preview = Prepare(book, order); Assert.False(preview.IsReady); Assert.Equal(b.Id, preview.Problem!.ProjectId);
        order = order with { Items = order.Items.Select(i => i.Id == item ? i with { CurrentProjectId = b.Id, ReviewedThroughVariant = 2 } : i).ToArray() };
        preview = Prepare(book, order); Assert.True(preview.IsReady);
        Assert.Equal("9337  Юрченко (ЗПТО)\n6 мм Ст3   на Плазму\n", preview.Text);
        order = order with { Specifications = [a, c, d] };
        Assert.Equal(b.Id, Prepare(book, order).Problem!.ProjectId); Assert.Equal("", Prepare(book, order).Text);
    }

    [Fact]
    public void GroupsRemainSeparateAndEqualThicknessRowsAreNotMerged()
    {
        var (book, order) = Sample(); var a = Spec(book, 6, bending: true); var b = Spec(book, 6, material: "AISI304");
        order = order with { Specifications = [Spec(book, 20, "Плазма"), a, b] };
        var text = Prepare(book, order).Text;
        Assert.Contains("6 мм AISI304", text); Assert.Contains("6 мм Ст3 ГИБКА", text);
        Assert.Equal(2, text.Split('\n').Count(line => line.StartsWith("6 мм ", StringComparison.Ordinal)));
        Assert.Contains("   на Лазер\n\n20 мм Ст3   на Плазму\n", text);
    }

    [Fact]
    public void MissingRequisitesAndUnverifiedMaterialNeverProducePartialText()
    {
        var (book, order) = Sample(); order = order with { Specifications = [Spec(book, 6)] };
        Assert.Equal(CuttingTaskTarget.Header, Prepare(book, order with { Blcb = " " }).Problem!.Target);
        Assert.Equal(CuttingTaskTarget.Projects, Prepare(book, order with { Specifications = [] }).Problem!.Target);
        var saved = order.Specifications[0];
        order = order with { Specifications = [saved with { Current = saved.Current with { Parameters = saved.Current.Parameters with { MaterialNeedsReview = true } } }] };
        Assert.Equal(saved.Id, Prepare(book, order).Problem!.ProjectId); Assert.Equal("", Prepare(book, order).Text);
        Assert.False(Prepare(book, order with { DeletedAt = At, DeletedBy = book.Employees[0].Id }).IsReady);
    }
}

using System.Diagnostics;
using Jornal.Workflow.Core;
using Jornal.Workflow.Desktop;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class WorkflowSearchTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("тек", true)] [InlineData("ВАН", true)] [InlineData("етров", true)]
    [InlineData("123", true)] [InlineData("БЛЦБ 123", true)] [InlineData("123/2026", false)]
    [InlineData("024-005", true)] [InlineData("Ст3", true)] [InlineData("2,5", true)] [InlineData("2.5", true)]
    [InlineData("птек иван 0057", true)] [InlineData("\tИВАН\n птек   0057 ", true)]
    [InlineData("Заявка 2026-10-24 14:35:27-02", true)] [InlineData("2026-10-24-14:35:27-02", true)]
    [InlineData("2026-10-24-143527-02", true)] [InlineData("24.10.2026", true)]
    [InlineData("эскиз-а*_[%]?.html", true)] [InlineData("*", true)] [InlineData("%", true)]
    [InlineData("№501", true)] [InlineData("птек 99999", false)]
    [InlineData("УСТАРЕВШЕЕ", false)] [InlineData("несуществующий", false)]
    public void FragmentsAndMultipleTermsMatchSavedFieldsAndNumbersWithoutSearchingHistory(string text, bool expected)
    {
        var book = Sample(); var index = new WorkflowOrderSearchIndex(book);
        Assert.Equal(expected, index.Documents[book.Orders[0].Id].Matches(new(text), out _));
    }

    [Fact]
    public void WildcardsAreLiteralAndLeadingZerosAndYoStaySignificant()
    {
        var book = Sample(); var order = book.Orders[0]; var specification = order.Specifications[0];
        var revision = specification.Current with { DocumentNumber = "00123", Report = null,
            Parameters = specification.Current.Parameters with { Comment = "ВсёСырьё", BlankSize = "Лист" } };
        book = book with { Orders = [order with { Specifications = [specification with { Current = revision }] }] };
        var doc = new WorkflowOrderSearchIndex(book).Documents[order.Id];
        Assert.True(doc.Matches(new("00123"), out _)); Assert.False(doc.Matches(new("000123"), out _));
        Assert.True(doc.Matches(new("всёсырьё"), out _)); Assert.False(doc.Matches(new("всесырье"), out _));
        Assert.False(doc.Matches(new("*"), out _)); Assert.False(doc.Matches(new("%"), out _));
        Assert.False(doc.Matches(new("?"), out _)); Assert.False(doc.Matches(new("["), out _));
        Assert.True(doc.Matches(new("\t\n"), out var detail)); Assert.Empty(detail);
    }

    [Fact]
    public void CurrentCalculationsOfNonDeletedVariantsAreSearchableButProjectAndOrderTrashAreNot()
    {
        var book = Sample(); var order = book.Orders[0]; var machine = book.Machines[0];
        var first = order.Specifications[0]; var deletedId = Guid.NewGuid(); var itemId = Guid.NewGuid();
        var deleted = first with { Id = deletedId, Current = first.Current with { DocumentNumber = "Удалённая-спека-9966" } };
        var removedProject = new CuttingProject(deletedId, itemId, 2, machine.Id, 4, "Удалённый-проект-9988", null, null, DateTimeOffset.Now) { DeletedAt = DateTimeOffset.Now };
        var activeProject = removedProject with { Id = first.Id, Variant = 1, Name = "Резервный-проект-5577", DeletedAt = null };
        var prepared = activeProject with { Id = Guid.NewGuid(), Variant = 3, Name = "Заготовка-без-отчёта-7766" };
        order = order with { Projects = [activeProject, removedProject, prepared], Specifications = [first, deleted],
            Items = [new(itemId, 1, prepared.Id) { IsIncluded = false }] };
        var trashed = order with { Id = Guid.NewGuid(), DeletedAt = DateTimeOffset.Now };
        var index = new WorkflowOrderSearchIndex(book with { Orders = [order, trashed] });
        var doc = Assert.Single(index.Documents).Value;
        Assert.True(doc.Matches(new("5577"), out var detail)); Assert.StartsWith("Проект:", detail);
        Assert.True(doc.Matches(new("7766"), out _)); Assert.True(doc.Matches(new("0057"), out _));
        Assert.False(doc.Matches(new("9966"), out _)); Assert.False(doc.Matches(new("9988"), out _));
    }

    [Fact]
    public async Task SearchUsesMemoryPreservesDirtyCardAndRefreshesSavedProjectionAfterStoreChanges()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var project = await f.AddAsync();
        await ProjectFixture.WriteAsync(project); await f.AcceptAsync();
        var draft = f.Model.SelectedDraft!; var savedComment = "УникальныйКомментарий";
        draft.Comment = savedComment; await f.Model.SaveSpecificationCommand.ExecuteAsync(null); Assert.Null(f.Model.Error);
        draft = f.Model.SelectedDraft!; f.Model.AddProjectCommand.Execute(null); f.Model.PlanThickness = "18";
        draft.Comment = "Несохранённый комментарий"; f.Model.ResultText = "Ручное письмо";
        var title = f.Model.OrderTitle; var bytes = await File.ReadAllBytesAsync(f.Store.FilePath, TestContext.Current.CancellationToken);
        using (var lease = new FileStream(f.Store.FilePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            f.Model.Search = "кальныйкомм"; Assert.Single(f.Model.Orders); Assert.Contains(savedComment, f.Model.Orders[0].SearchDetail);
            f.Model.Search = "НЕСОХРАНЁННЫЙ"; Assert.Empty(f.Model.Orders); // Lists reflect saved data.
            Assert.Equal(title, f.Model.OrderTitle); Assert.Same(draft, f.Model.SelectedDraft);
            Assert.Equal("Несохранённый комментарий", draft.Comment); Assert.True(f.Model.IsPlanning); Assert.Equal("18", f.Model.PlanThickness);
            Assert.Equal("Ручное письмо", f.Model.ResultText); f.Model.ClearSearchCommand.Execute(null); Assert.Single(f.Model.Orders);
        }
        Assert.Equal(bytes, await File.ReadAllBytesAsync(f.Store.FilePath, TestContext.Current.CancellationToken));
        f.Model.CancelPlanningCommand.Execute(null); f.Model.CancelDraftCommand.Execute(null); Assert.False(f.Model.HasPendingEdits);
        f.Model.Blcb = "777"; await f.Model.SaveHeaderCommand.ExecuteAsync(null);
        f.Model.Search = "777"; Assert.Single(f.Model.Orders); Assert.Null(f.Model.Error);
        var book = await f.Store.ReadAsync(TestContext.Current.CancellationToken);
        await f.Store.WriteAsync(book.Version, w => w with { Customers = w.Customers.Select(c => c with { Name = "ВнешнееПереименование" }).ToArray() }, TestContext.Current.CancellationToken);
        f.Model.Search = "ВнешнееПереименование"; Assert.Empty(f.Model.Orders);
        await f.Model.ReloadCommand.ExecuteAsync(null); Assert.Single(f.Model.Orders); Assert.Equal("ВнешнееПереименование", f.Model.Search);
    }

    [Fact]
    public void FullExistingOrderNumberIsADirectLookupAndAnAbsentNumberFallsBackToContentSearch()
    {
        var book = Sample(); var first = book.Orders[0] with { ReferenceNumber = "2026-10-24-143527" };
        var second = book.Orders[0] with { Id = Guid.NewGuid(), ReferenceNumber = first.ReferenceNumber + "-02" };
        var index = new WorkflowOrderSearchIndex(book with { Orders = [first, second] });
        foreach (var value in new[] { first.ReferenceNumber, "Заявка " + first.ReferenceNumber, "2026-10-24 14:35:27", "2026-10-24-14:35:27" })
            Assert.Equal(first.Id, index.FindExactOrder(new(value)));
        Assert.Equal(second.Id, index.FindExactOrder(new(second.ReferenceNumber)));
        Assert.Null(index.FindExactOrder(new("2026-10-24")));
        Assert.Null(index.FindExactOrder(new("2026-10-24-143527-03")));
        Assert.Null(index.FindExactOrder(new("ПТЕК 2026-10-24-143527")));
    }

    [Fact]
    public void SavedProjectionCanBeReusedAcrossQueriesAndFindsOrdersBeyondTheFirstPage()
    {
        var sample = Sample(); var original = sample.Orders[0]; const int count = 20_000;
        var orders = Enumerable.Range(0, count).Select(i => original with { Id = Guid.NewGuid(),
            ReferenceNumber = "2026-10-24-143527-" + (i + 2).ToString("00", System.Globalization.CultureInfo.InvariantCulture), Blcb = "Б" + i }).ToArray();
        var watch = Stopwatch.StartNew(); var index = new WorkflowOrderSearchIndex(sample with { Orders = orders });
        var buildMs = watch.ElapsedMilliseconds; watch.Restart();
        var query = new WorkflowSearchQuery("Б19999 0057 птек");
        var matched = index.Documents.Where(p => p.Value.Matches(query, out _)).Select(p => p.Key).ToArray();
        Assert.Equal([orders[^1].Id], matched); var elapsed = watch.ElapsedMilliseconds;
        Assert.DoesNotContain(index.Documents, p => p.Value.Matches(new("НетТакогоФрагмента"), out _));
        output.WriteLine($"Synthetic in-memory projection: {count} orders, build {buildMs} ms, filter {elapsed} ms. Not an Access/SMB benchmark.");
    }

    private static WorkflowWorkspace Sample()
    {
        var book = WorkflowTestData.CreateWorkspace(); var customer = new Customer(Guid.NewGuid(), "ПТЕК");
        var parameters = new CuttingParameters("Лазер", "Ст3", "Базовая", 2.5m, "Лист 1500×3000", 1, 15, 2, 5, 10, 1, false, "Чертёж №501") { MachineId = book.Machines[0].Id };
        var calculation = new CuttingCalculation(Guid.NewGuid(), 1, 1, 15, 2, 17);
        var current = new SpecificationState("1024-0057", new(2026, 10, 23), DateTimeOffset.Now,
            book.Employees[1].Id, parameters, calculation, null, null);
        current = current with {
            Report = new(Guid.NewGuid(), "", 1, "Эскиз-А*_[%]?.html", "", new("", "", "", "", "", "", "", "", "", "", "")) };
        var order = new WorkflowOrder(Guid.NewGuid(), 1, customer.Id, book.Employees[0].Id, book.Employees[1].Id,
            "123", 1, new(2026, 10, 24, 14, 35, 27, TimeSpan.Zero), DateTimeOffset.Now,
            [new(Guid.NewGuid(), current)]) { ReferenceNumber = "2026-10-24-143527-02", CreatedById = book.Employees[1].Id };
        return book with { Customers = [customer], Orders = [order] };
    }
}

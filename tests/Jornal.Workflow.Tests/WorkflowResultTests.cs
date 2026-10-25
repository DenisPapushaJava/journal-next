using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jornal.Core.Results;
using Jornal.Workflow.Core;
using Jornal.Workflow.Desktop;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class WorkflowResultTests
{
    private static SpecificationState State(decimal price = 1105m, bool chamfer = false, bool bending = false) => new("1025-014010",
        new(2026, 10, 25), DateTimeOffset.UtcNow, Guid.NewGuid(),
        new("Лазер", "Ст3", "Базовая", 6, "Лист 6×3000×1500", 1, 11, 5, 48.2m, 600, 1, bending, "Не включать в письмо") { HasChamfer = chamfer },
        new(Guid.NewGuid(), 100, 1, 1100, 5, price), null, "block-html/v1");

    [Theory]
    [InlineData(false, false, "")] [InlineData(true, false, "   ФАСКА")]
    [InlineData(false, true, "   ГИБКА")] [InlineData(true, true, "   ФАСКА   ГИБКА")]
    public void ExactLetterHasIndependentOperationsAndOneFinalNewline(bool chamfer, bool bending, string suffix)
    {
        var state = State(chamfer: chamfer, bending: bending);
        var text = WorkflowResultFormatter.Format([state], []);
        Assert.Equal("Лист 6x3000x1500,\t   Материал: Ст3,\t   1 шт.\t   Вес 48,2 кг.\t   Резка: 1105 ₽.     Время резки: 00:10\t   ЛАЗЕР" + suffix + "\n", text);
        var clipboard = ResultClipboardContent.FromText(text);
        Assert.Equal(text, clipboard.Text); Assert.Equal(text, ResultClipboardContent.FromText(clipboard.Text).Text);
        Assert.Contains("Резка:", WebUtility.HtmlDecode(clipboard.Html)); Assert.Contains("&nbsp;</span></div></div>", clipboard.Html);
        if (chamfer) { Assert.Contains("ФАСКА", WebUtility.HtmlDecode(clipboard.Html)); Assert.Contains(@"\u1060?", clipboard.Rtf); }
        Assert.DoesNotContain("Не включать", text); Assert.Equal(1105m, state.Calculation.TotalCost);
    }

    [Theory]
    [InlineData("0.0001", true, "500")] [InlineData("12.3456", true, "500")] [InlineData("499.9999", true, "500")]
    [InlineData("500.5", true, "500")] [InlineData("501.5", true, "502")]
    [InlineData("12.5", false, "12")] [InlineData("13.5", false, "14")] [InlineData("-1.5", false, "-2")]
    [InlineData("100000000000.6", false, "100000000001")]
    public void LetterRoundsToEvenAndAppliesMinimumWithoutChangingSavedPrice(string input, bool minimum, string expected)
    {
        var price = decimal.Parse(input, CultureInfo.InvariantCulture); var state = State(price);
        Assert.Contains("Резка: " + expected + " ₽.", WorkflowResultFormatter.Format([state], [], new(minimum)));
        Assert.Equal(price, state.Calculation.TotalCost);
    }

    [Fact]
    public void AlignmentUsesFinalPricesAndCurrentMachineNameWithRussianCasingBeforeMeasuring()
    {
        var first = State(0.0001m); var second = State(499.9999m);
        var lines = WorkflowResultFormatter.Format([first, second], []).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.All(lines, line => Assert.Contains("Резка: 500 ₽.", line));
        lines = WorkflowResultFormatter.Format([first, second with { Calculation = second.Calculation with { TotalCost = 1000 } }], [])
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("Резка:   500 ₽.", lines[0]); Assert.Contains("Резка: 1000 ₽.", lines[1]);
        var machine = WorkflowMachine.Defaults()[0] with { Name = "Лазер i № 1" };
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            var renamed = first with { Parameters = first.Parameters with { MachineId = machine.Id } };
            Assert.EndsWith("\t   ЛАЗЕР I № 1\n", WorkflowResultFormatter.Format([renamed], [machine]));
            Assert.Equal("Лазер", renamed.Parameters.Machine);
        }
        finally { CultureInfo.CurrentCulture = before; }
    }

    [Fact]
    public void CustomBlankUnknownWeightAndLongDurationStayReadableAndRowsKeepThicknessMaterialOrder()
    {
        var state = State() with { Parameters = State().Parameters with { BlankSize = "Полоса из остатка", WeightKg = null, DurationSeconds = 700001 } };
        var text = WorkflowResultFormatter.Format([state], []);
        Assert.StartsWith("Полоса из остатка,", text); Assert.Contains("Вес — кг.", text); Assert.Contains("Время резки: 194:27", text);
        var thin = state with { Parameters = state.Parameters with { ThicknessMm = 1, Material = "яя" } };
        var alphabetical = state with { Parameters = state.Parameters with { Material = "аа" } };
        var lines = WorkflowResultFormatter.Format([state, alphabetical, thin], []).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("Материал:   яя,", lines[0]); Assert.Contains("Материал:   аа,", lines[1]); Assert.Contains("Материал: Ст3,", lines[2]);
        Assert.Equal("", WorkflowResultFormatter.Format([], []));
    }

    [Fact]
    public async Task ChamferSurvivesRefreshRestartCancelAndTrashAndNeverChangesCuttingPrice()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var project = await f.AddAsync(); await ProjectFixture.WriteAsync(project);
        var review = await f.ReviewAsync(); var draft = review.Selected!.Draft!; Assert.False(draft.HasChamfer);
        var quote = draft.Quote; draft.HasChamfer = true; draft.HasBending = true; Assert.Equal(quote, draft.Quote);
        await review.SaveCommand.ExecuteAsync(null); Assert.Null(review.Error);
        await f.Model.ReloadCommand.ExecuteAsync(null); Assert.True(f.Model.SelectedDraft!.HasChamfer);
        var saved = f.Model.SelectedDraft.Saved!; f.Model.SelectedDraft.HasChamfer = false; Assert.True(f.Model.HasPendingEdits);
        f.Model.CancelDraftCommand.Execute(null); Assert.True(f.Model.SelectedDraft.HasChamfer); Assert.False(f.Model.HasPendingEdits);
        f.Model.GenerateResultCommand.Execute(null); Assert.EndsWith("   ФАСКА   ГИБКА\n", f.Model.ResultText);
        await ProjectFixture.WriteAsync(project, 20000);
        review = await f.ReviewAsync(); Assert.True(review.Selected!.Draft!.HasChamfer);
        review.Selected.Draft.HasChamfer = false;
        review = (await f.Model.CreateReportReviewAsync(TestContext.Current.CancellationToken, previous: review))!;
        Assert.False(review.Selected!.Draft!.HasChamfer); review.Selected.Draft.HasChamfer = true;
        await review.SaveCommand.ExecuteAsync(null); Assert.Null(review.Error);
        var version = (await f.Store.ReadAsync(TestContext.Current.CancellationToken)).Version;
        await f.Model.SaveSpecificationCommand.ExecuteAsync(null);
        Assert.Equal(version, (await f.Store.ReadAsync(TestContext.Current.CancellationToken)).Version);
        await f.Model.SetProjectTrashAsync(project.Id, true); await f.Model.SetProjectTrashAsync(project.Id, false);
        Assert.True(f.Model.SelectedDraft!.HasChamfer); Assert.True(f.Model.SelectedDraft.HasBending);
        var book = await f.Store.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(11, book.SchemaVersion); Assert.Equal(2105m, book.Orders.Single().CuttingTotal);
        Assert.Equal(saved.Calculation.PricePerMeter, book.Orders.Single().Specifications.Single().Current.Calculation.PricePerMeter);
        var legacy = JsonSerializer.SerializeToNode(book)!.AsObject();
        legacy["Orders"]![0]!["Specifications"]![0]!["Current"]!["Parameters"]!.AsObject().Remove("HasChamfer");
        var old = legacy.Deserialize<WorkflowWorkspace>()!; old.Validate();
        Assert.False(old.Orders.Single().Specifications.Single().Current.Parameters.HasChamfer);
        var other = await f.AddAsync(); await ProjectFixture.WriteAsync(other); review = await f.ReviewAsync();
        Assert.False(review.Candidates.Single(c => c.Project.Id == other.Id).Draft!.HasChamfer);
    }

    [Fact]
    public async Task PersonalPricingPersistsIndependentlyAndAppliesOnlyOnExplicitRegeneration()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var project = await f.AddAsync(); await ProjectFixture.WriteAsync(project); await f.AcceptAsync();
        var before = await f.Store.ReadAsync(TestContext.Current.CancellationToken); var draft = f.Model.SelectedDraft!;
        f.Model.ResultText = "Ручное письмо\n"; draft.Comment = "Несохранённый комментарий";
        using var settings = f.Model.CreateGeneralSettings(); using var competing = f.Model.CreateGeneralSettings();
        settings.ProfileName = "Профиль ещё не сохранён"; settings.ResultMinimumText = "2000";
        Assert.Null(settings.ResultPriceError); settings.UseResultMinimum = false;
        var preferences = new WorkflowPreferencesStore(Path.Combine(f.DirectoryPath, "preferences.json"));
        Assert.Equal(new WorkflowResultPricing(false, 2000), preferences.Load().ResultPricing);
        Assert.Equal("Ручное письмо\n", f.Model.ResultText); Assert.Same(draft, f.Model.SelectedDraft); Assert.True(f.Model.HasPendingEdits);
        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(await f.Store.ReadAsync(TestContext.Current.CancellationToken)));
        settings.ResultMinimumText = "-1"; Assert.NotNull(settings.ResultPriceError); settings.NormalizeResultPriceInput();
        Assert.Equal("2000", settings.ResultMinimumText); Assert.Null(settings.ResultPriceError);
        competing.ResultMinimumText = "800"; Assert.NotNull(competing.ResultPriceError); Assert.Equal("800", competing.ResultMinimumText);
        Assert.Equal(new WorkflowResultPricing(false, 2000), preferences.Load().ResultPricing);
        settings.UseResultMinimum = true; f.Model.CancelDraftCommand.Execute(null);
        f.Model.GenerateResultCommand.Execute(null); Assert.Contains("Резка: 2000 ₽.", f.Model.ResultText);
        f.Model.IsDarkTheme = !f.Model.IsDarkTheme; Assert.Equal(new WorkflowResultPricing(true, 2000), preferences.Load().ResultPricing);
        Assert.Equal(before.Orders.Single().CuttingTotal, (await f.Store.ReadAsync(TestContext.Current.CancellationToken)).Orders.Single().CuttingTotal);
        preferences.Update(p => p with { ResultPricing = new(false, -1) }); Assert.Equal(new WorkflowResultPricing(false, 500), preferences.Load().ResultPricing);
    }
}

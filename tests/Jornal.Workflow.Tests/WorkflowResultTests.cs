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
        new("Лазер", "Ст3", "Базовая", 6, "Лист 6×3000×1500", 1, 11, 5, 48.2m, 600, 1, bending, null) { HasChamfer = chamfer },
        new(Guid.NewGuid(), 100, 1, 1100, 5, price), null, "block-html/v1");

    [Theory]
    [InlineData(false, false, "")] [InlineData(true, false, "   ФАСКА")]
    [InlineData(false, true, "   ГИБКА")] [InlineData(true, true, "   ФАСКА   ГИБКА")]
    public void ExactLetterHasIndependentOperationsAndTwoFinalNewlines(bool chamfer, bool bending, string suffix)
    {
        var state = State(chamfer: chamfer, bending: bending);
        var text = WorkflowResultFormatter.Format([state], []);
        Assert.Equal("Лист 6x3000x1500,\t   Материал: Ст3,\t   1 шт.\t   Вес 48,2 кг.\t   Резка: 1105 ₽.     Время резки: 00:10\t   ЛАЗЕР" + suffix + "\n\n", text);
        var clipboard = ResultClipboardContent.FromText(text);
        Assert.Equal(text, clipboard.Text); Assert.Equal(text, ResultClipboardContent.FromText(clipboard.Text).Text);
        Assert.Contains("Резка:", WebUtility.HtmlDecode(clipboard.Html)); Assert.Contains("&nbsp;</span></div></div>", clipboard.Html);
        using var document = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(clipboard.Html);
        var paragraphs = document.Body!.FirstElementChild!.Children;
        Assert.Equal(3, paragraphs.Length);
        Assert.NotNull(paragraphs[1].QuerySelector("br"));
        Assert.Equal("\u00a0", paragraphs[2].TextContent);
        Assert.EndsWith("\\par \n\\par \n}", clipboard.Rtf);
        if (chamfer) { Assert.Contains("ФАСКА", WebUtility.HtmlDecode(clipboard.Html)); Assert.Contains(@"\u1060?", clipboard.Rtf); }
        Assert.Equal(1105m, state.Calculation.TotalCost);
    }

    [Fact]
    public void LetterAppendsOneLinePerCommentInCalculationOrder()
    {
        var first = State();
        var thin = first with { Parameters = first.Parameters with { ThicknessMm = 2.5m, Material = "09Г2С", Comment = "  Упаковать отдельно.\r\n\r\nПромаркировать комплект.  " } };
        var thick = first with { Parameters = first.Parameters with { ThicknessMm = 10, Comment = "Согласовать замену." } };
        var noComments = WorkflowResultFormatter.Format([first, thin with { Parameters = thin.Parameters with { Comment = null } },
            thick with { Parameters = thick.Parameters with { Comment = null } }], []);
        var letter = WorkflowResultFormatter.Format([thick, first, thin], []);
        Assert.Equal(noComments + "2,5 мм, 09Г2С: Упаковать отдельно. Промаркировать комплект.\n10 мм, Ст3: Согласовать замену.\n\n", letter);
        Assert.Contains("Промаркировать комплект", WebUtility.HtmlDecode(ResultClipboardContent.FromText(letter).Html));
    }

    [Fact]
    public async Task SavingOnlyACommentRefreshesAnAutomaticLetterWithoutReloadingReportsOrChangingPrices()
    {
        using var f = new ProjectFixture();
        await f.InitializeAsync();
        var project = await f.AddAsync();
        await ProjectFixture.WriteAsync(project);
        await f.AcceptAsync();
        f.Model.GenerateResultCommand.Execute(null);
        var initialLetter = f.Model.ResultText;
        var initial = f.Model.SelectedDraft!.Saved!;
        f.Model.OrderTabIndex = 0;
        using (var settings = f.Model.CreateGeneralSettings()) settings.ResultMinimumText = "2000";
        await ProjectFixture.WriteAsync(project, 20_000);

        f.Model.SelectedDraft!.Comment = "Отдельная упаковка.";
        await f.Model.SaveSpecificationCommand.ExecuteAsync(null);
        Assert.Null(f.Model.Error);
        Assert.Equal(initialLetter + "6 мм, Ст3: Отдельная упаковка.\n\n", f.Model.ResultText);
        Assert.False(f.Model.ResultIsStale);
        Assert.Equal(0, f.Model.OrderTabIndex);
        Assert.Equal(initial.Calculation, f.Model.SelectedDraft!.Saved!.Calculation);
        Assert.Equal(initial.Report!.Sha256, f.Model.SelectedDraft.Saved.Report!.Sha256);

        f.Model.SelectedDraft.Comment = "";
        await f.Model.SaveSpecificationCommand.ExecuteAsync(null);
        Assert.Equal(initialLetter, f.Model.ResultText);
        Assert.False(f.Model.ResultIsStale);
    }

    [Fact]
    public async Task EachSavedCommentUpdatesTheLetterWhileAnotherProjectHasADraft()
    {
        var token = TestContext.Current.CancellationToken;
        using var f = new ProjectFixture();
        await f.InitializeAsync();
        var laser = await f.AddAsync();
        var plasma = await f.AddAsync("Плазма");
        await ProjectFixture.WriteAsync(laser);
        await ProjectFixture.WriteAsync(plasma);
        await f.AcceptAsync();
        f.Model.GenerateResultCommand.Execute(null);
        var initialLetter = f.Model.ResultText;
        Assert.NotEmpty(initialLetter);

        var laserDraft = f.Model.Drafts.Single(d => d.Id == laser.Id);
        var plasmaDraft = f.Model.Drafts.Single(d => d.Id == plasma.Id);
        laserDraft.Comment = "Детали упаковать отдельно.";
        plasmaDraft.Comment = "Добавить маркировку.";
        f.Model.SelectedProject = f.Model.Projects.Single(row => row.Project.Id == laser.Id);
        await f.Model.SaveSpecificationCommand.ExecuteAsync(null);

        Assert.Null(f.Model.Error);
        Assert.False(f.Model.ResultIsStale);
        Assert.Equal(initialLetter + "6 мм, Ст3: Детали упаковать отдельно.\n\n", f.Model.ResultText);
        Assert.True(plasmaDraft.IsDirty);
        var afterFirstSave = (await f.Store.ReadAsync(token)).Orders.Single();
        Assert.Null(afterFirstSave.Specifications.Single(s => s.Id == plasma.Id).Current.Parameters.Comment);

        f.Model.SelectedProject = f.Model.Projects.Single(row => row.Project.Id == plasma.Id);
        await f.Model.SaveSpecificationCommand.ExecuteAsync(null);

        Assert.Null(f.Model.Error);
        Assert.False(f.Model.ResultIsStale);
        var saved = (await f.Store.ReadAsync(token)).Orders.Single();
        Assert.Equal("Добавить маркировку.", saved.Specifications.Single(s => s.Id == plasma.Id).Current.Parameters.Comment);
        Assert.Equal(WorkflowResultFormatter.Format(saved.ActiveSpecifications.Select(s => s.Current), []), f.Model.ResultText);
    }

    [Fact]
    public async Task MissingArchiveAllowsCommentSaveButStillGuardsChangesToTheCalculation()
    {
        var token = TestContext.Current.CancellationToken;
        using var f = new ProjectFixture();
        await f.InitializeAsync();
        var project = await f.AddAsync();
        await ProjectFixture.WriteAsync(project);
        await f.AcceptAsync();
        var order = (await f.Store.ReadAsync(token)).Orders.Single();
        var initial = order.Specifications.Single().Current;
        File.Delete(initial.Report!.ArchivePath);

        f.Model.SelectedDraft!.Comment = "Позвонить перед отгрузкой.";
        await f.Model.SaveSpecificationCommand.ExecuteAsync(null);

        Assert.Null(f.Model.Error);
        var saved = (await f.Store.ReadAsync(token)).Orders.Single();
        Assert.Equal(order.Version + 1, saved.Version);
        var state = saved.Specifications.Single().Current;
        Assert.Equal(initial with
        {
            Parameters = initial.Parameters with { Comment = "Позвонить перед отгрузкой." },
            SavedAt = state.SavedAt,
            AuthorId = state.AuthorId
        }, state);
        Assert.Contains("6 мм, Ст3: Позвонить перед отгрузкой.", f.Model.ResultText);

        var draft = f.Model.SelectedDraft!;
        draft.Coefficient = "2";
        draft.Comment = "Проверить количество.";
        await f.Model.SaveSpecificationCommand.ExecuteAsync(null);

        Assert.NotNull(f.Model.Error);
        Assert.Same(draft, f.Model.SelectedDraft);
        Assert.Equal("Проверить количество.", draft.Comment);
        var unchanged = (await f.Store.ReadAsync(token)).Orders.Single();
        Assert.Equal(saved.Version, unchanged.Version);
        Assert.Equal(state, unchanged.Specifications.Single().Current);
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
            Assert.EndsWith("\t   ЛАЗЕР I № 1\n\n", WorkflowResultFormatter.Format([renamed], [machine]));
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
        var formatted = WorkflowResultFormatter.Format([state, alphabetical, thin], []);
        Assert.EndsWith("\n\n", formatted); Assert.DoesNotContain("\n\n\n", formatted);
        var lines = formatted.Split('\n', StringSplitOptions.RemoveEmptyEntries);
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
        f.Model.GenerateResultCommand.Execute(null); Assert.EndsWith("   ФАСКА   ГИБКА\n\n", f.Model.ResultText);
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
        Assert.Equal(WorkflowWorkspace.CurrentSchema, book.SchemaVersion); Assert.Equal(2105m, book.Orders.Single().CuttingTotal);
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
        var replacement = Assert.IsType<WorkflowResultGeneration>(f.Model.PrepareResultGeneration()); Assert.True(replacement.NeedsConfirmation);
        Assert.True(f.Model.ApplyResultGeneration(replacement)); Assert.Contains("Резка: 2000 ₽.", f.Model.ResultText);
        f.Model.IsDarkTheme = !f.Model.IsDarkTheme; Assert.Equal(new WorkflowResultPricing(true, 2000), preferences.Load().ResultPricing);
        Assert.Equal(before.Orders.Single().CuttingTotal, (await f.Store.ReadAsync(TestContext.Current.CancellationToken)).Orders.Single().CuttingTotal);
        preferences.Update(p => p with { ResultPricing = new(false, -1) }); Assert.Equal(new WorkflowResultPricing(false, 500), preferences.Load().ResultPricing);
    }
}

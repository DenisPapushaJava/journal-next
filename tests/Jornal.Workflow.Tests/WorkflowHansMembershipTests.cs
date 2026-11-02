using System.Text.Json;
using Jornal.Workflow.Core;
using Jornal.Workflow.Desktop;
using Jornal.Workflow.Infrastructure;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class WorkflowHansMembershipTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private sealed class Editor : IWorkflowWordMapEditor
    {
        public int Inspections; public int Preparations; public bool Unavailable;
        public readonly List<WordMapMatch?> Policies = [];
        public Task<WordMapReference[]?> InspectAsync(byte[] source, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Inspections++;
            if (Unavailable) throw new WordMapUnavailableException("Word недоступен");
            return Task.FromResult(JsonSerializer.Deserialize<WordMapReference[]>(source));
        }
        public Task<WordMapEdit?> PrepareAsync(byte[] source, string name, string caption, CancellationToken token) => Prepare(source, name, null);
        public Task<WordMapEdit?> PrepareMatchedAsync(byte[] source, string name, string caption, WordMapMatch match, CancellationToken token) => Prepare(source, name, match);
        private Task<WordMapEdit?> Prepare(byte[] bytes, string name, WordMapMatch? match)
        {
            Preparations++; Policies.Add(match);
            var refs = JsonSerializer.Deserialize<WordMapReference[]>(bytes)!;
            var tables = refs.Select((r, i) => Table(r.Order, r.Program, i * 10)).ToArray();
            var comments = WorkflowWordMapRules.Comments(tables, name, match);
            return Task.FromResult<WordMapEdit?>(comments is null ? null : new(null, refs.Length, 0));
        }
    }
    private sealed class Fixture : IDisposable
    {
        public readonly ShopFixture Inner = new("HansLaser");
        public readonly CuttingProject Project; public readonly WorkflowOrder Order; public readonly WorkflowWorkspace Book;
        public readonly Editor Word = new();
        public string Folder => Inner.Source;
        public string Own => Path.GetFileName(Project.NativePath!);
        public Fixture(bool second = true, bool identical = false)
        {
            Project = Inner.Project with { NativePath = Path.ChangeExtension(Inner.Project.NativePath!, ".Dsp") };
            File.Move(Inner.Project.NativePath!, Project.NativePath!);
            foreach (var path in Directory.GetFiles(Folder).Where(p => Path.GetExtension(p).Equals(".DOC", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(p).Equals(".GNC", StringComparison.OrdinalIgnoreCase))) File.Delete(path);
            if (second) File.WriteAllText(Path.Combine(Folder, "Другой.Dsp"), identical ? File.ReadAllText(Project.NativePath!) : "Different DSP");
            Order = Inner.Order with { Projects = [Project] }; Book = Inner.Book with { Orders = [Order] };
        }
        public void Map(string name, WordMapReference first, WordMapReference? second = null) =>
            File.WriteAllText(Path.Combine(Folder, name), JsonSerializer.Serialize(second is null ? new[] { first } : new[] { first, second }));
        public void Up(string name) => File.WriteAllText(Path.Combine(Folder, name), "UP " + name);
        public WorkflowShopTransferViewModel Model() => new(Inner.ShopRoot, "hans-test", Order, Book, _ => Task.CompletedTask, _ => Task.CompletedTask, wordMapEditor: Word);
        public void Dispose() => Inner.Dispose();
    }
    private static WordMapCell[] Table(string order, string up, int offset = 0) =>
        [new(offset, 1, "Заказ"), new(offset + 1, 1, order), new(offset + 2, 2, "Файл УП"), new(offset + 3, 2, up), new(offset + 4, 3, "Комментарий"), new(offset + 5, 3, "")];

    [Fact]
    public async Task MultipleDspAutomaticallySendOnlyLinkedCurrentMapsAndProgramsWithArbitraryFileNames()
    {
        using var f = new Fixture(); f.Up("Произвольное имя 1.GNC"); f.Up("Чужая.GNC");
        f.Map("Моя карта.DOC", new(f.Own[..^4], @"C:\CAM\Произвольное имя 1")); f.Map("Старый раскрой.DOC", new("Другой.Dsp", "Чужая.GNC"));
        var before = Directory.GetFiles(f.Folder).ToDictionary(p => Path.GetFileName(p)!, File.ReadAllBytes);
        var vm = f.Model(); await vm.RefreshAsync(); Assert.True(vm.CanStartAutomatically, vm.Projects.Single().SelectionHelp);
        Assert.Equal(new[] { "Моя карта.DOC", "Произвольное имя 1.GNC" }, vm.Projects.Single().Files.Where(f => f.IsSelected).Select(f => f.Name).Order().ToArray());
        await vm.CopyAsync(); Assert.True(vm.TextCopied, vm.Error); Assert.Equal(2, f.Word.Inspections); Assert.Equal(1, f.Word.Preparations);
        var destination = vm.Projects.Single().Destination;
        Assert.True(File.Exists(Path.Combine(destination, "Моя карта.DOC"))); Assert.False(File.Exists(Path.Combine(destination, "Старый раскрой.DOC")));
        Assert.False(File.Exists(Path.Combine(destination, "Другой.Dsp"))); Assert.False(File.Exists(Path.Combine(destination, "Чужая.GNC")));
        foreach (var (name, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(f.Folder, name!)));
    }

    [Fact]
    public async Task EmptyOrderCanUseAUniqueProgramLinkAndSharedFolderInspectionIsCachedAcrossProjects()
    {
        using var f = new Fixture(); f.Up("Моя.GNC"); f.Up("Чужая.GNC");
        f.Map("Опорная.DOC", new(f.Own, "Моя")); f.Map("Та же карта.DOC", new("", "Моя")); // Identical UP, differing DOC bytes: competing maps need a choice.
        var vm = f.Model(); await vm.RefreshAsync(); Assert.False(vm.CanStartAutomatically);
        Assert.Contains(vm.Projects.Single().Files, c => c.File.ConfirmedProjectLink && c.File.Reason.Contains("Несколько разных DOC", StringComparison.Ordinal));
        File.Delete(Path.Combine(f.Folder, "Опорная.DOC"));
        f.Up("Дополнительная.GNC");
        f.Map("Моя многостраничная.DOC", new(f.Own, "Моя"), new("", "Дополнительная"));
        File.Delete(Path.Combine(f.Folder, "Та же карта.DOC")); f.Map("Чужая.DOC", new("Другой.Dsp", "Чужая"));
        var other = f.Project with { Id = Guid.NewGuid(), ItemId = Guid.NewGuid(), Name = "Другой", NativePath = Path.Combine(f.Folder, "Другой.Dsp") };
        var order = f.Order with { Projects = [f.Project, other], Items = [.. f.Order.Items, new(other.ItemId, 2, other.Id)],
            Specifications = [.. f.Order.Specifications, f.Order.Specifications.Single() with { Id = other.Id }] };
        f.Word.Inspections = 0;
        var plans = await WorkflowShopDiscovery.DiscoverAsync(f.Inner.ShopRoot, order, f.Book with { Orders = [order] }, Token, f.Word);
        Assert.All(plans, p => Assert.False(p.NeedsReview)); Assert.Equal(2, f.Word.Inspections); // Each physical DOC is inspected once per batch.
        var current = plans.Single(p => p.Project.Id == f.Project.Id); Assert.True(current.Files.Single(f => f.Name == "Моя многостраничная.DOC").ConfirmedProjectLink);
    }

    [Fact]
    public async Task UnknownOrderRequiresSelectionOnlyWhenItsProgramCannotIdentifyTheProjectAndManualSelectionCanBeSigned()
    {
        using var f = new Fixture(); f.Up("Моя.GNC"); f.Map("Карта.DOC", new("Своё название", "Моя"));
        var vm = f.Model(); await vm.RefreshAsync(); Assert.False(vm.CanStartAutomatically);
        var row = vm.Projects.Single(); Assert.Contains("не установлена", row.Files.Single(c => c.File.Kind == ShopFileKind.Map).File.Reason);
        foreach (var choice in row.Files) choice.IsSelected = true;
        row.Reviewed = true; Assert.True(vm.CanCopy);
        await vm.CopyAsync(); Assert.True(vm.TextCopied, vm.Error); Assert.True(Assert.Single(f.Word.Policies)!.AllowUnknownOrder);
    }

    [Fact]
    public void ExplicitConfirmationStillCannotOverrideACardThatNamesAnotherKnownDspOrASharedProgramConflict()
    {
        var match = new WordMapMatch(["Мой.Dsp"], ["Чужой.Dsp"], AllowUnknownOrder: true);
        Assert.Null(WorkflowWordMapRules.Comments([Table("Чужой", "1")], "Мой.Dsp", match));
        Assert.Throws<WorkflowValidationException>(() => WorkflowWordMapRules.Comments([Table("Мой", "1"), Table("Чужой", "2", 10)], "Мой.Dsp", match));
        Assert.Equal(new[] { new WordMapReference("Мой", "1"), new WordMapReference("", "2") }, WorkflowWordMapRules.References([Table("Мой", "1"), Table("", "2", 10)]));
    }

    [Fact]
    public async Task IdenticalDspCopyDoesNotDisableAutomationOrOpenWordForInspectionAndChangedProofStopsCopy()
    {
        using var f = new Fixture(identical: true); f.Up("Лист.GNC"); f.Map("Карта.DOC", new("Другой.Dsp", "Лист"));
        var vm = f.Model(); await vm.RefreshAsync(); Assert.True(vm.CanStartAutomatically); Assert.Equal(0, f.Word.Inspections);
        await vm.CopyAsync(); Assert.True(vm.TextCopied, vm.Error); Assert.Contains("Другой.Dsp", Assert.Single(f.Word.Policies)!.CurrentProjects);
        using var g = new Fixture(identical: true); g.Up("Лист.GNC"); g.Map("Карта.DOC", new(g.Own, "Лист"));
        var changed = g.Model(); await changed.RefreshAsync(); File.AppendAllText(Path.Combine(g.Folder, "Другой.Dsp"), "changed");
        await changed.CopyAsync(); Assert.False(changed.AllPrepared); Assert.Contains("Изменился DSP", changed.Error); Assert.Equal(0, g.Word.Preparations);
    }

    [Fact]
    public async Task UnavailableWordAndMissingOrConflictingProgramLinksProduceSpecificQuestionsWithoutDiscardingFiles()
    {
        using var f = new Fixture(); f.Word.Unavailable = true; f.Up("Моя.GNC"); f.Map("1.DOC", new(f.Own, "Моя")); f.Map("2.DOC", new(f.Own, "Моя"));
        var vm = f.Model(); await vm.RefreshAsync(); Assert.False(vm.CanStartAutomatically); Assert.Equal(1, f.Word.Inspections);
        Assert.All(vm.Projects.Single().Files, c => Assert.False(c.File.Ignored));
        f.Word.Unavailable = false; f.Map("1.DOC", new(f.Own, "Не экспортирована")); f.Map("2.DOC", new("Другой.Dsp", "Моя"));
        await vm.RefreshAsync(); Assert.False(vm.CanStartAutomatically); Assert.Contains("не найдена", vm.Projects.Single().Files.Single(c => c.Name == "1.DOC").File.Reason);
        f.Map("1.DOC", new(f.Own, "Моя")); await vm.RefreshAsync();
        Assert.Contains("разных DSP", vm.Projects.Single().Files.Single(c => c.Name == "1.DOC").File.Reason);
    }

    [Fact]
    public async Task SingleDspRetainsFastAutomaticFolderRuleWithoutInspectionEvenWhenFieldsAreEmpty()
    {
        using var f = new Fixture(second: false); f.Up("Лист.GNC"); f.Map("Карта.DOC", new("", ""));
        var vm = f.Model(); await vm.RefreshAsync(); Assert.True(vm.CanStartAutomatically); Assert.Equal(0, f.Word.Inspections);
        await vm.CopyAsync(); Assert.True(vm.TextCopied, vm.Error); Assert.Null(Assert.Single(f.Word.Policies));
    }

    [Fact]
    public async Task ANewDspDoesNotBlockAnUnambiguousExistingSelectionButNewForeignEvidenceCannotBeOverriddenByOldConfirmation()
    {
        using var f = new Fixture(second: false); f.Up("Лист.GNC"); f.Map("Карта.DOC", new(f.Own, "Лист"));
        var vm = f.Model(); await vm.RefreshAsync(); File.WriteAllText(Path.Combine(f.Folder, "Другой.Dsp"), "another project");
        await vm.CopyAsync(); Assert.True(vm.TextCopied, vm.Error);
        using var g = new Fixture(second: false); g.Up("Лист.GNC"); g.Map("Не заполнена.DOC", new("", "Лист"));
        var stale = g.Model(); await stale.RefreshAsync(); stale.Projects.Single().Reviewed = true;
        File.WriteAllText(Path.Combine(g.Folder, "Другой.Dsp"), "another project"); g.Map("Известная чужая.DOC", new("Другой.Dsp", "Лист"));
        await stale.CopyAsync(); Assert.False(stale.AllPrepared); Assert.Contains("теперь связан с другим DSP", stale.Error); Assert.Equal(0, g.Word.Preparations);
    }

    [Fact]
    public async Task DirectProjectSigningUsesTheSameMembershipAndNeverTouchesKnownForeignMaps()
    {
        using var f = new Fixture(); f.Up("Лист.GNC"); f.Up("Другой.GNC");
        f.Map("Моя.DOC", new(f.Own, "Лист")); f.Map("Чужая.DOC", new("Другой.Dsp", "Другой"));
        var results = await WorkflowMapSigning.SignAsync(f.Order, f.Book, f.Project.Id, _ => Task.CompletedTask, Token, f.Word);
        Assert.Single(results); Assert.False(results[0].NeedsAttention); Assert.EndsWith("Моя.DOC", results[0].FilePath); Assert.Equal(1, f.Word.Preparations);
    }

    [Fact]
    public async Task AProjectKeptOpenExclusivelyByCamDoesNotPreventAutomaticDocAndGncMatching()
    {
        using var f = new Fixture(); f.Up("Лист.GNC"); f.Map("Карта.DOC", new(f.Own, "Лист"));
        using var opened = new FileStream(f.Project.NativePath!, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var vm = f.Model(); await vm.RefreshAsync(); Assert.True(vm.CanStartAutomatically, vm.Projects.Single().SelectionHelp);
        await vm.CopyAsync(); Assert.True(vm.TextCopied, vm.Error);
    }

    [Fact]
    public async Task UnrecognisedDocIsNeverSilentlyDiscardedAndGeneratedLabelCaseOrColonDoNotAffectRecognition()
    {
        using var f = new Fixture(); f.Up("Лист.GNC"); f.Map("Карта.DOC", new(f.Own, "Лист"));
        File.WriteAllText(Path.Combine(f.Folder, "Неясный.DOC"), "[]");
        var vm = f.Model(); await vm.RefreshAsync(); Assert.False(vm.CanStartAutomatically);
        Assert.False(vm.Projects.Single().Files.Single(c => c.Name == "Неясный.DOC").File.Ignored);
        var table = Table(f.Own, "Лист").Select(c => c with { Text = c.Text is "Заказ" or "Файл УП" or "Комментарий" ? c.Text.ToLowerInvariant() + ":" : c.Text }).ToArray();
        Assert.Equal(new[] { new WordMapReference(f.Own, "Лист") }, WorkflowWordMapRules.References([table]));
    }
}

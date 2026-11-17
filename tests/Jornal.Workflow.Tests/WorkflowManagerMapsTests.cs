using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Jornal.Workflow.Core;
using Jornal.Workflow.Desktop;
using Jornal.Workflow.Infrastructure;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class WorkflowManagerMapsTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [AvaloniaFact]
    public async Task HansMenuChecksForTwoSourcesAndRefreshesAnUnchangedBundleWithoutSigning()
    {
        var word = new ShopWordEditor();
        using var f = new ProjectFixture(wordMapEditor: word);
        await f.InitializeAsync();
        var laser = await f.AddAsync("Лазер");
        var hans = await f.AddAsync("HansLaser");
        var folder = Path.GetDirectoryName(hans.NativePath!)!;
        var first = Path.Combine(folder, "Лист-1.DOC");
        var second = Path.Combine(folder, "Лист-2.DOC");
        await File.WriteAllTextAsync(first, "first", Token);
        var alternate = Path.Combine(folder, "Лист-1.Gnf.DOC");
        await File.WriteAllTextAsync(alternate, "alternate layout", Token);
        var window = new WorkflowWindow(new(Path.Combine(f.DirectoryPath, "preferences.json"))) { DataContext = f.Model };
        try
        {
            window.Show();
            await Until(() => !f.Model.IsBusy);
            var rows = f.Model.Projects;
            var laserRow = rows.Single(r => r.Project.Id == laser.Id);
            Assert.False(laserRow.HasManagerMapAction);
            var hansRow = rows.Single(r => r.Project.Id == hans.Id);
            var grid = window.FindControl<DataGrid>("WorkflowProjectsGrid")!;
            grid.ScrollIntoView(hansRow, null);
            Dispatcher.UIThread.RunJobs();
            var button = grid.GetVisualDescendants().OfType<Button>().Single(b => ReferenceEquals(b.DataContext, hansRow) && b.Flyout is MenuFlyout);
            var menu = Assert.IsType<MenuFlyout>(button.Flyout);
            menu.ShowAt(button); Dispatcher.UIThread.RunJobs();
            var action = menu.Items.OfType<MenuItem>().Single(m => Equals(m.Header, "Объединить карты раскроя"));
            Assert.True(action.IsVisible);
            Assert.False(action.IsEnabled);
            menu.Hide();
            await File.WriteAllTextAsync(second, "second", Token);
            menu.ShowAt(button);
            await Until(() => action.IsEnabled);
            action.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); menu.Hide();
            var output = Path.Combine(folder, hans.Name + "-Блок-Карта.doc");
            await Until(() => File.Exists(output) && !f.Model.IsPreparingManagerMaps);
            Assert.Contains(Path.GetFileName(output), f.Model.ManagerMapsNotice);
            Assert.Equal("first", await File.ReadAllTextAsync(first, Token));
            Assert.Equal("second", await File.ReadAllTextAsync(second, Token));
            Assert.DoesNotContain("alternate layout", await File.ReadAllTextAsync(output, Token));
            Assert.Equal("alternate layout", await File.ReadAllTextAsync(alternate, Token));
            await f.Model.MergeManagerMapsAsync(hansRow, Token);
            Assert.Equal(1, word.BundleCalls);
            Assert.Equal(0, word.Calls);
            Assert.Equal([Path.GetFileName(hans.NativePath!)], word.LastManagerMatch!.CurrentProjects);
            Assert.Equal(["Лист-1.DOC", "Лист-2.DOC"], ShopFileSystem.CandidateNames(folder, [".doc"], hans: true));
            await File.WriteAllTextAsync(second, "updated second card", Token);
            await f.Model.MergeManagerMapsAsync(hansRow, Token);
            Assert.Contains("updated second card", await File.ReadAllTextAsync(output, Token));
            Assert.Equal(2, word.BundleCalls);
            await File.WriteAllTextAsync(output, "manager edits", Token);
            await f.Model.MergeManagerMapsAsync(hansRow, Token);
            Assert.Equal("manager edits", await File.ReadAllTextAsync(output, Token));
            Assert.Contains("изменён вручную", f.Model.ManagerMapsNotice);
            Assert.Equal(2, word.BundleCalls);
            var order = Assert.Single((await f.Store.ReadAsync(Token)).Orders);
            Assert.True(string.IsNullOrEmpty(order.Blcb));
            Assert.Null(order.ShopHandoff);
        }
        finally { WorkflowTestWindows.Close(window); }
    }

    [AvaloniaTheory]
    [InlineData(0, true, false)]
    [InlineData(1, false, false)]
    [InlineData(1, true, false)]
    [InlineData(2, true, false)]
    [InlineData(2, true, true)]
    public async Task CalculateKeepsTheSpecificationAndPriceRegardlessOfManagerMapAvailability(int cardCount, bool pdf, bool wordFails)
    {
        var word = new ShopWordEditor { FailBundle = wordFails };
        using var f = new ProjectFixture(wordMapEditor: word);
        await f.InitializeAsync();
        var hans = await f.AddAsync("HansLaser");
        var folder = Path.GetDirectoryName(hans.NativePath!)!;
        var report = Path.ChangeExtension(hans.NativePath!, ".pdf");
        WriteReport(hans, report);
        var reportBytes = await File.ReadAllBytesAsync(report, Token);
        for (var i = 1; i <= cardCount; i++)
            await File.WriteAllTextAsync(Path.Combine(folder, $"Лист-{i}.DOC"), $"card {i}", Token);
        using (var settings = f.Model.CreateGeneralSettings())
        {
            Assert.True(settings.ManagerMapsDoc);
            if (pdf)
            {
                settings.ManagerMapsPdf = true;
                await settings.SaveCommand.ExecuteAsync(null);
            }
            Assert.Null(settings.Error);
        }
        var preferences = new WorkflowPreferencesStore(Path.Combine(f.DirectoryPath, "preferences.json"));
        Assert.Equal(pdf, preferences.Load().ManagerMapsAsPdf);
        Assert.False(preferences.Load().HansMapsAsPdf);
        var window = new WorkflowWindow(preferences) { DataContext = f.Model };
        try
        {
            window.Show();
            await Until(() => !f.Model.IsBusy);
            Assert.True(f.Model.CanCalculateOrder, f.Model.CalculationNotice);
            window.FindControl<Button>("RefreshWorkflowReports")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var shouldMerge = cardCount >= (pdf ? 1 : 2);
            await Until(() => !f.Model.IsBusy && !f.Model.IsPreparingManagerMaps && f.Model.ResultText.Length > 0 && word.BundleCalls == (shouldMerge ? 1 : 0),
                () => string.Join(" | ", f.Model.CalculationNotice, f.Model.Error, f.Model.ManagerMapsNotice, string.Join(", ", window.OwnedWindows.Select(w => w.Title))));
            var order = Assert.Single((await f.Store.ReadAsync(Token)).Orders);
            Assert.NotEmpty(order.Specifications);
            Assert.NotEmpty(f.Model.ResultText);
            Assert.Null(order.ShopHandoff);
            var output = Path.Combine(folder, hans.Name + "-Блок-Карта.pdf");
            Assert.Equal(shouldMerge && !wordFails, File.Exists(output));
            Assert.Equal(reportBytes, await File.ReadAllBytesAsync(report, Token));
            Assert.Equal([report], WorkflowReportFiles.ListReports(folder, hans.ImportProfile).Select(file => file.Path));
            if (!shouldMerge)
            {
                Assert.Empty(f.Model.ManagerMapsNotice);
                Assert.Null(f.Model.Error);
                Assert.Empty(window.OwnedWindows);
                Assert.False(Directory.Exists(Path.Combine(folder, WorkflowManagerMapBundle.MarkerFolder)));
            }
            else if (wordFails) Assert.Contains("Расчёт сохранён", f.Model.ManagerMapsNotice);
            else Assert.Contains(Path.GetFileName(output), f.Model.ManagerMapsNotice);
            if (cardCount == 1 && pdf)
            {
                File.Delete(output);
                var row = Assert.Single(f.Model.Projects);
                var grid = window.FindControl<DataGrid>("WorkflowProjectsGrid")!;
                grid.ScrollIntoView(row, null);
                Dispatcher.UIThread.RunJobs();
                var button = grid.GetVisualDescendants().OfType<Button>().Single(b => ReferenceEquals(b.DataContext, row) && b.Flyout is MenuFlyout);
                var menu = Assert.IsType<MenuFlyout>(button.Flyout);
                menu.ShowAt(button);
                await Until(() => row.CanMergeManagerMaps);
                var action = Assert.Single(menu.Items.OfType<MenuItem>(), item => Equals(item.Header, "Преобразовать карту в PDF"));
                Assert.True(action.IsEnabled);
                action.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                menu.Hide();
                await Until(() => !f.Model.IsPreparingManagerMaps && File.Exists(output));
                Assert.Equal(2, word.BundleCalls);
            }
        }
        finally
        {
            foreach (var child in window.OwnedWindows.ToArray()) child.Close();
            WorkflowTestWindows.Close(window);
        }
    }

    [AvaloniaTheory]
    [InlineData("complete")]
    [InlineData("cancel")]
    [InlineData("close")]
    [InlineData("exclude")]
    public async Task AutomaticWordBundleKeepsTheJournalAvailableAndCanBeCancelled(string outcome)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var word = new ShopWordEditor
        {
            BeforeBundleAsync = async token =>
            {
                using var registration = token.Register(() => cancelled.TrySetResult());
                started.TrySetResult();
                await resume.Task.WaitAsync(token);
            }
        };
        using var f = new ProjectFixture(wordMapEditor: word);
        await f.InitializeAsync();
        var initialOrder = f.Model.SelectedOrder!.Order.Id;
        var hans = await f.AddAsync("HansLaser");
        var folder = Path.GetDirectoryName(hans.NativePath!)!;
        WriteReport(hans, Path.ChangeExtension(hans.NativePath!, ".pdf"));
        await File.WriteAllTextAsync(Path.Combine(folder, "Лист-1.DOC"), "first", Token);
        await File.WriteAllTextAsync(Path.Combine(folder, "Лист-2.DOC"), "second", Token);
        var window = new WorkflowWindow(new(Path.Combine(f.DirectoryPath, "preferences.json"))) { DataContext = f.Model };
        try
        {
            window.Show();
            await Until(() => !f.Model.IsBusy);
            window.FindControl<Button>("RefreshWorkflowReports")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => started.Task.IsCompleted);
            Assert.True(f.Model.IsPreparingManagerMaps);
            Assert.True(f.Model.CanUse);
            Assert.True(f.Model.CanBrowseWorkspace);
            Assert.True(f.Model.CanCloseWorkspace);
            Assert.NotEmpty(f.Model.ResultText);

            var output = Path.Combine(folder, hans.Name + "-Блок-Карта.doc");
            var cancel = window.FindControl<Button>("CancelWorkflowManagerMaps")!;
            Assert.True(cancel.IsVisible);
            if (outcome == "complete")
            {
                f.Model.NewOrderCommand.Execute(null);
                f.Model.NewManager = "Иванов"; f.Model.NewCustomer = "Вторая заявка";
                await f.Model.CreateOrderCommand.ExecuteAsync(null);
                Assert.NotEqual(initialOrder, f.Model.SelectedOrder!.Order.Id);
                Assert.Empty(f.Model.ManagerMapsNotice);
                resume.SetResult();
                await Until(() => File.Exists(output));
                Assert.Empty(f.Model.ManagerMapsNotice);
                f.Model.SelectedOrder = f.Model.Orders.Single(row => row.Order.Id == initialOrder);
                await Until(() => !f.Model.IsPreparingManagerMaps);
                Assert.Contains(Path.GetFileName(output), f.Model.ManagerMapsNotice);
                Assert.False(cancelled.Task.IsCompleted);
            }
            else if (outcome == "exclude")
            {
                await f.Model.SetProjectIncludedAsync(hans.Id, false);
                Assert.False(f.Model.Projects.Single().IsIncluded);
                resume.SetResult();
                await Until(() => !f.Model.IsPreparingManagerMaps);
                Assert.False(File.Exists(output));
                Assert.Contains("Проект изменился", f.Model.ManagerMapsNotice);
            }
            else
            {
                if (outcome == "close")
                {
                    window.Close();
                    Assert.False(window.IsVisible);
                }
                else cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                await Until(() => cancelled.Task.IsCompleted && !f.Model.IsPreparingManagerMaps);
                Assert.False(File.Exists(output));
                if (outcome == "cancel") Assert.Equal("Сборка карт отменена.", f.Model.ManagerMapsNotice);
            }
            var order = (await f.Store.ReadAsync(Token)).Orders.Single(o => o.Id == initialOrder);
            Assert.Single(order.Specifications);
        }
        finally
        {
            resume.TrySetResult();
            WorkflowTestWindows.Close(window);
        }
    }

    [AvaloniaFact]
    public async Task GnfBundleUsesOnlyPairedSheetsInOrderAndIsCachedSeparately()
    {
        var word = new ShopWordEditor();
        using var fixture = new ProjectFixture(wordMapEditor: word);
        await fixture.InitializeAsync();
        var hans = await fixture.AddAsync("HansLaser");
        var folder = Path.GetDirectoryName(hans.NativePath!)!;
        foreach (var sheet in new[] { 10, 2 })
        {
            await File.WriteAllTextAsync(Path.Combine(folder, $"Лист-{sheet}.DOC"), $"ordinary-{sheet}", Token);
            await File.WriteAllTextAsync(Path.Combine(folder, $"Лист-{sheet}.Gnf.DOC"), $"gnf-{sheet}", Token);
        }
        await fixture.Model.MergeManagerMapsAsync(Assert.Single(fixture.Model.Projects), Token);
        var gnfName = hans.Name + "-Блок-Карта.Gnf.doc";
        var ordinaryName = hans.Name + "-Блок-Карта.doc";
        var gnf = await File.ReadAllTextAsync(Path.Combine(folder, gnfName), Token);
        Assert.Contains("gnf-2\nЛист-10.Gnf.DOC", gnf);
        Assert.DoesNotContain("ordinary-", gnf);
        Assert.DoesNotContain("gnf-", await File.ReadAllTextAsync(Path.Combine(folder, ordinaryName), Token));
        Assert.Equal(2, word.BundleCalls);
        Assert.Equal(["ordinary-2", "ordinary-10"], word.LastPairedSources);
        Assert.Contains("файлов 2", fixture.Model.ManagerMapsNotice);
        await fixture.Model.MergeManagerMapsAsync(Assert.Single(fixture.Model.Projects), Token);
        Assert.Equal(2, word.BundleCalls);
        Assert.DoesNotContain(gnfName, ShopFileSystem.CandidateNames(folder, [".doc"]));

        await File.WriteAllTextAsync(Path.Combine(folder, "Лист-2.Gnf.DOC"), "gnf-new", Token);
        await fixture.Model.MergeManagerMapsAsync(Assert.Single(fixture.Model.Projects), Token);
        Assert.Equal(3, word.BundleCalls);
        Assert.Contains("gnf-new", await File.ReadAllTextAsync(Path.Combine(folder, gnfName), Token));
    }

    [Fact]
    public void GnfSheetMustShareTheProgramOfAnOrdinaryCardFromTheCurrentProject()
    {
        var match = new WordMapMatch(["Проект.dsp"], ["Другой.dsp"]);
        static WordMapCell[] Gnf(string program) => [new(0, 1, "Файл:\a"), new(1, 1, program)];
        static WordMapCell[] Ordinary(string order) =>
            [new(0, 1, "Заказ"), new(1, 1, order), new(2, 2, "Файл УП"), new(3, 2, "Лист-1.gnc"),
             new(4, 3, "Комментарий"), new(5, 3, "")];
        Assert.True(WorkflowWordMapRules.IsGnfPair([Gnf("ЛИСТ-1")], [Ordinary("Проект.DSP")], match));
        Assert.False(WorkflowWordMapRules.IsGnfPair([Gnf("Лист-2")], [Ordinary("Проект.DSP")], match));
        Assert.False(WorkflowWordMapRules.IsGnfPair([Gnf("Лист-1")], [Ordinary("Другой.DSP")], match));
        Assert.False(WorkflowWordMapRules.IsGnfPair([Gnf("Лист-1")], [Ordinary("")], match));
    }

    [Fact]
    public async Task ACardRemovedDuringMergingPreventsPublicationOfAnIncompleteFile()
    {
        var word = new ShopWordEditor();
        using var f = new ProjectFixture(wordMapEditor: word);
        await f.InitializeAsync();
        var hans = await f.AddAsync("HansLaser");
        var folder = Path.GetDirectoryName(hans.NativePath!)!;
        var removed = Path.Combine(folder, "Лист-2.DOC");
        await File.WriteAllTextAsync(Path.Combine(folder, "Лист-1.DOC"), "first", Token);
        await File.WriteAllTextAsync(removed, "second", Token);
        var machine = f.Model.ProjectMachines.Single(m => m.Id == hans.MachineId);
        var error = await Assert.ThrowsAsync<WorkflowValidationException>(() => WorkflowManagerMapBundle.CreateAsync(
            hans, machine, ShopMapBundle.Pdf, word, _ => { File.Delete(removed); return Task.CompletedTask; }, Token));
        Assert.Contains("Состав карт изменился", error.Message);
        Assert.False(File.Exists(Path.Combine(folder, hans.Name + "-Блок-Карта.pdf")));
        Assert.Empty(Directory.GetFiles(Path.Combine(folder, WorkflowManagerMapBundle.MarkerFolder), "*.tmp"));
    }

    [Fact]
    public async Task ANewSpecificationReplacesAnOldBundleAtTheSamePathInTheReportPicker()
    {
        using var fixture = new ProjectFixture();
        await fixture.InitializeAsync();
        var hans = await fixture.AddAsync("HansLaser");
        var folder = Path.GetDirectoryName(hans.NativePath!)!;
        var report = Path.Combine(folder, hans.Name + ".pdf");
        var markers = Directory.CreateDirectory(Path.Combine(folder, WorkflowManagerMapBundle.MarkerFolder));
        await File.WriteAllTextAsync(report, "old generated bundle", Token);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(report, Token)));
        var receipt = System.Text.Json.JsonSerializer.Serialize(new { Current = new { InputsHash = "", OutputHash = hash } });
        await File.WriteAllTextAsync(Path.Combine(markers.FullName, Path.GetFileName(report)), receipt, Token);
        await File.WriteAllTextAsync(Path.Combine(folder, hans.Name + "-Блок-Карта.PDF"), "map without marker", Token);

        var candidate = Assert.Single(WorkflowReportFiles.ListReports(folder, hans.ImportProfile));
        Assert.Equal(report, candidate.Path);
        Assert.Null(await WorkflowReportFiles.ReadAsync(candidate, Token));
        WriteReport(hans, report);
        var review = await fixture.ReviewAsync();
        var ready = Assert.Single(review.Candidates);
        Assert.True(ready.Ready, ready.Status);
    }

    private static void WriteReport(CuttingProject project, string path)
    {
        using var pdf = new PdfDocument();
        using (var graphics = XGraphics.FromPdfPage(pdf.AddPage()))
        {
            var font = WorkflowPdfFont.Create(10);
            void Text(string text, double x, double y) => graphics.DrawString(text, font, XBrushes.Black, x, y);
            Text("Заказ", 30, 60); Text(Path.GetFileName(project.NativePath!), 190, 60);
            Text("Длина реза:(мм)", 30, 85); Text("5056.7", 190, 85);
            Text("Пробивки:", 30, 110); Text("20", 190, 110);
            Text("Листов:", 30, 135); Text("1", 190, 135);
            Text("Вес (кг)", 30, 160); Text("20", 190, 160);
            Text("Общие кол-во деталей", 30, 185); Text("1", 190, 185);
            Text("Листы", 30, 225);
            Text("№", 30, 245); Text("Размер X", 80, 245); Text("Размер Y", 160, 245);
            Text("Материал", 250, 245); Text("Gas", 340, 245); Text("Толщ. (мм)", 400, 245); Text("К-во", 500, 245);
            Text("1", 30, 270); Text("130", 90, 270); Text("1500", 170, 270);
            Text("St-45_0", 260, 270); Text("O2", 340, 270); Text("6", 410, 270); Text("1", 500, 270);
            Text("Детали", 30, 300);
        }
        pdf.Save(path);
    }

    private static async Task Until(Func<bool> condition, Func<string>? detail = null)
    {
        for (var attempt = 0; attempt < 300 && !condition(); attempt++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10, Token);
        }
        Dispatcher.UIThread.RunJobs();
        Assert.True(condition(), condition() ? null : detail?.Invoke());
    }
}

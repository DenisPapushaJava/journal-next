using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Media;
using Jornal.Desktop.ViewModels;
using Jornal.Workflow.Core;
using Jornal.Workflow.Desktop;
using Jornal.Workflow.Infrastructure;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class WorkflowProfileTests
{
    [Fact]
    public async Task FamiliarPresetsAndExactColorKeepSeparateEditorStateAcrossSettingsSections()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var settings = f.Model.CreateGeneralSettings();
        Assert.Equal(new[] { "#80FF80", "#42C1FF", "#FF9DFF", "#FFFF80", "#80FFFF" }, settings.FrequentColors.Select(c => c.Hex));
        Assert.Equal(24, settings.OtherColors.Count);
        Assert.Contains(settings.OtherColors, c => c.Hex == "#C0C0C0"); Assert.Contains(settings.OtherColors, c => c.Hex == "#FF8000");
        settings.SelectProfileColorCommand.Execute(settings.FrequentColors[0]);
        Assert.Equal(0xFF80FF80u, settings.ProfileColorArgb); Assert.True(settings.FrequentColors[0].IsSelected);
        settings.SelectedSectionIndex = 1; settings.SelectedSectionIndex = 2; settings.SelectedSectionIndex = 0;
        Assert.Equal(0xFF80FF80u, settings.ProfileColorArgb);
        var before = await f.Store.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Null(before.Employees.Single(e => e.Id == f.Model.CurrentEmployee!.Id).ColorArgb);
        await settings.SaveCommand.ExecuteAsync(null); Assert.Null(settings.Error);
        var discarded = f.Model.CreateGeneralSettings(); Assert.True(discarded.FrequentColors[0].IsSelected);
        discarded.ProfileSpectrumColor = new HsvColor(1, 117, .37, .73);
        Assert.DoesNotContain(discarded.FrequentColors.Concat(discarded.OtherColors), c => c.IsSelected);
        Assert.Equal(0xFF80FF80u, (await f.Store.ReadAsync(TestContext.Current.CancellationToken)).Employees.Single(e => e.Id == f.Model.CurrentEmployee!.Id).ColorArgb);
    }

    private static WorkflowViewModel Client(ProjectFixture f, string preferences)
    {
        var model = new WorkflowViewModel(f.Store, new AppUpdatesViewModel(new WorkflowAppUpdater()));
        model.UseLocalPreferencesStore(new(preferences)); return model;
    }

    [Fact]
    public async Task SelectedEmployeeIsLocalButColorIsSharedAndSurvivesThemeChangesAndRestart()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var project = await f.AddAsync();
        await ProjectFixture.WriteAsync(project); await f.AcceptAsync();
        var before = await f.Store.ReadAsync(TestContext.Current.CancellationToken);
        var originalAuthor = before.Orders.Single().Specifications.Single().Current.AuthorId;
        var draft = f.Model.SelectedDraft!; draft.Coefficient = "1,3";
        var preferences = new WorkflowPreferencesStore(Path.Combine(f.DirectoryPath, "preferences.json"));
        preferences.Update(p => p with { DarkTheme = true });
        var settings = f.Model.CreateGeneralSettings(); settings.ProfileName = "Кузнецов";
        settings.ProfileSpectrumColor = new HsvColor(1, 150, .6, .8);
        settings.ProjectRoot = Path.Combine(f.DirectoryPath, "not-yet-saved");
        var color = settings.ProfileColorArgb;
        await settings.SaveCommand.ExecuteAsync(null); Assert.Null(settings.Error);
        Assert.True(settings.HasRootChanges); Assert.False(settings.HasProfileChanges);
        var after = await f.Store.ReadAsync(TestContext.Current.CancellationToken);
        var employee = after.Employees.Single(e => e.Name == "Кузнецов"); Assert.Equal(color, employee.ColorArgb);
        Assert.Equal(before.ProjectRoot, after.ProjectRoot); Assert.Equal(before.Orders.Single().Version, after.Orders.Single().Version);
        Assert.Equal(originalAuthor, after.Orders.Single().Specifications.Single().Current.AuthorId);
        Assert.Same(draft, f.Model.SelectedDraft); Assert.Equal("1,3", draft.Coefficient);
        Assert.Equal(employee.Id, preferences.Load().EmployeeId); Assert.True(preferences.Load().DarkTheme);
        var json = await File.ReadAllTextAsync(Path.Combine(f.DirectoryPath, "preferences.json"), TestContext.Current.CancellationToken);
        Assert.DoesNotContain("Кузнецов", json); Assert.DoesNotContain("Color", json);

        var restarted = Client(f, Path.Combine(f.DirectoryPath, "preferences.json"));
        await restarted.ReloadCommand.ExecuteAsync(null); Assert.Equal(employee, restarted.CurrentEmployee);
        var another = Client(f, Path.Combine(f.DirectoryPath, "another-pc.json"));
        await another.ReloadCommand.ExecuteAsync(null); Assert.Null(another.CurrentEmployee);
        another.NewOrderCommand.Execute(null); Assert.Contains("Выберите себя", another.Error);
        var choice = another.CreateGeneralSettings(); choice.ProfileName = employee.Name;
        Assert.Equal(color, choice.ProfileColorArgb); await choice.SaveCommand.ExecuteAsync(null); Assert.Null(choice.Error);
        Assert.Equal(employee.Id, another.CurrentEmployee!.Id);

        var changed = f.Model.CreateGeneralSettings(); changed.ProfileSpectrumColor = new HsvColor(1, 20, .7, .8);
        await changed.SaveCommand.ExecuteAsync(null); Assert.Null(changed.Error);
        var folders = another.CreateGeneralSettings(); folders.SelectedSectionIndex = 1;
        folders.ProjectRoot = Path.Combine(f.DirectoryPath, "changed-root"); await folders.SaveCommand.ExecuteAsync(null);
        Assert.Null(folders.Error);
        Assert.Equal(changed.ProfileColorArgb, another.CreateGeneralSettings().ProfileColorArgb);
        Assert.Equal(changed.ProfileColorArgb, Assert.IsType<SolidColorBrush>(another.ProfileBrush).Color.ToUInt32());
        await another.ReloadCommand.ExecuteAsync(null);
        Assert.Equal(changed.ProfileColorArgb, another.CurrentEmployee!.ColorArgb);
        preferences.Update(p => p with { DarkTheme = false }); Assert.Equal(employee.Id, preferences.Load().EmployeeId);
        restarted.Detach(); another.Detach();
    }

    [Fact]
    public async Task ColorConflictsKeepInputAndSwitchingLocalProfileNeverRewritesHistoricalAuthors()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync();
        var a = f.Model.CreateGeneralSettings(); var b = f.Model.CreateGeneralSettings();
        a.ProfileSpectrumColor = new HsvColor(1, 10, .5, .8); b.ProfileSpectrumColor = new HsvColor(1, 230, .8, .7);
        await a.SaveCommand.ExecuteAsync(null); Assert.Null(a.Error);
        await b.SaveCommand.ExecuteAsync(null); Assert.NotNull(b.Error); Assert.True(b.HasProfileChanges);
        Assert.Equal(new HsvColor(1, 230, .8, .7).ToRgb().ToUInt32(), b.ProfileColorArgb);
        var book = await f.Store.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(a.ProfileColorArgb, book.Employees.Single(e => e.Id == f.Model.CurrentEmployee!.Id).ColorArgb);
        var oldOrder = book.Orders.Single(); var chosen = f.Model.CreateGeneralSettings(); chosen.ProfileName = "Иванов";
        await chosen.SaveCommand.ExecuteAsync(null); Assert.Null(chosen.Error);
        book = await f.Store.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(oldOrder.Events, book.Orders.Single().Events); Assert.Equal(oldOrder.TechnologistId, book.Orders.Single().TechnologistId);
        f.Model.NewOrderCommand.Execute(null); f.Model.NewManager = "Иванов"; f.Model.NewCustomer = "Новая заявка";
        await f.Model.CreateOrderCommand.ExecuteAsync(null); Assert.Null(f.Model.Error);
        var added = (await f.Store.ReadAsync(TestContext.Current.CancellationToken)).Orders.Single(o => o.Id != oldOrder.Id);
        Assert.Equal(f.Model.CurrentEmployee!.Id, added.TechnologistId); Assert.Equal(f.Model.CurrentEmployee.Id, added.Events.Single().ActorId);
    }

    [Fact]
    public async Task SchemaFiveUpgradePreservesNumbersAndDataAndLocalWriteFailureDoesNotHideSharedCommit()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync();
        var book = await f.Store.ReadAsync(TestContext.Current.CancellationToken);
        var old = JsonSerializer.SerializeToNode(book)!.AsObject(); old["SchemaVersion"] = 5;
        foreach (var employee in old["Employees"]!.AsArray()) employee!.AsObject().Remove("ColorArgb");
        var path = Path.Combine(f.DirectoryPath, "schema-five.json"); var original = old.ToJsonString();
        await File.WriteAllTextAsync(path, original, TestContext.Current.CancellationToken);
        var upgraded = await new LocalWorkflowWorkspaceStore(path).ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(7, upgraded.SchemaVersion); Assert.Equal(book.Version, upgraded.Version);
        Assert.Equal(book.Orders.Single().ReferenceNumber, upgraded.Orders.Single().ReferenceNumber);
        Assert.Equal(book.Orders.Single().Events, upgraded.Orders.Single().Events);
        Assert.Equal(original, await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(f.DirectoryPath, "schema-five.json.*.bak")), TestContext.Current.CancellationToken));
        var blocked = Path.Combine(f.DirectoryPath, "blocked");
        var client = Client(f, Path.Combine(blocked, "preferences.json")); await client.ReloadCommand.ExecuteAsync(null);
        var settings = client.CreateGeneralSettings(); settings.ProfileName = "Папуша";
        await File.WriteAllTextAsync(blocked, "Вместо папки", TestContext.Current.CancellationToken);
        await settings.SaveCommand.ExecuteAsync(null); Assert.Contains("цвет сохранены в БД", settings.Error);
        Assert.Null(client.CurrentEmployee); Assert.True(settings.HasProfileChanges);
        var registered = (await f.Store.ReadAsync(TestContext.Current.CancellationToken)).Employees.Single(e => e.Name == "Папуша");
        File.Delete(blocked); await settings.SaveCommand.ExecuteAsync(null); Assert.Null(settings.Error);
        Assert.Equal(registered.Id, client.CurrentEmployee!.Id);
        Assert.Single((await f.Store.ReadAsync(TestContext.Current.CancellationToken)).Employees, e => e.Name == "Папуша");
        client.Detach();
    }
}

using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Jornal.Workflow.Desktop;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class WorkflowFolderWindowTests
{
    [AvaloniaTheory]
    [InlineData(false)] [InlineData(true)]
    public async Task FolderButtonsOpenTheirOwnRowsAndKeepDraftsWhenFolderIsUnavailable(bool dark)
    {
        using var f = new ProjectFixture(); await f.InitializeAsync();
        var a = await f.AddAsync(); var b = await f.AddAsync("Плазма");
        await ProjectFixture.WriteAsync(a); await ProjectFixture.WriteAsync(b); await f.AcceptAsync();
        var pending = await f.AddAsync();
        var preferences = new WorkflowPreferencesStore(Path.Combine(f.DirectoryPath, "preferences.json")); preferences.Save(new(dark) { EmployeeId = Jornal.Workflow.Core.WorkflowWorkspace.Empty.Employees[1].Id });
        var window = new FolderWindow(preferences) { DataContext = f.Model, Width = 1366, Height = 768 };
        try
        {
            window.Show(); while (f.Model.IsBusy) await Task.Delay(10, TestContext.Current.CancellationToken);
            Dispatcher.UIThread.RunJobs();
            var draft = f.Model.Drafts.Single(d => d.Id == a.Id); draft.Coefficient = "1,2";
            f.Model.SelectedProject = f.Model.Projects.Single(r => r.Project.Id == a.Id);
            var projects = window.FindControl<DataGrid>("WorkflowProjectsGrid")!;
            ClickFolder(window, projects, f.Model.Projects.Single(r => r.Project.Id == b.Id));
            Assert.Equal(Path.GetDirectoryName(b.NativePath), window.OpenedPaths[^1]);
            ClickFolder(window, projects, f.Model.Projects.Single(r => r.Project.Id == pending.Id));
            Assert.Equal(Path.GetDirectoryName(pending.NativePath), window.OpenedPaths[^1]);

            f.Model.OrderTabIndex = 1; Dispatcher.UIThread.RunJobs();
            var specifications = window.FindControl<DataGrid>("WorkflowSpecificationsGrid")!;
            var second = f.Model.Drafts.Single(d => d.Id == b.Id);
            f.Model.SelectedDraft = draft;
            window.FolderAvailable = false;
            ClickFolder(window, specifications, second);
            Assert.Equal(Path.GetDirectoryName(b.NativePath), window.OpenedPaths[^1]);
            Assert.Equal("Рабочая папка недоступна.", f.Model.Error);
            Assert.Equal("1,2", draft.Coefficient);
            Assert.True(draft.IsDirty);

            f.Model.AddManualSpecificationCommand.Execute(null); Dispatcher.UIThread.RunJobs();
            var manual = f.Model.SelectedDraft!;
            var button = FolderButton(window, specifications, manual);
            Assert.False(button.IsEnabled); Assert.Null(manual.FolderPath);

            // An imported report outside a prepared project has its own source folder.
            var imported = Path.Combine(f.DirectoryPath, "outside.rcam.html");
            await File.WriteAllTextAsync(imported, ProjectFixture.Html(a with { NativePath = Path.Combine(f.DirectoryPath, "6 мм 2026-10-24-155959--ЛАЗЕР.rcam") }), TestContext.Current.CancellationToken);
            await f.Model.ImportFilesAsync([imported], TestContext.Current.CancellationToken);
            Assert.Null(f.Model.Error);
            var outside = f.Model.Drafts.Single(d => d.SourcePath == imported);
            window.FolderAvailable = true;
            ClickFolder(window, specifications, outside);
            Assert.Equal(f.DirectoryPath, window.OpenedPaths[^1]);
            Assert.Equal("1,2", draft.Coefficient);
            var book = await f.Store.ReadAsync(TestContext.Current.CancellationToken);
            Assert.Equal(2, book.Orders.Single().Specifications.Length);
            Assert.All(book.Orders.Single().Specifications, s => Assert.Single(s.Revisions));
        }
        finally { window.Close(); }
    }

    private static Button FolderButton(Window window, DataGrid grid, object row)
    {
        grid.ScrollIntoView(row, null); Dispatcher.UIThread.RunJobs();
        var button = grid.GetVisualDescendants().OfType<Button>().Single(b => ReferenceEquals(b.DataContext, row) &&
            AutomationProperties.GetName(b)?.StartsWith("Открыть папку", StringComparison.Ordinal) == true);
        var point = button.TranslatePoint(default, window)!.Value;
        Assert.InRange(point.X, 0, window.ClientSize.Width - button.Bounds.Width);
        Assert.InRange(point.Y, 0, window.ClientSize.Height - button.Bounds.Height);
        return button;
    }

    private static void ClickFolder(FolderWindow window, DataGrid grid, object row)
    {
        var button = FolderButton(window, grid, row); Assert.True(button.IsEnabled);
        var point = button.TranslatePoint(default, window)!.Value + new Vector(button.Bounds.Width / 2, button.Bounds.Height / 2);
        var before = window.OpenedPaths.Count;
        window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Dispatcher.UIThread.RunJobs();
        Assert.Equal(before + 1, window.OpenedPaths.Count);
    }

    private sealed class FolderWindow(WorkflowPreferencesStore preferences) : WorkflowWindow(preferences)
    {
        public List<string> OpenedPaths { get; } = [];
        public bool FolderAvailable { get; set; } = true;
        protected override Task<bool> OpenFolderAsync(string path)
        { OpenedPaths.Add(path); return Task.FromResult(FolderAvailable); }
    }
}

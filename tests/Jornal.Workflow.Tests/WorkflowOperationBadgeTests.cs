using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Jornal.Workflow.Core;
using Jornal.Workflow.Desktop;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class WorkflowOperationBadgeTests
{
    [AvaloniaFact]
    public async Task SavedOperationsFollowInclusionAndUnsavedEditsDoNotChangeTheSummary()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync();
        var project = await f.AddAsync(); await ProjectFixture.WriteAsync(project);
        var review = await f.ReviewAsync();
        review.Selected!.Draft!.HasBending = true; review.Selected.Draft.HasChamfer = true;
        await review.SaveCommand.ExecuteAsync(null); Assert.Null(review.Error);
        Assert.Equal(2, f.Model.Orders.Single().OperationBadges.Count);
        f.Model.SelectedDraft!.HasBending = false;
        Assert.Equal(2, f.Model.Orders.Single().OperationBadges.Count);
        await f.Model.SaveSpecificationCommand.ExecuteAsync(null); Assert.Null(f.Model.Error);
        Assert.StartsWith("Фаска", Assert.Single(f.Model.Orders.Single().OperationBadges).Hint);
        var row = f.Model.Orders.Single();
        await f.Model.SetProjectIncludedAsync(project.Id, false); Assert.Null(f.Model.Error);
        Assert.Same(row, f.Model.Orders.Single()); Assert.False(row.HasOperationBadges);
        await f.Model.SetProjectIncludedAsync(project.Id, true); Assert.Null(f.Model.Error);
        Assert.Same(row, f.Model.Orders.Single()); Assert.True(row.HasOperationBadges);
    }

    [Fact]
    public void SummaryUsesOnlySavedIncludedCurrentVariantsAndUpdatesInPlace()
    {
        var order = Example(); var row = Row(order);
        Assert.Equal(new[] { "2,5 мм", "6 мм", "2,5 мм", "6 мм", "10 мм" }, row.OperationBadges.Select(b => b.Label));
        Assert.StartsWith("Фаска", row.OperationBadges[0].Hint);
        Assert.StartsWith("Гибка", row.OperationBadges[2].Hint);
        Assert.Contains("Проектов: 2", row.OperationBadges[2].Hint);
        Assert.Contains("09Г2С, Ст3", row.OperationBadges[2].Hint);
        Assert.Contains("без спецификации: 1", row.OperationBadges[2].Hint);
        Assert.DoesNotContain(row.OperationBadges, b => b.Label == "99 мм");
        Assert.NotEqual(row.OperationBadges[0].IconPath, row.OperationBadges[2].IconPath);
        var notifications = new List<string?>(); row.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
        row.Apply(Row(order with { Version = order.Version + 1, Items = order.Items.Select(i => i with { IsIncluded = false }).ToArray() }));
        Assert.False(row.HasOperationBadges); Assert.Empty(row.OperationBadges);
        Assert.Contains(nameof(row.OperationBadges), notifications); Assert.Contains(nameof(row.HasOperationBadges), notifications);
        row.Apply(Row(order with { Version = order.Version + 2 })); Assert.True(row.HasOperationBadges);
        var plain = Row(order with { Specifications = order.Specifications.Select(s => s with
            { Current = s.Current with { Parameters = s.Current.Parameters with { HasBending = false, HasChamfer = false } } }).ToArray() });
        Assert.False(plain.HasOperationBadges); // Missing specifications never manufacture a negative/positive badge.
    }

    [AvaloniaTheory]
    [InlineData(false, 1180)] [InlineData(true, 1180)] [InlineData(false, 1480)] [InlineData(true, 1480)]
    public async Task BadgesStayOnOneLineInTheOrderCellAndUseBothThemes(bool dark, int width)
    {
        using var log = new WorkflowWindowTests.BindingLog();
        using var f = new ProjectFixture(); await f.InitializeAsync();
        var prefs = new WorkflowPreferencesStore(Path.Combine(f.DirectoryPath, "preferences.json"));
        prefs.Update(p => p with { DarkTheme = dark });
        var window = new WorkflowWindow(prefs) { DataContext = f.Model, Width = width, Height = 768 };
        try
        {
            window.Show(); while (f.Model.IsBusy) await Task.Delay(10, TestContext.Current.CancellationToken);
            var workspace = window.FindControl<Grid>("JournalWorkspace")!;
            workspace.ColumnDefinitions[0].Width = new GridLength(width == 1180 ? 368 : 660);
            var grid = window.FindControl<DataGrid>("WorkflowOrdersGrid")!;
            var row = Row(Example());
            var simple = new WorkflowOrderRow(row.Order with { Id = Guid.NewGuid(), ReferenceNumber = "2026-10-24-191914",
                Specifications = row.Order.Specifications.Where(s => s.Current.Parameters.ThicknessMm == 6)
                    .Select(s => s with { Current = s.Current with { Parameters = s.Current.Parameters with { HasChamfer = false } } }).ToArray() }, "Промис", "Вахреева", "Тест");
            var plain = new WorkflowOrderRow(row.Order with { Id = Guid.NewGuid(), ReferenceNumber = "2026-10-25-135001", Specifications = [] }, "АНТК", "Вахреева", "Тест");
            grid.ItemsSource = new[] { row, simple, plain }; Pump();
            var strip = Assert.Single(grid.GetVisualDescendants().OfType<WorkflowBadgeStrip>(), s => s.DataContext == row);
            var singleStrip = Assert.Single(grid.GetVisualDescendants().OfType<WorkflowBadgeStrip>(), s => s.DataContext == simple);
            Assert.Equal(strip.TranslatePoint(default, grid)!.Value.X, singleStrip.TranslatePoint(default, grid)!.Value.X, 1);
            Assert.Equal(18, strip.Bounds.Height);
            var title = Assert.Single(grid.GetVisualDescendants().OfType<TextBlock>(), t => t.DataContext == row && t.Text == row.Customer);
            Assert.Equal(1, Grid.GetColumnSpan(title));
            Assert.True(title.TranslatePoint(default, grid)!.Value.X + title.Bounds.Width <= strip.TranslatePoint(default, grid)!.Value.X);
            var badges = strip.GetVisualDescendants().OfType<WorkflowBadge>().Where(b => b.IsEffectivelyVisible).ToArray();
            Assert.NotEmpty(badges);
            foreach (var badge in badges)
            {
                var point = badge.TranslatePoint(default, strip)!.Value;
                Assert.InRange(point.X, 0, strip.Bounds.Width);
                Assert.True(point.X + badge.Bounds.Width <= strip.Bounds.Width + 0.1);
                Assert.Equal(0, point.Y); Assert.Equal(18, badge.Bounds.Height);
            }
            {
                var more = Assert.Single(badges, b => b.Label.StartsWith('+'));
                Assert.Equal(row.OperationBadges.Count - (badges.Length - 1), int.Parse(more.Label[1..]));
                Assert.Contains("Гибка", Assert.IsType<TextBlock>(ToolTip.GetTip(more)).Text);
            }
            var iconBadge = Assert.Single(strip.GetVisualDescendants().OfType<WorkflowBadge>().Take(1));
            Assert.True(iconBadge.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().Single().IsVisible);
            Assert.True(iconBadge.TryFindResource("JournalText", window.ActualThemeVariant, out var resource));
            var expected = Assert.IsAssignableFrom<IBrush>(resource);
            Assert.Equal(expected.ToString(), iconBadge.Foreground!.ToString());
            SaveFrame(window, $"operations-{width}-{(dark ? "dark" : "light")}");
            var heights = grid.GetVisualDescendants().OfType<DataGridRow>().Select(r => r.Bounds.Height).ToArray();
            Assert.Equal(3, heights.Length); Assert.All(heights, h => Assert.Equal(heights[0], h));
            var height = heights[0];
            grid.ItemsSource = new[] { Row(row.Order with { Specifications = [] }) }; Pump();
            Assert.Equal(height, Assert.Single(grid.GetVisualDescendants().OfType<DataGridRow>()).Bounds.Height);
            grid.ItemsSource = new[] { row }; Pump();
            strip = Assert.Single(grid.GetVisualDescendants().OfType<WorkflowBadgeStrip>());
            // Resizing must reveal the same items again, without changing their order or data.
            window.Width = 2100; workspace.ColumnDefinitions[0].Width = new GridLength(1280); Pump();
            Assert.Equal(row.OperationBadges.Count, strip.GetVisualDescendants().OfType<WorkflowBadge>().Count(b => b.IsEffectivelyVisible));
            workspace.ColumnDefinitions[0].Width = new GridLength(368); Pump();
            Assert.Contains(strip.GetVisualDescendants().OfType<WorkflowBadge>(), b => b.IsEffectivelyVisible && b.Label.StartsWith('+'));
            Assert.Empty(log.Errors);
        }
        finally { window.Close(); }
    }

    private static WorkflowOrderRow Row(WorkflowOrder order) => new(order, "Иванов И. М.", "Чупров", "Тест");
    private static WorkflowOrder Example()
    {
        var now = DateTimeOffset.UtcNow;
        var items = new List<WorkflowOrderItem>(); var projects = new List<CuttingProject>(); var specs = new List<WorkflowSpecification>();
        Add(2.5m, "Ст3", true, true); Add(2.5m, "09Г2С", true, false);
        Add(6, "Ст3", true, true); Add(10, "Ст3", true, false);
        Add(99, "Не включён", true, true, included: false);
        Add(99, "Удалён", true, true, deleted: true);
        Add(99, "Прежний вариант", true, true, current: false);
        Add(20, "Ожидается", false, false, saved: false);
        return new(Guid.NewGuid(), 1, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "000837", 1, now, now, specs.ToArray())
        { ReferenceNumber = "2026-10-31-110032", Items = items.ToArray(), Projects = projects.ToArray() };

        void Add(decimal thickness, string material, bool bending, bool chamfer, bool included = true, bool deleted = false, bool current = true, bool saved = true)
        {
            var id = Guid.NewGuid(); var item = Guid.NewGuid();
            var project = new CuttingProject(id, item, 1, Guid.NewGuid(), thickness, "Тест", null, null, now)
                { DeletedAt = deleted ? now : null };
            projects.Add(project);
            var currentId = id;
            if (!current) { currentId = Guid.NewGuid(); projects.Add(project with { Id = currentId, Variant = 2 }); }
            items.Add(new(item, items.Count + 1, currentId) { IsIncluded = included && !deleted });
            if (!saved) return;
            var state = new SpecificationState("001", DateOnly.FromDateTime(now.DateTime), now, Guid.NewGuid(),
                new("Лазер", material, "Базовая", thickness, "1500x3000", 1, 10, 5, null, null, 1, bending, null) { HasChamfer = chamfer },
                new(Guid.NewGuid(), 100, 1, 1000, 5, 1005), null, null);
            specs.Add(new(id, state));
            if (!current) specs.Add(new(currentId, state with { Parameters = state.Parameters with { HasBending = false, HasChamfer = false } }));
        }
    }
    private static void Pump() { Dispatcher.UIThread.RunJobs(); }
    private static void SaveFrame(Window window, string name)
    {
        var directory = Environment.GetEnvironmentVariable("JOURNAL_WORKFLOW_SCREENSHOTS");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory); Pump();
        using var frame = window.CaptureRenderedFrame();
        using var file = File.Create(Path.Combine(directory, name + ".png")); frame!.Save(file, new PngBitmapEncoderOptions());
    }
}

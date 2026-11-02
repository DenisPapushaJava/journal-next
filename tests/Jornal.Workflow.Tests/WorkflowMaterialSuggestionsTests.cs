using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Jornal.Desktop.Controls;
using Jornal.Workflow.Desktop;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class WorkflowMaterialSuggestionsTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    [Fact]
    public async Task OlderPreferencesWithoutMaterialsRemainReadableAndSavingOnlyMaterialsPreservesOtherSettingsAndDatabase()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync();
        var path = Path.Combine(f.DirectoryPath, "preferences.json");
        await File.WriteAllTextAsync(path, "{\"DarkTheme\":true,\"EntrySuggestions\":{\"Managers\":[\"Личный менеджер\"],\"Customers\":[\"Личный заказчик\"]}}", Token);
        var prefs = new WorkflowPreferencesStore(path); Assert.Empty(prefs.Load().EntrySuggestions!.Materials);
        await f.Model.ReloadCommand.ExecuteAsync(null);
        var original = await File.ReadAllBytesAsync(f.Store.FilePath, Token);
        var editor = f.Model.CreateGeneralSettings().CreateEntrySuggestionsEditor();
        Assert.Empty(editor.MaterialsText); editor.MaterialsText = "Ст3\n09Г2С";
        await editor.SaveCommand.ExecuteAsync(null); Assert.Null(editor.Error);
        var saved = prefs.Load(); Assert.True(saved.DarkTheme); Assert.Equal(new[] { "Личный менеджер" }, saved.EntrySuggestions!.Managers);
        Assert.Equal(new[] { "Личный заказчик" }, saved.EntrySuggestions.Customers); Assert.Equal(new[] { "Ст3", "09Г2С" }, saved.EntrySuggestions.Materials);
        Assert.Equal(original, await File.ReadAllBytesAsync(f.Store.FilePath, Token));
    }

    [AvaloniaTheory]
    [InlineData(false)] [InlineData(true)]
    public async Task MaterialListsWorkInProjectAndBothReviewInputsAndDoNotConfirmAwjcThroughFocusOrSelectingTheSameValue(bool dark)
    {
        using var log = new WorkflowWindowTests.BindingLog(); using var f = new ProjectFixture(); await f.InitializeAsync();
        var editor = f.Model.CreateGeneralSettings().CreateEntrySuggestionsEditor(); editor.MaterialsText = "09Г2С\nСт3\nAISI304";
        await editor.SaveCommand.ExecuteAsync(null); Assert.Null(editor.Error);
        var project = await f.AddAsync("ГАР"); WorkflowAutomaticCalculationTests.WriteAwjc(project);
        var window = new WorkflowWindow(new(Path.Combine(f.DirectoryPath, "preferences.json")))
        { DataContext = f.Model, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light, Width = 1180, Height = 768 };
        WorkflowReportReviewWindow? dialog = null;
        try
        {
            window.Show(); await Until(() => !f.Model.IsBusy);
            var review = Assert.IsType<WorkflowReportReviewViewModel>(await f.Model.CalculateOrderAsync(Token));
            var candidate = Assert.Single(review.VisibleCandidates); Assert.True(candidate.Draft!.MaterialNeedsReview); Assert.False(candidate.Draft.MaterialConfirmed);
            dialog = new() { DataContext = review, RequestedThemeVariant = window.ActualThemeVariant }; dialog.Show(window); Pump();
            var material = dialog.FindControl<EntrySuggestionComboBox>("ReviewMaterialInput")!;
            Assert.Equal(f.Model.MaterialSuggestions, material.Suggestions); Assert.Equal("09Г2С", material.Value);
            candidate.Draft.Pierces = "15"; Assert.True(review.SaveCommand.CanExecute(null));
            material.GetVisualDescendants().OfType<TextBox>().Single().Focus(); Press(dialog, Key.F4); Assert.True(material.IsDropDownOpen); Press(dialog, Key.Enter);
            Assert.False(material.IsDropDownOpen); Assert.Equal("09Г2С", candidate.Draft.Material);
            Assert.True(candidate.Draft.MaterialNeedsReview); Assert.False(candidate.Draft.MaterialConfirmed); Assert.False(review.WasSaved);
            var lists = f.Model.CreateGeneralSettings().CreateEntrySuggestionsEditor(); lists.MaterialsText = "09Г2С\nАлюминий";
            await lists.SaveCommand.ExecuteAsync(null); Assert.Null(lists.Error); Pump();
            Assert.Equal(new[] { "09Г2С", "Алюминий" }, material.Suggestions);
            Assert.Equal("09Г2С", material.Value); Assert.True(candidate.Draft.MaterialNeedsReview); Assert.False(candidate.Draft.MaterialConfirmed);
            candidate.ShowReportFields = true; Pump();
            var expanded = dialog.FindControl<EntrySuggestionComboBox>("ReviewReportMaterialInput")!;
            Assert.Equal(material.Suggestions, expanded.Suggestions); Assert.Equal(material.Value, expanded.Value);
            Assert.True(log.Errors.Count == 0, string.Join("\n", log.Errors)); log.Errors.Clear();
            Frame(dialog, $"material-review-{dark}");
            candidate.Draft.Pierces = "15"; candidate.Draft.MaterialConfirmed = true;
            await review.SaveCommand.ExecuteAsync(null); Assert.Null(review.Error); Assert.True(review.WasSaved); Pump();
            var main = window.FindControl<EntrySuggestionComboBox>("WorkflowCalculationMaterial")!;
            Assert.Equal(f.Model.MaterialSuggestions, main.Suggestions); Assert.Equal("09Г2С", main.Value);
            var beforeInput = await File.ReadAllBytesAsync(f.Store.FilePath, Token);
            var text = main.GetVisualDescendants().OfType<TextBox>().Single(); text.Focus(); text.SelectAll(); window.KeyTextInput("Титан"); Pump();
            Assert.Equal("Титан", f.Model.SelectedDraft!.Material); Assert.DoesNotContain("Титан", f.Model.MaterialSuggestions);
            Assert.Equal(beforeInput, await File.ReadAllBytesAsync(f.Store.FilePath, Token));
            Press(window, Key.F4); Assert.True(main.IsDropDownOpen); Assert.Equal(new[] { "09Г2С", "Алюминий" }, main.Items.Cast<string>());
            await Task.Delay(250, Token); Pump(); Frame(window, $"material-project-{dark}"); Press(window, Key.Escape);
            Assert.DoesNotContain(log.Errors, e => e.Contains("Suggestions;", StringComparison.Ordinal));
        }
        finally { if (dialog?.IsVisible == true) dialog.Close(); WorkflowTestWindows.Close(window); }
    }
    private static void Pump() => Dispatcher.UIThread.RunJobs();
    private static void Press(TopLevel root, Key key) { root.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, null); Pump(); }
    private static async Task Until(Func<bool> ready)
    { for (var i = 0; i < 500 && !ready(); i++) { await Task.Delay(10, Token); Pump(); } Pump(); Assert.True(ready()); }
    private static void Frame(TopLevel root, string name)
    {
        if (Environment.GetEnvironmentVariable("JOURNAL_WORKFLOW_SCREENSHOTS") is not { } output) return;
        Directory.CreateDirectory(output); using var frame = root.CaptureRenderedFrame(); frame!.Save(Path.Combine(output, name + ".png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.Logging;
using Jornal.Desktop.ViewModels;
using Jornal.Workflow.Desktop;
using Jornal.Workflow.Infrastructure;
using Jornal.Workflow.Core;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(Jornal.Workflow.Tests.WorkflowTestApp))]
namespace Jornal.Workflow.Tests;
public static class WorkflowTestApp
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UseSkia().WithInterFont()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
public sealed class WorkflowWindowTests
{
    [AvaloniaTheory]
    [InlineData(false)] [InlineData(true)]
    public async Task OrderCalculationHistoryAndThemeRemainInsideTheNewProduct(bool dark)
    {
        using var bindingLog = new BindingLog();
        var dir = Path.Combine(Path.GetTempPath(), "workflow-ui-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        var preferences = new WorkflowPreferencesStore(Path.Combine(dir, "preferences.json")); preferences.Save(new(dark) { EmployeeId = Jornal.Workflow.Core.WorkflowWorkspace.Empty.Employees[1].Id });
        new WorkflowPreferencesStore(Path.Combine(dir, "preferences.json")).Update(p => p with { EmployeeId = Jornal.Workflow.Core.WorkflowWorkspace.Empty.Employees[1].Id });
        var model = new WorkflowViewModel(new LocalWorkflowWorkspaceStore(Path.Combine(dir, "workspace.json")), new AppUpdatesViewModel(new WorkflowAppUpdater()));
        var window = new WorkflowWindow(preferences) { DataContext = model, Width = 1520, Height = 960 };
        try
        {
            await model.ReloadCommand.ExecuteAsync(null); model.NewManager = "Иванов"; model.NewCustomer = "ООО «Пример»"; await model.CreateOrderCommand.ExecuteAsync(null);
            model.AddManualSpecificationCommand.Execute(null); var draft = Assert.IsType<SpecificationDraft>(model.SelectedDraft);
            draft.Number = "1023-0920"; draft.Date = "23.10.2026"; draft.Machine = "ГАР"; draft.Material = "09Г2С";
            draft.Thickness = "20"; draft.BlankSize = "Лист 20×1500×6000"; draft.Quantity = "1"; draft.Length = "80";
            draft.Pierces = "100"; draft.Coefficient = "1,10"; draft.Weight = "1413"; draft.Duration = "01:12";
            draft.PricePerMeter = "230"; draft.PricePerPierce = "5";
            await model.SaveRateCommand.ExecuteAsync(null); Assert.True(draft.CanSave);
            await model.SaveSpecificationCommand.ExecuteAsync(null); Assert.Null(model.Error); Assert.Single(model.Revisions);
            model.SelectedDraft!.HasBending = true; await model.SaveSpecificationCommand.ExecuteAsync(null);
            Assert.Equal(2, model.Revisions.Count); Assert.False(model.HasPendingEdits);
            model.GenerateResultCommand.Execute(null); Assert.EndsWith("\n", model.ResultText); Assert.Contains("20", model.ResultText);
            window.Show(); window.FindControl<TabControl>("WorkflowOrderTabs")!.SelectedIndex = 1; Dispatcher.UIThread.RunJobs();
            Assert.Equal(dark ? ThemeVariant.Dark : ThemeVariant.Light, window.ActualThemeVariant);
            Assert.NotNull(window.FindControl<Button>("UpdatesBell")); Assert.NotNull(window.FindControl<DataGrid>("WorkflowSpecificationsGrid"));
            var save = window.FindControl<Button>("SaveWorkflowSpecification")!;
            var point = save.TranslatePoint(default, window)!.Value;
            Assert.InRange(point.Y + save.Bounds.Height, 0, window.ClientSize.Height - 20);
            Assert.True(bindingLog.Errors.Count == 0, string.Join("\n", bindingLog.Errors.Distinct().Take(12)));
            var image = window.CaptureRenderedFrame(); Assert.NotNull(image);
            var output = Environment.GetEnvironmentVariable("JOURNAL_WORKFLOW_SCREENSHOTS");
            if (output is not null) { Directory.CreateDirectory(output); image!.Save(Path.Combine(output, dark ? "workflow-dark.png" : "workflow-light.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions()); }
        }
        finally { window.Close(); Directory.Delete(dir, true); }
    }

    [AvaloniaTheory]
    [InlineData(false)] [InlineData(true)]
    public async Task DatabasePreviewImportsPricesAndExistingCalculationKeepsHistoricalTariff(bool dark)
    {
        using var bindingLog = new BindingLog();
        var dir = Path.Combine(Path.GetTempPath(), "workflow-prices-ui-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        new WorkflowPreferencesStore(Path.Combine(dir, "preferences.json")).Update(p => p with { EmployeeId = Jornal.Workflow.Core.WorkflowWorkspace.Empty.Employees[1].Id });
        var model = new WorkflowViewModel(new LocalWorkflowWorkspaceStore(Path.Combine(dir, "workspace.json")), new AppUpdatesViewModel(new WorkflowAppUpdater()));
        var source = new CatalogSource();
        await model.ReloadCommand.ExecuteAsync(null);
        var settings = model.CreateDatabaseSettings(source); settings.DatabasePath = Path.Combine(dir, "source.mdb");
        var window = new WorkflowDatabaseWindow { DataContext = settings, Width = 840, Height = 700,
            RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
        try
        {
            Assert.False(settings.ImportCommand.CanExecute(null));
            await settings.ReadCommand.ExecuteAsync(null); Assert.Null(settings.Error); Assert.True(settings.ImportCommand.CanExecute(null));
            window.Show(); Dispatcher.UIThread.RunJobs();
            Assert.Equal(dark ? ThemeVariant.Dark : ThemeVariant.Light, window.ActualThemeVariant);
            Assert.NotNull(window.FindControl<DataGrid>("WorkflowLegacyPricesGrid"));
            var output = Environment.GetEnvironmentVariable("JOURNAL_WORKFLOW_SCREENSHOTS");
            if (output is not null)
            {
                Directory.CreateDirectory(output);
                window.CaptureRenderedFrame()!.Save(Path.Combine(output, dark ? "workflow-database-dark.png" : "workflow-database-light.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            }
            await settings.ImportCommand.ExecuteAsync(null); Assert.Null(settings.Error); Assert.False(settings.ImportCommand.CanExecute(null));
            model.NewManager = "Иванов"; model.NewCustomer = "ООО «Пример»"; await model.CreateOrderCommand.ExecuteAsync(null);
            model.AddManualSpecificationCommand.Execute(null);
            var draft = Assert.IsType<SpecificationDraft>(model.SelectedDraft);
            draft.Number = "1023-1000"; draft.Date = "23.10.2026"; draft.Material = "Ст3"; draft.Thickness = "2,5";
            draft.Length = "10"; draft.Pierces = "5";
            Assert.Equal("200", draft.PricePerMeter); Assert.Equal("1", draft.PricePerPierce); Assert.True(draft.CanSave);
            Assert.Contains("2,5", draft.ThicknessSuggestions);
            draft.PricePerPierce = "10";
            await model.SaveRateCommand.ExecuteAsync(null); await model.SaveSpecificationCommand.ExecuteAsync(null); Assert.Null(model.Error);
            Assert.Equal(2050, model.SelectedDraft!.Quote!.TotalCost);
            var editedDraft = model.SelectedDraft;
            editedDraft.Comment = "Несохранённая правка";
            source.Price = 700; await settings.ReadCommand.ExecuteAsync(null); await settings.ImportCommand.ExecuteAsync(null);
            Assert.Null(settings.Error); Assert.Same(editedDraft, model.SelectedDraft);
            Assert.Equal("Несохранённая правка", model.SelectedDraft.Comment); Assert.Equal("200", model.SelectedDraft.PricePerMeter);
            model.CancelDraftCommand.Execute(null); Assert.False(model.HasPendingEdits);
            model.RefreshRateCommand.Execute(null);
            Assert.Equal("700", model.SelectedDraft.PricePerMeter); Assert.Equal("10", model.SelectedDraft.PricePerPierce);
            Assert.True(model.HasPendingEdits);
            model.SelectedDraft.Thickness = "99";
            Assert.Null(model.SelectedDraft.Quote); Assert.False(model.SelectedDraft.CanSave);
            model.RefreshRateCommand.Execute(null);
            Assert.Empty(model.SelectedDraft.PricePerMeter); Assert.Contains("нет тарифа", model.SelectedDraft.CalculationError);
            Dispatcher.UIThread.RunJobs();
            Assert.True(bindingLog.Errors.Count == 0, string.Join("\n", bindingLog.Errors.Distinct().Take(12)));
        }
        finally { window.Close(); model.Detach(); Directory.Delete(dir, true); }
    }

    private sealed class CatalogSource : ILegacyPriceCatalogReader
    {
        public decimal Price { get; set; } = 200;
        public Task<LegacyPriceCatalog> ReadAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(new LegacyPriceCatalog([new("Лазер", "block-html/v1")], [new(new("Лазер", "Базовая", 2.5m), Price)], []));
    }

    [AvaloniaTheory]
    [InlineData(false)] [InlineData(true)]
    public async Task MachineEditorRenamesByIdPreservesOpenCalculationAndAddsTariffs(bool dark)
    {
        using var bindingLog = new BindingLog();
        var dir = Path.Combine(Path.GetTempPath(), "workflow-catalog-ui-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        new WorkflowPreferencesStore(Path.Combine(dir, "preferences.json")).Update(p => p with { EmployeeId = Jornal.Workflow.Core.WorkflowWorkspace.Empty.Employees[1].Id });
        var model = new WorkflowViewModel(new LocalWorkflowWorkspaceStore(Path.Combine(dir, "workspace.json")), new AppUpdatesViewModel(new WorkflowAppUpdater()));
        await model.ReloadCommand.ExecuteAsync(null); model.NewManager = "Иванов"; model.NewCustomer = "ООО «Пример»"; await model.CreateOrderCommand.ExecuteAsync(null);
        model.AddManualSpecificationCommand.Execute(null);
        var draft = model.SelectedDraft!; draft.Number = "1023-1130"; draft.Date = "23.10.2026"; draft.Machine = "ГАР";
        draft.Material = "Ст3"; draft.Thickness = "10"; draft.Length = "20"; draft.Pierces = "50";
        draft.PricePerMeter = "300"; draft.PricePerPierce = "1";
        await model.SaveRateCommand.ExecuteAsync(null); await model.SaveSpecificationCommand.ExecuteAsync(null); Assert.Null(model.Error);
        var before = model.SelectedDraft!; var id = before.MachineId; var saved = before.Saved!;
        var editor = model.CreateCatalogSettings();
        var window = new WorkflowCatalogWindow { DataContext = editor, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
        try
        {
            Assert.False(editor.SaveCommand.CanExecute(null));
            editor.SelectedMachine = editor.Machines.Single(m => m.Id == id);
            window.Show(); Dispatcher.UIThread.RunJobs();
            editor.SelectedMachine.Name = "Гидроабразив № 1";
            await editor.SaveCommand.ExecuteAsync(null); Assert.Null(editor.Error); Assert.False(editor.HasChanges);
            Assert.Same(before, model.SelectedDraft); Assert.Equal("Гидроабразив № 1", before.Machine); Assert.Equal(id, before.MachineId);
            Assert.False(model.HasPendingEdits); Assert.Equal("ГАР", saved.Parameters.Machine); Assert.Equal(6050, before.Quote!.TotalCost);
            Assert.Single(model.Revisions);
            model.GenerateResultCommand.Execute(null); Assert.Contains("резка Гидроабразив № 1", model.ResultText);
            editor.AddRateCommand.Execute(null); var added = editor.SelectedRate!;
            Assert.Equal("1", added.PricePerPierce); added.Thickness = "12"; added.PricePerMeter = "400";
            await editor.SaveCommand.ExecuteAsync(null); Assert.Null(editor.Error); Assert.Equal(2, editor.Rates!.Count);
            editor.AddRateCommand.Execute(null); editor.SelectedRate!.Thickness = "12"; editor.SelectedRate.PricePerMeter = "500";
            await editor.SaveCommand.ExecuteAsync(null); Assert.Contains("не должна повторяться", editor.Error); Assert.True(editor.HasChanges);
            editor.Reload(); Assert.Equal(2, editor.Rates!.Count); editor.Error = null;
            editor.SelectedMachine!.Name = ""; await editor.SaveCommand.ExecuteAsync(null);
            Assert.NotNull(editor.Error); Assert.True(editor.HasChanges); editor.Reload(); editor.Error = null;
            editor.AddMachineCommand.Execute(null); editor.SelectedMachine!.Name = "Труборез № 2"; var newId = editor.SelectedMachine.Id;
            editor.AddRateCommand.Execute(null); editor.SelectedRate!.Thickness = "2"; editor.SelectedRate.PricePerMeter = "150";
            await editor.SaveCommand.ExecuteAsync(null); Assert.Null(editor.Error); Assert.Equal(newId, editor.SelectedMachine!.Id);
            Assert.Equal("1", editor.Rates![0].PricePerPierce);
            editor.SelectedMachine = editor.Machines.Single(m => m.Id == id); Dispatcher.UIThread.RunJobs();
            window.FindControl<DataGrid>("WorkflowMachineRates")!.SelectedIndex = 0; Dispatcher.UIThread.RunJobs();
            Assert.NotNull(editor.SelectedRate);
            Assert.True(bindingLog.Errors.Count == 0, string.Join("\n", bindingLog.Errors.Distinct().Take(12)));
            var output = Environment.GetEnvironmentVariable("JOURNAL_WORKFLOW_SCREENSHOTS");
            if (output is not null)
            { Directory.CreateDirectory(output); window.CaptureRenderedFrame()!.Save(Path.Combine(output, dark ? "workflow-catalog-dark.png" : "workflow-catalog-light.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions()); }
        }
        finally { editor.Reload(); window.Close(); model.Detach(); Directory.Delete(dir, true); }
    }
    private sealed class BindingLog : ILogSink, IDisposable
    {
        private readonly ILogSink? _previous = Logger.Sink;
        public List<string> Errors { get; } = [];
        public BindingLog() => Logger.Sink = this;
        public void Dispose() => Logger.Sink = _previous;
        public bool IsEnabled(LogEventLevel level, string area) => area == LogArea.Binding && level >= LogEventLevel.Warning;
        public void Log(LogEventLevel level, string area, object? source, string messageTemplate) => Log(level, area, source, messageTemplate, []);
        public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues) => Errors.Add(messageTemplate + " " + string.Join("; ", propertyValues));
    }
}

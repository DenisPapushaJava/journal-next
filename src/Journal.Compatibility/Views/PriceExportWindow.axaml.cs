using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Jornal.Desktop.ViewModels;

namespace Jornal.Desktop.Views;

public partial class PriceExportWindow : Window
{
    private bool _closeRequested;
    private PriceExportViewModel? Model => DataContext as PriceExportViewModel;
    public PriceExportWindow() => InitializeComponent();
    protected override void OnOpened(EventArgs e) { base.OnOpened(e); WindowSizing.FitToWorkingArea(this); }
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (Model is { IsExporting: true } model)
        { _closeRequested = true; model.CancelExport(); e.Cancel = true; }
        base.OnClosing(e);
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape) { Close(); e.Handled = true; }
    }
    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();
    private async void OnExportClick(object? sender, RoutedEventArgs e)
    {
        if (Model is not { } model || !model.CanExport) return;
        var saved = await model.ExportAsync(ChoosePathAsync);
        if (saved) Close(true);
        else if (_closeRequested) Close(false);
    }
    private async Task<string?> ChoosePathAsync(string suggestedName)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new()
        {
            Title = "Сохранить прайсы", SuggestedFileName = suggestedName, DefaultExtension = "xlsx", ShowOverwritePrompt = true,
            FileTypeChoices = [new FilePickerFileType("Книга Excel") { Patterns = ["*.xlsx"] }]
        });
        if (file is null) return null;
        return file.TryGetLocalPath() ?? throw new NotSupportedException("Выберите локальную или сетевую папку.");
    }
}

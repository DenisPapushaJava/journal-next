using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace Jornal.Workflow.Desktop;

public partial class WorkflowShopTransferWindow : Window
{
    private readonly bool _refreshOnOpen;
    public WorkflowShopTransferWindow() : this(true) { }
    public WorkflowShopTransferWindow(bool refreshOnOpen) { _refreshOnOpen = refreshOnOpen; InitializeComponent(); }
    protected override async void OnOpened(EventArgs e)
    { base.OnOpened(e); if (_refreshOnOpen && DataContext is WorkflowShopTransferViewModel model) await model.RefreshAsync(); }
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (DataContext is WorkflowShopTransferViewModel { IsBusy: true } model)
        { e.Cancel = true; model.Cancel(); }
        base.OnClosing(e);
    }
    protected override void OnKeyDown(KeyEventArgs e)
    { base.OnKeyDown(e); if (!e.Handled && e.Key == Key.Escape) { Close(); e.Handled = true; } }
    private void OnClose(object? sender, RoutedEventArgs e) => Close();
    private async void OnOpenFile(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not WorkflowShopTransferViewModel { CanEdit: true } model || sender is not Control { DataContext: WorkflowShopFileChoice file }) return;
        try
        {
            var item = await StorageProvider.TryGetFileFromPathAsync(file.File.SourcePath);
            if (item is null || !await Launcher.LaunchFileAsync(item)) model.Error = "Не удалось открыть файл. Проверьте доступ и назначенную программу.";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { model.Error = "Не удалось открыть файл. Проверьте доступ и назначенную программу."; }
    }
    private async void OnOpenDestination(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not WorkflowShopTransferViewModel { CanEdit: true, SelectedProject: { HasDestination: true } project } model) return;
        try
        {
            if (!await Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(project.Destination))) model.Error = "Не удалось открыть папку задания.";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { model.Error = "Не удалось открыть папку задания. Проверьте доступ."; }
    }
}

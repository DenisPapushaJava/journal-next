using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Jornal.Infrastructure.Local;

namespace Jornal.Workflow.Desktop;

public partial class WorkflowDatabaseWindow : Window
{
    public WorkflowDatabaseWindow() => InitializeComponent();
    private WorkflowDatabaseViewModel? Model => DataContext as WorkflowDatabaseViewModel;
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (Model is { IsApplying: true }) e.Cancel = true;
        else Model?.ReadCommand.Cancel();
        base.OnClosing(e);
    }
    private void OnClose(object? sender, RoutedEventArgs e) => Close();
    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        if (Model is not { CanEdit: true } model || !StorageProvider.CanOpen) return;
        try
        {
            var types = new List<FilePickerFileType> { new("База Access") { Patterns = ["*.mdb"] } };
            if (OperatingSystem.IsLinux()) types.Add(new("Локальная копия для разработки") { Patterns = ["*" + LocalJournalStore.Extension] });
            var files = await StorageProvider.OpenFilePickerAsync(new() { Title = "База с ценами прежнего журнала", FileTypeFilter = types, AllowMultiple = false });
            if (IsVisible && files.FirstOrDefault()?.TryGetLocalPath() is { } path) model.DatabasePath = path;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException) { if (IsVisible) model.Error = "Не удалось открыть выбор базы данных."; }
    }
}

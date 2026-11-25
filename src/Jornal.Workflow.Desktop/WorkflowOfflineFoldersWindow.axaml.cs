using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Jornal.Workflow.Desktop;

public partial class WorkflowOfflineFoldersWindow : Window
{
    public WorkflowOfflineFoldersWindow()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnDialogKeyDown, RoutingStrategies.Tunnel);
    }

    internal WorkflowOfflineFoldersWindow(WorkflowOfflineFolders folders) : this()
    {
        OfflineFoldersList.ItemsSource = folders.Items;
        OfflineFoldersList.SelectedIndex = 0;
        OfflineFoldersPartial.IsVisible = folders.Incomplete;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Dispatcher.UIThread.Post(() =>
        {
            if (IsVisible) (OfflineFoldersList.ContainerFromIndex(OfflineFoldersList.SelectedIndex) ?? OfflineFoldersList).Focus();
        }, DispatcherPriority.Loaded);
    }

    private void OnDialogKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; Close(); }
        else if (e.Key == Key.Enter && !CloseOfflineFolders.IsKeyboardFocusWithin)
        { e.Handled = true; OpenSelected(); }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
    private void OnOpen(object? sender, RoutedEventArgs e) => OpenSelected();

    private void OnFolderDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Visual source && source.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault()?.DataContext
            is WorkflowOfflineFolder item)
        {
            e.Handled = true;
            Close(item);
        }
    }

    private void OpenSelected()
    {
        if (OfflineFoldersList.SelectedItem is WorkflowOfflineFolder selected) Close(selected);
    }
}

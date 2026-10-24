using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Jornal.Workflow.Desktop;

public partial class WorkflowReportReviewWindow : Window
{
    public WorkflowReportReviewWindow() => InitializeComponent();
    protected override void OnClosing(WindowClosingEventArgs e)
    { if (DataContext is WorkflowReportReviewViewModel { IsBusy: true }) e.Cancel = true; base.OnClosing(e); }
    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}

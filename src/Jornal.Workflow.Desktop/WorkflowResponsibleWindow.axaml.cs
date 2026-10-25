using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Jornal.Workflow.Core;

namespace Jornal.Workflow.Desktop;

public partial class WorkflowResponsibleWindow : Window
{
    private Guid _initialEmployeeId;
    public WorkflowResponsibleWindow() => InitializeComponent();
    public WorkflowResponsibleWindow(WorkflowResponsibleChange request) : this()
    {
        _initialEmployeeId = request.EmployeeId;
        ResponsibleOrderTitle.Text = request.OrderTitle;
        ResponsibleEmployeeInput.ItemsSource = request.Employees;
        ResponsibleEmployeeInput.SelectedItem = request.Employees.SingleOrDefault(e => e.Id == request.EmployeeId);
    }
    protected override void OnOpened(EventArgs e) { base.OnOpened(e); ResponsibleEmployeeInput.Focus(); }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled && e.Key == Key.Escape) { Close(); e.Handled = true; }
    }
    private void OnEmployeeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (SaveResponsibleChange is { } save)
            save.IsEnabled = ResponsibleEmployeeInput.SelectedItem is Employee selected && selected.Id != _initialEmployeeId;
    }
    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
    private void OnSave(object? sender, RoutedEventArgs e)
    {
        if (ResponsibleEmployeeInput.SelectedItem is Employee selected && selected.Id != _initialEmployeeId) Close((Guid?)selected.Id);
    }
}

namespace Jornal.Workflow.Desktop;

public partial class WorkflowWindow
{
    private void OnOrderPeriodSelected(object? sender, WorkflowPeriodSelectedEventArgs e)
    {
        if (Model is not { } model) return;
        if (e.WholeMonth) model.SelectMonth(e.Date);
        else model.FilterDate = e.Date;
    }
}

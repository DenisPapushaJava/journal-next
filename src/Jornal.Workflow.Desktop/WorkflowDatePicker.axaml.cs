using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Jornal.Workflow.Desktop;

public partial class WorkflowDatePicker : UserControl
{
    public static readonly StyledProperty<DateTime?> SelectedDateProperty =
        AvaloniaProperty.Register<WorkflowDatePicker, DateTime?>(nameof(SelectedDate), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);
    public DateTime? SelectedDate { get => GetValue(SelectedDateProperty); set => SetValue(SelectedDateProperty, value); }
    public WorkflowCalendarViewModel Calendar { get; } = new();
    private Button DateButtonControl => this.FindControl<Button>("DateButton")!;
    private Flyout CalendarFlyout => (Flyout)DateButtonControl.Flyout!;
    public WorkflowDatePicker() => InitializeComponent();
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SelectedDateProperty)
        {
            Calendar.SetSelection(SelectedDate);
            Avalonia.Automation.AutomationProperties.SetName(DateButtonControl,
                SelectedDate is { } date ? "Дата заявок: " + date.ToString("dd.MM.yyyy") + ". Выбрать другую дату" : "Выбрать дату заявок");
        }
    }
    private void OnOpened(object? sender, EventArgs e) { Calendar.Open(); FocusCell(); }
    private void OnClosed(object? sender, EventArgs e) => DateButtonControl.Focus();
    private void OnPrevious(object? sender, RoutedEventArgs e) => Calendar.Move(-1);
    private void OnNext(object? sender, RoutedEventArgs e) => Calendar.Move(1);
    private void OnHeading(object? sender, RoutedEventArgs e) => Calendar.Broaden();
    private void OnDay(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: WorkflowCalendarCell cell } || cell.Caption.Length == 0) return;
        if (Calendar.Choose(cell) is { } date)
        { SetCurrentValue(SelectedDateProperty, date); CalendarFlyout.Hide(); }
        else FocusCell();
    }
    private void FocusCell() => Dispatcher.UIThread.Post(() =>
    {
        if (!CalendarFlyout.IsOpen) return;
        var panel = (Control)CalendarFlyout.Content!;
        var buttons = panel.GetVisualDescendants().OfType<Button>().Where(b => b.DataContext is WorkflowCalendarCell { IsEnabled: true }).ToArray();
        (buttons.FirstOrDefault(b => ((WorkflowCalendarCell)b.DataContext!).Date == Calendar.FocusDate) ?? buttons.FirstOrDefault())?.Focus();
    });
    private void OnCalendarKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { CalendarFlyout.Hide(); e.Handled = true; return; }
        if (!Calendar.IsDays || e.Source is not Control { DataContext: WorkflowCalendarCell cell }) return;
        var offset = e.Key switch { Key.Left => -1, Key.Right => 1, Key.Up => -7, Key.Down => 7,
            Key.Home => -((int)cell.Date.DayOfWeek + 6) % 7, Key.End => 6 - ((int)cell.Date.DayOfWeek + 6) % 7, _ => 0 };
        DateTime target;
        try
        {
            if (e.Key is Key.PageUp or Key.PageDown) target = cell.Date.AddMonths(e.Key == Key.PageUp ? -1 : 1);
            else if (offset != 0) target = cell.Date.AddDays(offset);
            else return;
        }
        catch (ArgumentOutOfRangeException) { e.Handled = true; return; }
        Calendar.FocusDay(target); FocusCell(); e.Handled = true;
    }
}

using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Jornal.Workflow.Desktop;

public sealed record WorkflowCalendarCell(DateTime Date, string Caption, string Description,
    bool IsOutside, bool IsToday, bool IsSelected)
{
    public bool IsEnabled => Caption.Length > 0;
}

// A view-local calendar. Browsing months/years never changes the journal's period.
public sealed class WorkflowCalendarViewModel : ObservableObject
{
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");
    private DateTime? _selectedDate;
    private DateTime _displayDate = DateTime.Today;
    private int _level;
    public ObservableCollection<WorkflowCalendarCell> Cells { get; } = [];
    public string[] Weekdays { get; } = ["Пн", "Вт", "Ср", "Чт", "Пт", "Сб", "Вс"];
    public string Caption => _selectedDate?.ToString("dd.MM.yyyy", Russian) ?? "Выбрать дату";
    public string Title => _level switch
    {
        1 => _displayDate.Year.ToString(Russian),
        2 => $"{YearStart} — {Math.Min(9999, YearStart + 11)}",
        _ => Russian.TextInfo.ToTitleCase(_displayDate.ToString("MMMM yyyy", Russian))
    };
    private int YearStart => (_displayDate.Year - 1) / 12 * 12 + 1;
    public bool IsDays => _level == 0;
    public int Columns => IsDays ? 7 : 3;
    public bool CanPrevious => _level switch { 2 => YearStart > 1, 1 => _displayDate.Year > 1, _ => _displayDate.Year > 1 || _displayDate.Month > 1 };
    public bool CanNext => _level switch { 2 => YearStart + 11 < 9999, 1 => _displayDate.Year < 9999, _ => _displayDate.Year < 9999 || _displayDate.Month < 12 };
    public DateTime FocusDate { get; private set; } = DateTime.Today;

    public void SetSelection(DateTime? date)
    {
        _selectedDate = date?.Date;
        OnPropertyChanged(nameof(Caption));
        Refresh();
    }
    public void Open()
    {
        _level = 0; _displayDate = FocusDate = _selectedDate ?? DateTime.Today; Refresh();
    }
    public void Broaden() { _level = Math.Min(2, _level + 1); Refresh(); }
    public void Move(int direction)
    {
        if (direction < 0 ? !CanPrevious : !CanNext) return;
        _displayDate = _level switch
        {
            1 => new(Math.Clamp(_displayDate.Year + direction, 1, 9999), _displayDate.Month, 1),
            2 => new(Math.Clamp(_displayDate.Year + direction * 12, 1, 9999), 1, 1),
            _ => new DateTime(_displayDate.Year, _displayDate.Month, 1).AddMonths(direction)
        };
        FocusDate = _displayDate; Refresh();
    }
    // Returns a committed day only at the final level.
    public DateTime? Choose(WorkflowCalendarCell cell)
    {
        if (IsDays) return cell.Date;
        _displayDate = FocusDate = cell.Date; _level--; Refresh(); return null;
    }
    public void FocusDay(DateTime date)
    {
        _displayDate = FocusDate = date.Date; _level = 0; Refresh();
    }
    private void Refresh()
    {
        Cells.Clear(); var today = DateTime.Today;
        if (_level == 2)
        {
            for (var year = YearStart; year <= Math.Min(9999, YearStart + 11); year++)
                Add(new(year, 1, 1), year.ToString(Russian), false, today.Year == year, _selectedDate?.Year == year);
        }
        else if (_level == 1)
        {
            for (var month = 1; month <= 12; month++)
            {
                var date = new DateTime(_displayDate.Year, month, 1);
                Add(date, Russian.TextInfo.ToTitleCase(date.ToString("MMM", Russian).TrimEnd('.')), false,
                    today.Year == date.Year && today.Month == month, _selectedDate?.Year == date.Year && _selectedDate?.Month == month);
            }
        }
        else
        {
            var first = new DateOnly(_displayDate.Year, _displayDate.Month, 1);
            var start = first.DayNumber - ((int)first.DayOfWeek + 6) % 7;
            for (var i = 0; i < 42; i++)
            {
                var dayNumber = Math.Clamp(start + i, DateOnly.MinValue.DayNumber, DateOnly.MaxValue.DayNumber);
                var date = DateOnly.FromDayNumber(dayNumber).ToDateTime(TimeOnly.MinValue);
                Add(date, start + i == dayNumber ? date.Day.ToString(Russian) : "", date.Month != first.Month,
                    date == today, date == _selectedDate);
            }
        }
        foreach (var name in new[] { nameof(Title), nameof(IsDays), nameof(Columns), nameof(CanPrevious), nameof(CanNext) }) OnPropertyChanged(name);
    }
    private void Add(DateTime date, string caption, bool outside, bool today, bool selected) =>
        Cells.Add(new(date, caption, _level == 2 ? date.Year.ToString(Russian) : date.ToString(IsDays ? "dddd, d MMMM yyyy" : "MMMM yyyy", Russian),
            outside, today && caption.Length > 0, selected && caption.Length > 0));
}

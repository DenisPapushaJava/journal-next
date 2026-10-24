using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Styling;

namespace Jornal.Desktop.Controls;

public sealed class JournalDatePicker : CalendarDatePicker
{
    protected override Type StyleKeyOverride => typeof(CalendarDatePicker);

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        if (e.NameScope.Find<Button>("PART_Button") is { } button)
            button.Theme = this.FindResource(ActualThemeVariant, "JournalCalendarButton") as ControlTheme;
    }
}

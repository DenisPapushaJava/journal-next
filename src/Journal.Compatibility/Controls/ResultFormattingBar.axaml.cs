using Avalonia.Controls;
using Avalonia.Interactivity;
using Jornal.Core.Results;
using Jornal.Desktop.ViewModels;

namespace Jornal.Desktop.Controls;

public partial class ResultFormattingBar : UserControl
{
    internal bool HasOpenPopup => ResultFontSize.IsDropDownOpen || ResultColorButton.Flyout?.IsOpen == true;

    public ResultFormattingBar() => InitializeComponent();

    private void OnChooseColor(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel { CanEditResult: true } model &&
            sender is Button { DataContext: ResultColorOption option })
            model.ResultTextColor = option.Hex;
        ResultColorButton.Flyout?.Hide();
    }

    private void OnDefaultColor(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel { CanEditResult: true } model)
            model.ResultTextColor = ResultTextStyle.DefaultColor;
        ResultColorButton.Flyout?.Hide();
    }
}

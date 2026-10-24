using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Jornal.Desktop.Controls;

public partial class OrderAnnotationsControl : UserControl
{
    public OrderAnnotationsControl() => InitializeComponent();
    private void OnClearCommentClick(object? sender, RoutedEventArgs e) => this.FindControl<TextBox>("OrderCommentInput")?.Focus();
}

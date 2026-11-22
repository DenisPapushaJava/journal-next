using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Jornal.Workflow.Desktop;

public partial class WorkflowOfficeConnectionWindow : Window
{
    private readonly Func<string?, string?, CancellationToken, Task<string?>>? _connect;
    private bool _connecting;

    public WorkflowOfficeConnectionWindow() => InitializeComponent();

    internal WorkflowOfficeConnectionWindow(string? address, Func<string?, string?, CancellationToken, Task<string?>> connect) : this()
    {
        ServerAddress.Text = address;
        if (!string.IsNullOrEmpty(address))
            AdministratorKey.PlaceholderText = "Оставьте пустым для сохранённого подключения";
        _connect = connect;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (string.IsNullOrWhiteSpace(ServerAddress.Text)) ServerAddress.Focus();
        else AdministratorKey.Focus();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (_connecting) e.Cancel = true;
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        AdministratorKey.Clear();
        base.OnClosed(e);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    private async void OnConnect(object? sender, RoutedEventArgs e)
    {
        if (_connect is null || _connecting) return;
        _connecting = true;
        ConnectionFields.IsEnabled = ConnectOffice.IsEnabled = CancelOfficeConnection.IsEnabled = false;
        ConnectionError.IsVisible = false;
        string? error;
        try { error = await _connect(ServerAddress.Text, AdministratorKey.Text, CancellationToken.None); }
        finally
        {
            _connecting = false;
            ConnectionFields.IsEnabled = ConnectOffice.IsEnabled = CancelOfficeConnection.IsEnabled = true;
        }
        if (error is null) Close(true);
        else
        {
            ConnectionError.Text = error;
            ConnectionError.IsVisible = true;
        }
    }
}

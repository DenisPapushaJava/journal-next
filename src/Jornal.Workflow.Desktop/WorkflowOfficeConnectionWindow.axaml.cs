using System.Net.NetworkInformation;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Jornal.Infrastructure.Diagnostics;
using Jornal.Workflow.Core;
using Jornal.Workflow.Infrastructure;

namespace Jornal.Workflow.Desktop;

public partial class WorkflowOfficeConnectionWindow : Window
{
    private static readonly Uri ZeroTierDownloads = new("https://www.zerotier.com/download/");
    private readonly Func<string?, string?, WorkflowOfficeRoute?, CancellationToken, Action?, Task<string?>>? _connect;
    private readonly Func<string?>? _switchProblem;
    private readonly Func<WorkflowOfficeSetupDraft, CancellationToken, Task>? _remember;
    private readonly Func<Uri, Task<bool>>? _openDownloadPage;
    private readonly Func<(bool Windows, bool Installed)> _readZeroTierStatus =
        static () => (OperatingSystem.IsWindows(), ZeroTierWindowsInstaller.IsInstalled);
    private readonly WorkflowOfficeStorageSettings? _saved;
    private readonly WorkflowOfficeSetup _setup = new();
    private CancellationTokenSource? _operation;
    private WorkflowOfficeSetupStep? _next;
    private string? _nodeId;
    private string? _formAddress;
    private string? _managedNetworkId;
    private bool _closing;
    private bool _finishing;

    public WorkflowOfficeConnectionWindow() => InitializeComponent();

    internal WorkflowOfficeConnectionWindow(WorkflowOfficeStorageSettings? saved, WorkflowOfficeSetupDraft? draft,
        Func<string?> switchProblem,
        Func<WorkflowOfficeSetupDraft, CancellationToken, Task> remember,
        Func<string?, string?, WorkflowOfficeRoute?, CancellationToken, Action?, Task<string?>> connect,
        WorkflowOfficeSetup? setup = null, Func<(bool Windows, bool Installed)>? readZeroTierStatus = null,
        Func<Uri, Task<bool>>? openDownloadPage = null) : this()
    {
        _saved = saved;
        ServerAddress.Text = draft?.Address ?? saved?.Address;
        _formAddress = NormalizedAddress();
        ShowConnectionHints();
        if (draft is not null)
        {
            NetworkId.Text = draft.NetworkId ??
                (draft.LinuxInterfaceName is { } old && ZeroTierClient.IsNetworkId(old) ? old : null);
        }
        _connect = connect;
        _switchProblem = switchProblem;
        _remember = remember;
        if (setup is not null) _setup = setup;
        if (readZeroTierStatus is not null) _readZeroTierStatus = readZeroTierStatus;
        _openDownloadPage = openDownloadPage;
        ServerAddress.PropertyChanged += OnServerAddressChanged;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (string.IsNullOrWhiteSpace(ServerAddress.Text)) ServerAddress.Focus();
        else AdministratorKey.Focus();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (_finishing) e.Cancel = true;
        base.OnClosing(e);
        if (e.Cancel) return;
        _closing = true;
        _operation?.Cancel();
    }

    protected override void OnClosed(EventArgs e)
    {
        ServerAddress.PropertyChanged -= OnServerAddressChanged;
        AdministratorKey.Clear();
        base.OnClosed(e);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    private void OnServerAddressChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != TextBox.TextProperty || _operation is not null) return;
        var address = NormalizedAddress();
        if (address == _formAddress) return;
        _formAddress = address;
        NetworkId.Clear();
        AdministratorKey.Clear();
        _managedNetworkId = null;
        ShowConnectionHints();
        ConnectionError.IsVisible = false;
        _next = null;
        ShowStatus(null);
        ConnectOffice.Label = "Подключиться";
    }

    private string? NormalizedAddress() =>
        Uri.TryCreate(ServerAddress.Text?.Trim(), UriKind.Absolute, out var address) ? address.AbsoluteUri : null;

    private void ShowConnectionHints()
    {
        var sameServer = _saved is not null && _formAddress == _saved.Address;
        AdministratorKey.PlaceholderText = sameServer
            ? "Для сохранённого подключения можно оставить пустым" : "Кнопка «Ключ администратора»";
        NetworkFields.IsVisible = !sameServer || string.IsNullOrWhiteSpace(_saved?.ZeroTierInterfaceId);
    }

    private async void OnCopyDeviceId(object? sender, RoutedEventArgs e)
    {
        if (_nodeId is not { } id || Clipboard is not { } clipboard) return;
        try { await clipboard.SetTextAsync(id); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!_closing) ShowError($"Не удалось скопировать ID устройства. ID: {id}");
            AppDiagnostics.Capture(DiagnosticOperation.Clipboard, ex);
        }
    }

    private async void OnDownloadZeroTier(object? sender, RoutedEventArgs e)
    {
        if (_next != WorkflowOfficeSetupStep.Install || !DownloadZeroTier.IsEnabled) return;
        DownloadZeroTier.IsEnabled = false;
        ConnectionError.IsVisible = false;
        try
        {
            if (await (_openDownloadPage?.Invoke(ZeroTierDownloads) ?? Launcher.LaunchUriAsync(ZeroTierDownloads))) return;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppDiagnostics.Capture(DiagnosticOperation.Unexpected, ex);
        }
        finally { if (!_closing) DownloadZeroTier.IsEnabled = true; }

        if (!_closing && _next == WorkflowOfficeSetupStep.Install)
            ShowError($"Не удалось открыть браузер. Страница загрузки — {ZeroTierDownloads}");
    }

    private async void OnConnect(object? sender, RoutedEventArgs e)
    {
        if (_connect is null || _remember is null || _operation is not null) return;
        using var operation = new CancellationTokenSource();
        _operation = operation;
        var token = operation.Token;
        var confirmManagedAddresses = _next == WorkflowOfficeSetupStep.EnableManagedAddresses &&
            string.Equals(NetworkId.Text?.Trim(), _managedNetworkId, StringComparison.OrdinalIgnoreCase);
        var networkInputVisible = NetworkFields.IsVisible;
        ConnectionFields.IsEnabled = ConnectOffice.IsEnabled = false;
        ConnectionError.IsVisible = false;
        _next = null;
        ShowStatus("Проверяем связь с сервером…");
        var connected = false;
        try
        {
            var server = WorkflowOfficeTransport.RequireServer(ServerAddress.Text);
            var key = AdministratorKey.Text;
            var saved = _saved?.Address == server.AbsoluteUri ? _saved : null;
            var savedInterface = saved?.ZeroTierInterfaceId;
            var useSavedInterface = !networkInputVisible && !string.IsNullOrWhiteSpace(savedInterface);
            if (_switchProblem?.Invoke() is { } blocked) { ShowError(blocked); return; }
            if (!string.IsNullOrWhiteSpace(key) || saved is null)
                key = WorkflowOfficeTransport.RequireAdminKey(key);
            var networkId = NetworkId.Text?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(networkId)) networkId = null;
            if (!useSavedInterface && networkId is not null && !ZeroTierClient.IsNetworkId(networkId))
            {
                Present(new(WorkflowOfficeSetupStep.NetworkId, "ID сети состоит из 16 шестнадцатеричных символов."));
                return;
            }
            WorkflowOfficeRoute? expectedRoute = !string.IsNullOrWhiteSpace(savedInterface) ? new(null, null, savedInterface) : null;
            await _remember(new(server.AbsoluteUri, networkId), token);
            token.ThrowIfCancellationRequested();
            NetworkId.Text = networkId;

            var (windows, installed) = _readZeroTierStatus();
            var state = await _setup.PrepareAsync(server, useSavedInterface ? null : networkId, windows, installed,
                ShowProgress, token, confirmManagedAddresses, expectedRoute);
            token.ThrowIfCancellationRequested();
            _managedNetworkId = state.Step == WorkflowOfficeSetupStep.EnableManagedAddresses ? networkId : null;
            if (networkId is not null && state.Step != WorkflowOfficeSetupStep.Ready) NetworkFields.IsVisible = true;
            if (state.Step != WorkflowOfficeSetupStep.Ready) { Present(state); return; }

            ShowStatus("Сервер доступен. Загружаем заявки…");
            var error = await _connect(server.AbsoluteUri, key, state.Route, token, () =>
            {
                SetFinishing(true);
                ShowStatus("Сохраняем подключение…");
            });
            if (!_closing && error is not null) ShowError(error);
            connected = error is null;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!_closing) ShowError(ex switch
            {
                WorkflowValidationException or InvalidOperationException or UnauthorizedAccessException or InvalidDataException => ex.Message,
                IOException { InnerException: HttpRequestException or OperationCanceledException } => ex.Message,
                IOException or HttpRequestException or JsonException or NetworkInformationException =>
                    "Не удалось подготовить подключение. Проверьте доступ к ZeroTier и локальным настройкам.",
                _ => "Не удалось подготовить подключение. Повторите проверку."
            });
            AppDiagnostics.Capture(DiagnosticOperation.Unexpected, ex);
        }
        finally
        {
            _operation = null;
            if (!_closing)
            {
                SetFinishing(false);
                ConnectionFields.IsEnabled = ConnectOffice.IsEnabled = true;
                ConnectOffice.Label = _next switch
                {
                    WorkflowOfficeSetupStep.Install => "Проверить установку",
                    WorkflowOfficeSetupStep.EnableManagedAddresses => "Разрешить адреса",
                    WorkflowOfficeSetupStep.WaitingApproval or WorkflowOfficeSetupStep.WaitingConfiguration or WorkflowOfficeSetupStep.WaitingRoute => "Проверить снова",
                    _ => "Подключиться"
                };
                if (_next == WorkflowOfficeSetupStep.NetworkId) NetworkId.Focus();
            }
        }
        if (connected && !_closing) Close(true);
    }

    private void SetFinishing(bool value)
    { _finishing = value; CancelOfficeConnection.IsEnabled = !value; }

    private void ShowProgress(WorkflowOfficeSetupState state)
    {
        if (!_closing) ShowStatus(state.Message, state.NodeId);
    }

    private void Present(WorkflowOfficeSetupState state)
    {
        if (_closing) return;
        _next = state.Step;
        switch (state.Step)
        {
            case WorkflowOfficeSetupStep.Install:
                ShowStatus("Скачайте и установите ZeroTier для Windows. Затем нажмите «Проверить установку».");
                break;
            case WorkflowOfficeSetupStep.NetworkId:
                NetworkFields.IsVisible = true;
                if (state.Message is { } error) ShowError(error);
                else ShowStatus("Введите ID офисной сети из панели «Сервер заявок».");
                break;
            case WorkflowOfficeSetupStep.Error:
                ShowError(state.Message!);
                break;
            default:
                ShowProgress(state);
                break;
        }
    }

    private void ShowError(string message)
    {
        ShowStatus(null);
        ConnectionError.Text = message;
        ConnectionError.IsVisible = true;
    }

    private void ShowStatus(string? message, string? nodeId = null)
    {
        if (ConnectionStatus.Text != message) ConnectionStatus.Text = message;
        ConnectionStatus.IsVisible = message is not null;
        DownloadZeroTier.IsVisible = _next == WorkflowOfficeSetupStep.Install;
        _nodeId = nodeId;
        CopyDeviceId.IsVisible = nodeId is not null;
    }
}

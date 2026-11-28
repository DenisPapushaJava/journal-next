using System.ComponentModel;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text.Json;
using Jornal.Workflow.Infrastructure;

namespace Jornal.Workflow.Server.Windows;

internal sealed class OfficeServerForm : Form
{
    private readonly TextBox _database = new() { Dock = DockStyle.Fill };
    private readonly TextBox _projects = new() { Dock = DockStyle.Fill };
    private readonly TextBox _calculationExchange = new() { Dock = DockStyle.Fill };
    private readonly TextBox _network = new() { Dock = DockStyle.Fill, MaxLength = 16 };
    private readonly TextBox _remoteAddress = new() { Dock = DockStyle.Fill, ReadOnly = true };
    private readonly CheckBox _autorun = new() { Text = "Запускать при входе в Windows", AutoSize = true };
    private readonly CheckBox _allowRemoteWrites = new() { Text = "Разрешить удалённое сохранение", AutoSize = true };
    private readonly Label _status = new() { AutoSize = true, MaximumSize = new(650, 0) };
    private readonly Label _remoteStatus = new() { AutoSize = true, MaximumSize = new(650, 0) };
    private readonly TableLayoutPanel _layout = new() { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1 };
    private readonly Button _stop = CreateButton("Остановить");
    private readonly Button _serve = CreateButton("Подключить");
    private readonly Button _installZeroTier = CreateButton("Установить ZeroTier");
    private readonly Button _copyAddress = CreateButton("Копировать адрес");
    private readonly Button _copyKey = CreateButton("Ключ администратора");
    private readonly Button _openLog = CreateButton("Журнал сервера");
    private readonly Button _technologistAccess = CreateButton("Доступ технологов…");
    private readonly ZeroTierClient _zeroTier = new();
    private readonly System.Windows.Forms.Timer _joinTimer = new() { Interval = 2500 };
    private readonly System.Windows.Forms.Timer _trayTimer = new() { Interval = 5000 };
    private readonly OfficeTray _tray;
    private string? _joiningNetwork;
    private DateTimeOffset _joinDeadline;
    private OfficeSettings? _settings;
    private bool _busy;
    private bool _closeRequested;
    private bool _hideOnStartup;
    private bool _restoringPanel;
    private bool _dependenciesReady;
    private bool _trayChecking;
    private bool _trayReady;

    public OfficeServerForm(bool startInTray)
    {
        _hideOnStartup = startInTray;
        Text = "Сервер заявок";
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new(700, 520);
        MinimumSize = new(716, 493);
        Padding = new Padding(16, 12, 16, 12);
        AutoScroll = true;
        MaximizeBox = false;
        Controls.Add(_layout);

        Add(new Label { Text = "База данных MDB", AutoSize = true });
        Add(Field(_database, "Выбрать…", () => { ChooseDatabase(); return Task.CompletedTask; }));
        Add(new Label { Text = "Рабочая папка проектов", AutoSize = true });
        Add(Field(_projects, "Выбрать…", () => { ChooseFolder(_projects, "Выберите рабочую папку проектов"); return Task.CompletedTask; }));
        Add(new Label { Text = "Папка обмена расчётами · необязательно", AutoSize = true });
        Add(Field(_calculationExchange, "Выбрать…", () =>
        {
            ChooseFolder(_calculationExchange, "Выберите локальную папку обмена на Яндекс Диске");
            return Task.CompletedTask;
        }));
        var test = CreateButton("Создать тестовую MDB");
        var start = CreateButton("Сохранить и запустить");
        var refresh = CreateButton("Обновить состояние");
        Add(Row(_autorun, test));
        Add(_allowRemoteWrites);
        Add(Row(start, _stop, refresh));
        Add(_status);

        Add(new Label { Text = "ZeroTier · Network ID", AutoSize = true, Margin = new(3, 13, 3, 0) });
        Add(Field(_network, "ZeroTier Central", () => ExecuteAsync(() => OpenAsync(ZeroTierClient.CentralAddress))));
        Add(_remoteStatus);
        Add(_remoteAddress);
        Add(Row(_serve, _copyAddress, _copyKey));
        Add(Row(_installZeroTier, _technologistAccess, _openLog));

        _settings = OfficeInstallation.Load();
        _database.Text = _settings?.DatabasePath ?? "";
        _projects.Text = _settings?.ProjectsRoot ?? "";
        _calculationExchange.Text = _settings?.CalculationExchangeRoot ?? "";
        _network.Text = _settings?.ZeroTierNetworkId ?? "";
        _autorun.Checked = _settings is null || OfficeInstallation.AutorunEnabled();
        _allowRemoteWrites.Checked = _settings?.AllowRemoteWrites == true;
        _tray = new OfficeTray(ShowPanel, ToggleServerAsync, () => ExecuteAsync(() => OpenAsync(OfficeInstallation.LogPath)), RequestClose);

        start.Click += async (_, _) => await ExecuteAsync(SaveAndStartAsync);
        test.Click += async (_, _) => await ExecuteAsync(CreateTestAsync);
        refresh.Click += async (_, _) => await ExecuteAsync(RefreshAsync);
        _stop.Click += async (_, _) => await ExecuteAsync(StopAsync);
        _serve.Click += async (_, _) => await ExecuteAsync(ChangeRemoteAsync);
        _installZeroTier.Click += async (_, _) => await ExecuteAsync(InstallZeroTierAsync);
        _network.TextChanged += (_, _) =>
        {
            StopWaitingForJoin();
            _remoteAddress.Clear();
            _remoteStatus.Text = "Введите Network ID приватной сети и нажмите «Подключить».";
            UpdateCommands();
        };
        _joinTimer.Tick += async (_, _) => await ExecuteAsync(CheckJoinAsync);
        _copyAddress.Click += async (_, _) => await ExecuteAsync(() => CopyAsync(_remoteAddress.Text));
        _copyKey.Click += async (_, _) => await ExecuteAsync(() => CopyAsync(_settings?.Key() ?? ""));
        _technologistAccess.Click += async (_, _) => await ExecuteAsync(OpenTechnologistAccessAsync);
        _openLog.Click += async (_, _) => await ExecuteAsync(() => OpenAsync(OfficeInstallation.LogPath));
        _trayTimer.Tick += async (_, _) => await ProbeTrayAsync();
        Resize += (_, _) =>
        {
            if (!_restoringPanel && WindowState == FormWindowState.Minimized) Hide();
        };
        FormClosing += (_, args) =>
        {
            if (args.CloseReason != CloseReason.UserClosing) return;
            args.Cancel = _busy || !_closeRequested;
            if (!args.Cancel) return;
            if (!_busy) Hide();
        };
        FormClosed += (_, _) => { _joinTimer.Dispose(); _trayTimer.Dispose(); _tray.Dispose(); };
        UpdateCommands();
    }

    protected override void SetVisibleCore(bool value)
    {
        if (value && _hideOnStartup)
        {
            _hideOnStartup = false;
            base.SetVisibleCore(false);
            return;
        }
        base.SetVisibleCore(value);
    }

    public async Task InitializeAsync(bool startInTray)
    {
        _trayTimer.Start();
        await ExecuteAsync(async () =>
        {
            if (startInTray && _settings is { } settings && !OfficeInstallation.IsRunning())
                await OfficeInstallation.StartAsync(settings);
            await RefreshAsync();
        });
    }

    public void ShowPanel()
    {
        _hideOnStartup = false;
        _restoringPanel = true;
        try
        {
            // WinForms applies WindowState to the native window only when the form is visible.
            Show();
            WindowState = FormWindowState.Normal;
        }
        finally { _restoringPanel = false; }
        Activate();
    }

    public void RequestClose()
    {
        _closeRequested = true;
        if (!_busy) Close();
    }

    private Task ToggleServerAsync() => ExecuteAsync(async () =>
    {
        if (OfficeInstallation.IsRunning()) await StopAsync();
        else if (_settings is { } settings)
        {
            await OfficeInstallation.StartAsync(settings);
            await RefreshAsync();
        }
        else ShowPanel();
    });

    private async Task ProbeTrayAsync()
    {
        if (_trayChecking || _busy) return;
        _trayChecking = true;
        try
        {
            var settings = _settings;
            _trayReady = settings is not null && OfficeInstallation.IsRunning() &&
                await OfficeInstallation.IsListeningAsync(settings.LocalAddress) && _dependenciesReady &&
                (settings.ZeroTierListener is not { } remote || await OfficeInstallation.IsListeningAsync(remote.Url(settings.Port)));
            if (!IsDisposed) UpdateCommands();
        }
        finally { _trayChecking = false; }
    }

    private void Add(Control control)
    {
        control.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _layout.Controls.Add(control);
    }

    private static Button CreateButton(string caption) => new() { Text = caption, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };

    private static FlowLayoutPanel Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        row.Controls.AddRange(controls);
        return row;
    }

    private static TableLayoutPanel Field(TextBox box, string caption, Func<Task> choose)
    {
        var field = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Margin = Padding.Empty };
        field.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        field.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var button = CreateButton(caption);
        button.Click += async (_, _) => await choose();
        field.Controls.Add(box, 0, 0);
        field.Controls.Add(button, 1, 0);
        return field;
    }

    private void ChooseDatabase()
    {
        using var dialog = new OpenFileDialog { Title = "Выберите MDB программы «Заявки»", Filter = "База данных Access (*.mdb)|*.mdb" };
        if (dialog.ShowDialog(this) == DialogResult.OK) _database.Text = dialog.FileName;
    }

    private void ChooseFolder(TextBox field, string title)
    {
        using var dialog = new FolderBrowserDialog { Description = title, UseDescriptionForTitle = true };
        if (dialog.ShowDialog(this) == DialogResult.OK) field.Text = dialog.SelectedPath;
    }

    private async Task CreateTestAsync()
    {
        var data = await OfficeInstallation.CreateTestDataAsync();
        _database.Text = data.DatabasePath;
        _projects.Text = data.ProjectsRoot;
        _status.Text = "Тестовая MDB готова. Нажмите «Сохранить и запустить».";
    }

    private async Task SaveAndStartAsync()
    {
        var database = WorkflowMdbStore.NormalizePath(_database.Text.Trim());
        var projects = _projects.Text.Trim();
        if (!await Task.Run(() => File.Exists(database)))
            throw new IOException("Выберите доступную MDB или создайте тестовую.");
        if (!Path.IsPathFullyQualified(projects) || !await Task.Run(() => Directory.Exists(projects)))
            throw new IOException("Выберите доступную рабочую папку проектов.");
        var exchange = _calculationExchange.Text.Trim();
        if (exchange.Length > 0 && (!Path.IsPathFullyQualified(exchange) || !await Task.Run(() => Directory.Exists(exchange))))
            throw new IOException("Выберите доступную папку обмена расчётами.");

        var settings = _settings is { } previous
            ? previous.ForZeroTier() with { DatabasePath = database, ProjectsRoot = Path.GetFullPath(projects) }
            : OfficeSettings.Create(database, Path.GetFullPath(projects));
        settings = settings with { AllowRemoteWrites = _allowRemoteWrites.Checked,
            CalculationExchangeRoot = exchange.Length > 0 ? Path.GetFullPath(exchange) : null };
        OfficeInstallation.CheckDataLocations(settings);
        await ApplySettingsAsync(settings);
        OfficeInstallation.SetAutorun(_autorun.Checked);
        await RefreshAsync();
    }

    private async Task OpenTechnologistAccessAsync()
    {
        if (_settings is not { } settings) return;
        var store = new WorkflowMdbStore(settings.DatabasePath);
        var workspace = await store.ReadOrdersAsync([]);
        var databaseId = store.DatabaseId ?? throw new IOException("Не удалось определить ID MDB.");
        var existing = settings.RemoteAccessDatabaseId == databaseId ? settings.TechnologistKeys ?? [] : [];
        using var dialog = new OfficeTechnologistAccessForm(workspace.Employees, existing);
        if (dialog.ShowDialog(this) != DialogResult.OK || existing.SequenceEqual(dialog.Keys)) return;
        await ApplySettingsAsync(settings with { RemoteAccessDatabaseId = databaseId, TechnologistKeys = dialog.Keys });
        await RefreshAsync();
    }

    private async Task ApplySettingsAsync(OfficeSettings settings)
    {
        var install = OfficeInstallation.NeedsInstallation();
        if (OfficeInstallation.IsRunning() && (install || settings != _settings))
            await OfficeInstallation.StopAsync();
        if (install)
            await Task.Run(OfficeInstallation.Install);
        OfficeInstallation.Save(settings);
        _settings = settings;
        if (!OfficeInstallation.IsRunning()) await OfficeInstallation.StartAsync(settings);
    }

    private async Task StopAsync()
    {
        StopWaitingForJoin();
        await OfficeInstallation.StopAsync();
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        var settings = _settings;
        _dependenciesReady = false;
        _status.Text = "Сервер остановлен.";
        _remoteAddress.Clear();
        _remoteStatus.Text = "Введите Network ID приватной сети и нажмите «Подключить».";
        if (settings is null) return;

        if (OfficeInstallation.IsRunning())
        {
            try
            {
                var (message, ready) = await OfficeInstallation.StatusAsync(settings);
                _status.Text = message;
                _dependenciesReady = ready;
            }
            catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or JsonException)
            { _status.Text = "Сервер запущен, проверка MDB пока недоступна."; }
        }
        var networkId = _network.Text.Trim().ToLowerInvariant();
        if (ZeroTierClient.IsNetworkId(networkId))
            await CheckRemoteStatusAsync(settings, networkId);
        _trayReady = _dependenciesReady && (settings.ZeroTierListener is null || _remoteAddress.TextLength > 0);
    }

    private async Task CheckRemoteStatusAsync(OfficeSettings settings, string networkId)
    {
        try
        {
            var network = await _zeroTier.ReadAsync(networkId);
            _remoteStatus.Text = network.Description;
            if (network.Binding is not { } binding) return;
            if (settings.ZeroTierNetworkId != networkId || settings.ZeroTierListener != OfficeZeroTierListener.Capture(binding))
            {
                _remoteStatus.Text = settings.ZeroTierListener is null
                    ? "Сеть подключена. Нажмите «Подключить», чтобы открыть порт «Заявок»."
                    : "Параметры сети изменились. Отключите доступ и подключитесь снова.";
                return;
            }
            if (!OfficeInstallation.IsRunning())
            {
                _remoteStatus.Text = "Сеть подключена. Запустите сервер, чтобы открыть доступ.";
                return;
            }
            var address = settings.ZeroTierListener.Url(settings.Port);
            if (!await OfficeInstallation.IsListeningAsync(address))
            {
                _remoteStatus.Text = "Сеть подключена. Порт сервера пока недоступен.";
                return;
            }
            _remoteAddress.Text = address.AbsoluteUri;
            _remoteStatus.Text = "Порт «Заявок» открыт в приватной сети ZeroTier.";
        }
        catch (Exception exception) when (IsNetworkError(exception))
        {
            _remoteStatus.Text = exception is JsonException ? "ZeroTier вернул неожиданный ответ." : exception.Message;
        }
    }

    private async Task ChangeRemoteAsync()
    {
        if (_settings is not { } settings) return;
        if (_joiningNetwork is not null)
        {
            StopWaitingForJoin();
            await RefreshAsync();
            return;
        }
        if (settings.ZeroTierListener is not null)
        {
            await ApplySettingsAsync(settings with { ZeroTierListener = null });
            await RefreshAsync();
            return;
        }
        if (!OfficeInstallation.IsRunning()) throw new InvalidOperationException("Сначала запустите сервер.");

        var id = _network.Text.Trim().ToLowerInvariant();
        if (settings.ZeroTierNetworkId is null || OfficeInstallation.NeedsInstallation())
        {
            settings = settings.ForZeroTier();
            await ApplySettingsAsync(settings);
        }
        await _zeroTier.RequestJoinAsync(id, default);
        settings = settings with { ZeroTierNetworkId = id };
        OfficeInstallation.Save(settings);
        _settings = settings;
        _joiningNetwork = id;
        _joinDeadline = DateTimeOffset.UtcNow.AddMinutes(2);
        await CheckJoinAsync();
        if (_joiningNetwork is not null) _joinTimer.Start();
    }

    private async Task CheckJoinAsync()
    {
        if (_joiningNetwork is not { } id || _settings is not { } settings) return;
        try
        {
            var network = await _zeroTier.ReadAsync(id);
            _remoteStatus.Text = network.Description;
            if (network.Binding is not { } binding)
            {
                if ((!network.CanWait && network.Status != "NOT_JOINED") || DateTimeOffset.UtcNow >= _joinDeadline)
                    StopWaitingForJoin();
                return;
            }

            StopWaitingForJoin();
            _remoteStatus.Text = "Сеть подключена. Подтвердите настройку порта в запросе Windows.";
            await ZeroTierWindowsSetup.AllowApiAsync(binding, settings.Port);
            if ((await _zeroTier.ReadAsync(id)).Binding != binding)
                throw new InvalidOperationException("Параметры сети изменились. Нажмите «Подключить» повторно.");
            await ApplySettingsAsync(settings with { ZeroTierListener = OfficeZeroTierListener.Capture(binding) });
            await RefreshAsync();
        }
        catch
        {
            StopWaitingForJoin();
            throw;
        }
    }

    private void StopWaitingForJoin()
    {
        _joiningNetwork = null;
        _joinTimer.Stop();
    }

    private async Task InstallZeroTierAsync()
    {
        _remoteStatus.Text = "Загружается установщик ZeroTier…";
        await ZeroTierWindowsSetup.InstallClientAsync();
        await RefreshAsync();
    }

    private static bool IsNetworkError(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or HttpRequestException or
            InvalidDataException or JsonException or NetworkInformationException;

    private async Task ExecuteAsync(Func<Task> operation)
    {
        if (_busy) return;
        _busy = true;
        _layout.Enabled = false;
        UseWaitCursor = true;
        try { await operation(); }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _busy = false;
            _layout.Enabled = true;
            UseWaitCursor = false;
            if (_closeRequested) Close();
            else UpdateCommands();
        }
    }

    private void UpdateCommands()
    {
        var running = OfficeInstallation.IsRunning();
        var published = _settings?.ZeroTierListener is not null;
        _serve.Enabled = _joiningNetwork is not null || published || running && ZeroTierClient.IsNetworkId(_network.Text.Trim());
        _serve.Text = _joiningNetwork is not null ? "Прекратить ожидание" : published ? "Отключить доступ" : "Подключить";
        _network.ReadOnly = published || _joiningNetwork is not null;
        _installZeroTier.Enabled = !ZeroTierWindowsSetup.ClientInstalled;
        _copyAddress.Enabled = _remoteAddress.TextLength > 0;
        _copyKey.Enabled = _settings is not null;
        _technologistAccess.Enabled = _settings is not null;
        _stop.Enabled = running;
        _openLog.Enabled = File.Exists(OfficeInstallation.LogPath);
        _tray.Update(running, _trayReady, !_busy, !_busy && _openLog.Enabled);
    }

    private static Task CopyAsync(string text)
    {
        if (text.Length > 0) Clipboard.SetText(text);
        return Task.CompletedTask;
    }

    private static Task OpenAsync(string target)
    {
        using var process = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        return Task.CompletedTask;
    }
}

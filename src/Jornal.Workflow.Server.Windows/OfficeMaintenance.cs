using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Jornal.Workflow.Server.Windows;

internal static partial class OfficeInstallation
{
    private static string LegacyBootMarker => Path.Combine(DirectoryPath, "boot-task.enabled");

    public static async Task InstallFromSetupAsync()
    {
        var settings = Load();
        var runningSettings = IsRunning() ? settings : null;
        var autorun = AutorunEnabled();
        CheckDataLocations(settings);
        CheckInstalledTree();
        var id = Guid.NewGuid().ToString("N");
        var staging = Path.Combine(DirectoryPath, "install-" + id);
        var previous = Path.Combine(DirectoryPath, "previous-" + id);
        var replaced = false;
        var enabledAutorun = false;
        try
        {
            StageInstallation(staging);
            var remote = runningSettings?.ZeroTierListener?.Url(runningSettings.Port);
            var checkRemote = remote is not null && await IsListeningAsync(remote);
            var checkStorage = runningSettings is not null && await StorageReadyAsync(runningSettings);
            await PrepareMaintenanceAsync();
            ReplaceInstallation(staging, previous);
            replaced = true;
            if (autorun) SetAutorun(true);
            if (runningSettings is not null)
            {
                await StartAsync(runningSettings);
                if (checkStorage)
                {
                    var (message, ready) = await StatusAsync(runningSettings);
                    if (!ready) throw new IOException(message);
                }
                if (checkRemote) await WaitForListenerAsync(remote!);
            }
            if (File.Exists(LegacyBootMarker))
            {
                if (!autorun)
                {
                    SetAutorun(true);
                    enabledAutorun = true;
                }
                await RemoveLegacyBootTaskAsync();
            }
            DeleteStaging(previous);
        }
        catch (Exception installationError)
        {
            var restored = false;
            try
            {
                if (enabledAutorun) SetAutorun(false);
                if (replaced && Directory.Exists(previous))
                {
                    await PrepareMaintenanceAsync();
                    Directory.Move(AppDirectory, staging);
                    try { Directory.Move(previous, AppDirectory); }
                    catch
                    {
                        Directory.Move(staging, AppDirectory);
                        throw;
                    }
                    restored = true;
                }
                if (runningSettings is not null && !IsRunning()) await StartAsync(runningSettings);
            }
            catch (Exception recoveryError)
            {
                throw new IOException($"{installationError.Message}\nНе удалось восстановить запуск сервера. {recoveryError.Message}", recoveryError);
            }
            if (restored)
                throw new IOException($"Предыдущая версия сервера восстановлена. {installationError.Message}", installationError);
            throw;
        }
        finally { DeleteStaging(staging); }
    }

    private static async Task<bool> StorageReadyAsync(OfficeSettings settings)
    {
        try { return (await StatusAsync(settings)).Ready; }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
        { return false; }
    }

    public static async Task RemoveSettingsAsync()
    {
        var settings = Load();
        CheckDataLocations(settings);
        await PrepareMaintenanceAsync();
        CheckInstalledTree();
        await RemoveLegacyBootTaskAsync();
        if (settings?.ZeroTierNetworkId is { Length: 16 } || File.Exists(ZeroTierWindowsSetup.RuleMarker))
            await ZeroTierWindowsSetup.RemoveApiRulesAsync();
        SetAutorun(false);
        File.Delete(SettingsPath);
        for (var index = 0; index <= OfficeLogWriter.Keep; index++)
            File.Delete(OfficeLogWriter.PartPath(LogPath, index));
        File.Delete(ZeroTierWindowsSetup.RuleMarker);
        foreach (var file in Directory.EnumerateFiles(DirectoryPath, "settings.json.*.tmp"))
        {
            var name = Path.GetFileName(file);
            if (name.Length == 50 && Guid.TryParseExact(name[14..^4], "N", out _)) File.Delete(file);
        }
        foreach (var folder in RetiredFolders())
        {
            CheckInstalledTree(folder);
            Directory.Delete(folder, recursive: true);
        }
    }

    private static async Task RemoveLegacyBootTaskAsync()
    {
        if (!File.Exists(LegacyBootMarker)) return;
        var taskName = "JournalOfficeServer-" + UserSid;
        var command = "$ErrorActionPreference='Stop'; Get-ScheduledTask -TaskPath '\\' | " +
            $"Where-Object {{ $_.TaskName -eq '{taskName}' }} | Unregister-ScheduledTask -Confirm:$false";
        if (await RunElevatedPowerShellAsync(command) != 0)
            throw new IOException("Не удалось удалить прежнюю задачу запуска. Подтвердите настройку в Windows и повторите обновление.");
        File.Delete(LegacyBootMarker);
    }

    internal static Task<int> RunElevatedPowerShellAsync(string command) => RunElevatedAsync(
        Path.Combine("WindowsPowerShell", "v1.0", "powershell.exe"), "-NoProfile", "-NonInteractive", "-EncodedCommand",
        Convert.ToBase64String(Encoding.Unicode.GetBytes(command)));

    internal static async Task<int> RunElevatedAsync(string executable, params string[] arguments)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, executable))
        {
            UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(start) ?? throw new IOException("Windows не запустила настройку.");
            await process.WaitForExitAsync();
            return process.ExitCode;
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            throw new InvalidOperationException("Действие отменено в запросе Windows.", exception);
        }
    }

    public static void CheckDataLocations(OfficeSettings? settings)
    {
        if (settings is null) return;
        foreach (var folder in RetiredFolders().Prepend(AppDirectory))
        {
            if (Inside(settings.DatabasePath, folder) || Inside(settings.ProjectsRoot, folder) ||
                settings.CalculationExchangeRoot is { } exchange && Inside(exchange, folder))
                throw new IOException("Переместите MDB и проекты из папки установки и укажите новые пути в панели сервера.");
        }
    }

    private static IEnumerable<string> RetiredFolders() => Directory.Exists(DirectoryPath)
        ? Directory.EnumerateDirectories(DirectoryPath).Where(folder => Path.GetFileName(folder).Split('-', 2) is [var prefix, var suffix] &&
            prefix is "install" or "previous" && Guid.TryParseExact(suffix, "N", out _)) : [];

    private static bool Inside(string path, string folder)
    {
        var relative = Path.GetRelativePath(folder, path);
        return !Path.IsPathRooted(relative) && relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static void CheckInstalledTree() => CheckInstalledTree(AppDirectory);

    private static void CheckInstalledTree(string root)
    {
        if (!Directory.Exists(root)) return;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var folder))
        {
            if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("В папке установки найдена перенаправленная папка. Проверьте установку перед удалением.");
            foreach (var child in Directory.EnumerateDirectories(folder)) pending.Push(child);
        }
    }

    private static async Task PrepareMaintenanceAsync()
    {
        if (EventWaitHandle.TryOpenExisting(PanelEventName("close"), out var close))
        {
            using (close) close.Set();
            for (var attempt = 0; attempt < 100 && IsPanelOpen(); attempt++) await Task.Delay(100);
            if (IsPanelOpen()) throw new IOException("Панель сервера занята. Дождитесь завершения операции и повторите установку.");
        }
        await StopAsync();
        for (var attempt = 0; attempt < 20 && OtherInstalledProcess(); attempt++) await Task.Delay(100);
        if (OtherInstalledProcess())
            throw new IOException("Закройте окно предыдущей версии «Сервера заявок» и повторите действие.");
    }

    private static bool IsPanelOpen()
    {
        if (!EventWaitHandle.TryOpenExisting(PanelEventName("show"), out var signal)) return false;
        signal.Dispose();
        return true;
    }

    private static bool OtherInstalledProcess()
    {
        using var current = Process.GetCurrentProcess();
        var processes = Process.GetProcessesByName("JournalOfficeServer");
        try
        {
            foreach (var process in processes)
            {
                try
                {
                    if (process.Id != current.Id &&
                        string.Equals(process.MainModule?.FileName, Executable, StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch (Exception exception) when (exception is Win32Exception or InvalidOperationException) { }
            }
            return false;
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }
}

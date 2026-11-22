using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jornal.Workflow.Infrastructure;
using Microsoft.Win32;

namespace Jornal.Workflow.Server.Windows;

internal sealed record OfficeSettings(string DatabasePath, string ProjectsRoot, int Port, string ProtectedKey,
    string? ZeroTierNetworkId = null, OfficeZeroTierListener? ZeroTierListener = null,
    Guid? RemoteAccessDatabaseId = null, OfficeTechnologistKey[]? TechnologistKeys = null, bool AllowRemoteWrites = false)
{
    public string Key() => ReadKey(ProtectedKey);

    public OfficeWorkAccess WorkAccess() => new(RemoteAccessDatabaseId,
        RemoteAccessDatabaseId is { } database && database != Guid.Empty
            ? TechnologistKeys?.Select(key => key.WorkKey()).ToArray() ?? []
            : [], AllowRemoteWrites);

    [JsonIgnore]
    public Uri LocalAddress => new($"http://127.0.0.1:{Port}/");

    public static OfficeSettings Create(string databasePath, string projectsRoot)
        => new(databasePath, projectsRoot, AllocatePort(), GenerateKey(), "");

    public static string GenerateKey()
    {
        var random = Encoding.ASCII.GetBytes(Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
        try { return Convert.ToBase64String(ProtectedData.Protect(random, null, DataProtectionScope.CurrentUser)); }
        finally { CryptographicOperations.ZeroMemory(random); }
    }

    public static string ReadKey(string protectedKey)
    {
        var key = ProtectedData.Unprotect(Convert.FromBase64String(protectedKey), null, DataProtectionScope.CurrentUser);
        try { return Encoding.ASCII.GetString(key); }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    public OfficeSettings ForZeroTier() => ZeroTierNetworkId is null
        ? this with { Port = AllocatePort(), ZeroTierNetworkId = "" } : this;

    private static int AllocatePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}

internal sealed record OfficeTechnologistKey(Guid EmployeeId, string ProtectedKey)
{
    public string Key() => OfficeSettings.ReadKey(ProtectedKey);

    public OfficeWorkKey WorkKey()
    {
        var key = Encoding.ASCII.GetBytes(Key());
        try { return new(EmployeeId, SHA256.HashData(key)); }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
}

internal static partial class OfficeInstallation
{
    private const string AppName = "JournalWorkflowOfficeServer";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string BuildFile = "install-build.txt";
    public static string DirectoryPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);
    private static string SettingsPath => Path.Combine(DirectoryPath, "settings.json");
    private static string AppDirectory => Path.Combine(DirectoryPath, "app");
    public static string Executable => Path.Combine(AppDirectory, "JournalOfficeServer.exe");
    public static string LogPath => Path.Combine(DirectoryPath, "server.log");
    private static string AutorunCommand => $"\"{Executable}\" --tray";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly HttpClient LocalClient = new(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    public static string UserSid { get; } = GetUserSid();
    private static string LocalStopEvent => $@"Local\{AppName}-{UserSid}";
    private static string StopEventName => $@"Global\{AppName}-{UserSid}";

    private static string GetUserSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value ?? throw new InvalidOperationException("Не удалось определить учётную запись Windows.");
    }

    public static string PanelEventName(string action) => LocalStopEvent + "-panel-" + action;

    public static OfficeSettings? Load() => File.Exists(SettingsPath)
        ? JsonSerializer.Deserialize<OfficeSettings>(File.ReadAllText(SettingsPath))
        : null;

    public static void Save(OfficeSettings settings)
    {
        Directory.CreateDirectory(DirectoryPath);
        var temporary = SettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temporary, SettingsPath, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    public static bool AutorunEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        var value = key?.GetValue(AppName) as string;
        return string.Equals(value, AutorunCommand, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, $"\"{Executable}\" --background", StringComparison.OrdinalIgnoreCase);
    }

    public static void SetAutorun(bool enabled)
    {
        if (enabled)
        {
            if (AutorunCommand.Length > 260) throw new InvalidOperationException("Путь к программе слишком длинный для автозапуска.");
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            key.SetValue(AppName, AutorunCommand);
        }
        else if (AutorunEnabled())
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            key?.DeleteValue(AppName, throwOnMissingValue: false);
        }
    }

    public static bool NeedsInstallation()
    {
        if (!File.Exists(Executable)) return true;
        if (string.Equals(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), AppDirectory, StringComparison.OrdinalIgnoreCase))
            return false;
        var sourceBuild = Path.Combine(AppContext.BaseDirectory, BuildFile);
        var installedBuild = Path.Combine(AppDirectory, BuildFile);
        return !File.Exists(sourceBuild) || !File.Exists(installedBuild) || File.ReadAllText(sourceBuild) != File.ReadAllText(installedBuild);
    }

    public static void Install()
    {
        var id = Guid.NewGuid().ToString("N");
        var staging = Path.Combine(DirectoryPath, "install-" + id);
        var previous = Path.Combine(DirectoryPath, "previous-" + id);
        try
        {
            StageInstallation(staging);
            ReplaceInstallation(staging, previous);
            DeleteStaging(previous);
        }
        finally { DeleteStaging(staging); }
    }

    private static void StageInstallation(string staging)
    {
        CopyBundle(AppContext.BaseDirectory, staging);
        if (!File.Exists(Path.Combine(staging, BuildFile)) ||
            !File.Exists(Path.Combine(staging, "AccessHost", "Jornal.AccessHost.exe")))
            throw new IOException("AccessHost отсутствует. Распакуйте архив целиком и повторите установку.");
    }

    private static void ReplaceInstallation(string staging, string previous)
    {
        if (Directory.Exists(AppDirectory)) Directory.Move(AppDirectory, previous);
        try { Directory.Move(staging, AppDirectory); }
        catch
        {
            if (Directory.Exists(previous)) Directory.Move(previous, AppDirectory);
            throw;
        }
    }

    private static void DeleteStaging(string directory)
    {
        if (!Directory.Exists(directory)) return;
        try { Directory.Delete(directory, recursive: true); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The enabled version and the user's settings are outside this directory.
        }
    }

    private static void CopyBundle(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        foreach (var folder in Directory.EnumerateDirectories(source))
            CopyBundle(folder, Path.Combine(destination, Path.GetFileName(folder)));
    }

    private static EventWaitHandle? OpenStopSignal()
    {
        // A setup launched from v15 can still find the old per-session server.
        if (EventWaitHandle.TryOpenExisting(StopEventName, out var signal)) return signal;
        return EventWaitHandle.TryOpenExisting(LocalStopEvent, out signal) ? signal : null;
    }

    public static bool IsRunning()
    {
        using var signal = OpenStopSignal();
        return signal is not null;
    }

    public static async Task StopAsync()
    {
        var signal = OpenStopSignal();
        if (signal is null) return;
        using (signal) signal.Set();
        for (var attempt = 0; attempt < 320; attempt++)
        {
            if (!IsRunning()) return;
            await Task.Delay(125);
        }
        throw new IOException("Сервер ещё завершает работу. Обновите состояние через несколько секунд.");
    }

    public static async Task StartAsync(OfficeSettings settings)
    {
        using var process = Process.Start(new ProcessStartInfo(Executable, "--background")
            { UseShellExecute = false, WorkingDirectory = DirectoryPath })
            ?? throw new IOException("Не удалось запустить сервер.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!process.HasExited && !deadline.IsCancellationRequested)
        {
            try
            {
                using var response = await LocalClient.GetAsync(new Uri(settings.LocalAddress, "health"), deadline.Token);
                if (response.IsSuccessStatusCode && IsRunning()) return;
            }
            catch (HttpRequestException) { }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested) { break; }
            await Task.Delay(150);
        }
        throw new IOException("Сервер не ответил. Подробности в журнале сервера.");
    }

    public static async Task<(string Text, bool Ready)> StatusAsync(OfficeSettings settings)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(settings.LocalAddress, "api/v1/status"));
        request.Headers.Authorization = new("Bearer", settings.Key());
        using var response = await LocalClient.SendAsync(request);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var status = body.RootElement;
        var database = status.GetProperty("database").GetString() switch
        {
            "ready" => "MDB подключена",
            "file-unavailable" => "MDB недоступна. Проверьте путь и доступ к файлу.",
            "read-failed" => "Не удалось прочитать MDB. Откройте журнал сервера.",
            _ => "Проверьте подключение к MDB."
        };
        var projects = status.GetProperty("projects").GetString() == "available"
            ? "Рабочая папка доступна."
            : "Рабочая папка недоступна.";
        return ($"Сервер запущен. {database} {projects}", status.GetProperty("ready").GetBoolean());
    }

    public static async Task<bool> IsListeningAsync(Uri address)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try { return await LocalClient.GetStringAsync(new Uri(address, "health"), timeout.Token) == "{\"status\":\"ok\"}"; }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException) { return false; }
    }

    public static async Task WaitForListenerAsync(Uri address)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        while (!deadline.IsCancellationRequested)
        {
            if (await IsListeningAsync(address)) return;
            await Task.Delay(500);
        }
        throw new IOException("Сервер запустился, но порт ZeroTier не ответил. Проверьте состояние сети.");
    }

    public static async Task<(string DatabasePath, string ProjectsRoot)> CreateTestDataAsync()
    {
        var data = Path.Combine(DirectoryPath, "test-data");
        var projects = Path.Combine(data, "projects");
        Directory.CreateDirectory(projects);
        var database = Path.Combine(data, "JournalNext-Test.mdb");
        if (!File.Exists(database))
            await new WorkflowAccessClient().SendAsync(new(1, WorkflowAccessOperation.Create, database));
        return (database, projects);
    }

    public static async Task<int> RunBackgroundAsync()
    {
        if (EventWaitHandle.TryOpenExisting(LocalStopEvent, out var legacy))
        {
            legacy.Dispose();
            return 0;
        }
        var security = new EventWaitHandleSecurity();
        security.AddAccessRule(new EventWaitHandleAccessRule(new SecurityIdentifier(UserSid),
            EventWaitHandleRights.FullControl, AccessControlType.Allow));
        using var stop = EventWaitHandleAcl.Create(false, EventResetMode.ManualReset, StopEventName, out var created, security);
        if (!created) return 0;
        Directory.CreateDirectory(DirectoryPath);
        using var log = new OfficeLogWriter(LogPath);
        var output = TextWriter.Synchronized(log);
        Console.SetOut(output);
        Console.SetError(output);
        using var cancellation = new CancellationTokenSource();
        var registration = ThreadPool.RegisterWaitForSingleObject(stop, (_, _) => cancellation.Cancel(),
            null, Timeout.Infinite, executeOnlyOnce: true);
        try
        {
            Console.WriteLine($"[{DateTimeOffset.Now:O}] Запуск сервера.");
            // AccessHost inherits the server's working directory. Keep it independent of the setup media.
            Directory.SetCurrentDirectory(DirectoryPath);
            var settings = Load() ?? throw new InvalidOperationException("Сохраните параметры сервера через окно настройки.");
            Environment.SetEnvironmentVariable("JOURNAL_OFFICE_SERVER_TOKEN", settings.Key());
            Environment.SetEnvironmentVariable("OfficeServer__DatabasePath", settings.DatabasePath);
            Environment.SetEnvironmentVariable("OfficeServer__ProjectsRoot", settings.ProjectsRoot);
            Environment.SetEnvironmentVariable("OfficeServer__Port", settings.Port.ToString(CultureInfo.InvariantCulture));
            Environment.SetEnvironmentVariable("Logging__LogLevel__Default", "Warning");
            var workAccess = settings.WorkAccess();
            var local = OfficeServerApplication.RunAsync([], cancellation.Token, workAccess: workAccess);
            var remote = OfficeZeroTierHost.RunAsync(settings, workAccess, cancellation.Token);
            try { return await local; }
            finally
            {
                await cancellation.CancelAsync();
                await remote;
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return 0; }
        catch (Exception exception)
        {
            await output.WriteLineAsync(exception.ToString());
            return 1;
        }
        finally
        {
            using var unregistered = new ManualResetEvent(false);
            if (registration.Unregister(unregistered)) unregistered.WaitOne();
            Console.WriteLine($"[{DateTimeOffset.Now:O}] Сервер остановлен.");
        }
    }
}

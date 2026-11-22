using System.Text.Json;

namespace Jornal.Workflow.Desktop;

internal sealed record WorkflowOfficeConnection(string Address, string Token)
{
    private const UnixFileMode PrivateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    internal static string SettingsPath => Path.Combine(WorkflowIdentity.ProfileDirectory, "office-connection.json");

    internal static WorkflowOfficeConnection? Load(string path)
    {
        if (!File.Exists(path)) return null;
        if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(path) & ~PrivateMode) != 0)
            throw new InvalidDataException("Локальный файл удалённого подключения доступен другим пользователям.");
        using var stream = File.OpenRead(path);
        var connection = stream.Length <= 2048 ? JsonSerializer.Deserialize<WorkflowOfficeConnection>(stream) : null;
        if (connection is null || !(IsSessionToken(connection.Token) || IsStorageToken(connection.Token)))
            throw new InvalidDataException("Локальный файл удалённого подключения повреждён.");
        WorkflowOfficePreviewClient.RequireServer(connection.Address);
        return connection;
    }

    internal static bool IsSessionToken(string? token) =>
        IsToken(token, "session.");

    internal static bool IsStorageToken(string? token) => IsToken(token, "storage.");

    private static bool IsToken(string? token, string prefix) =>
        token is { Length: > 8 and <= 512 } && token.StartsWith(prefix, StringComparison.Ordinal) &&
        token.AsSpan(8).IndexOfAnyExcept("0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz-_") < 0;

    internal void Save(string path)
    {
        if (!(IsSessionToken(Token) || IsStorageToken(Token))) throw new InvalidDataException("Сервер не выдал рабочий токен.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = PrivateMode;
        try
        {
            using (var output = new FileStream(temporary, options)) JsonSerializer.Serialize(output, this);
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }
}

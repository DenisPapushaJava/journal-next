using System.Text.Json;
using Jornal.Core.Results;

namespace Jornal.Desktop.Clipboard;

// Только собственные пустые временные каталоги. Не удаляем их при закрытии:
// пользователь может вставить папки из буфера уже после выхода из Journal.
public sealed class TemporaryFolderStore(string? directory = null, TimeProvider? clock = null)
{
    private readonly string _root = Path.GetFullPath(directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Journal", "Clipboard"));
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private const string RootMarker = ".journal-clipboard-root";
    private const string RootIdentity = "Journal clipboard staging 1";
    private const string MarkerName = ".journal-folders.json";
    private sealed record Marker(int Version, DateTimeOffset CreatedAt, string[] Names);
    public int MaximumNameLength => Math.Min(OrderFolderPlan.MaximumNameLength, 248 - _root.Length - 36);

    public Task<IReadOnlyList<string>> PrepareAsync(OrderFolderPlan plan, CancellationToken token = default) => Task.Run<IReadOnlyList<string>>(() =>
    {
        if (plan.Items is null || plan.Items.Count is < 1 or > OrderFolderPlan.MaximumFolders || MaximumNameLength < 40 ||
            plan.Items.Any(item => item is null || string.IsNullOrEmpty(item.Name) || item.Name != OrderFolderPlan.SafeName(item.Name, MaximumNameLength)) ||
            plan.Items.Select(item => item.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != plan.Items.Count)
            throw new ArgumentException("Проверьте имена папок.");
        token.ThrowIfCancellationRequested(); Directory.CreateDirectory(_root); RejectLink(_root);
        var identity = Path.Combine(_root, RootMarker);
        if (!File.Exists(identity))
        {
            if (Directory.EnumerateFileSystemEntries(_root).Any()) throw new IOException("Временная папка занята.");
            using var created = new FileStream(identity, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            using var text = new StreamWriter(created); text.Write(RootIdentity);
        }
        RequireOwnedRoot();
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var batch = Path.Combine(_root, "b-" + Guid.NewGuid().ToString("N"));
        if (Directory.Exists(batch) || File.Exists(batch)) throw new IOException("Временная папка уже существует.");
        Directory.CreateDirectory(batch); RejectLink(batch);
        var paths = new List<string>();
        try
        {
            foreach (var item in plan.Items)
            {
                token.ThrowIfCancellationRequested(); var path = Path.GetFullPath(Path.Combine(batch, item.Name));
                if (!string.Equals(Path.GetDirectoryName(path), batch, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    throw new ArgumentException("Проверьте имя папки.");
                Directory.CreateDirectory(path); RejectLink(path); paths.Add(path);
            }
            token.ThrowIfCancellationRequested();
            using var marker = new FileStream(Path.Combine(batch, MarkerName), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            JsonSerializer.Serialize(marker, new Marker(1, _clock.GetUtcNow(), plan.Items.Select(item => item.Name).ToArray()));
            marker.Flush(flushToDisk: true);
            return paths;
        }
        catch
        {
            // Сборка ещё не передана буферу. Удаление только пустых каталогов, без рекурсии.
            foreach (var path in paths) TryDeleteEmpty(path);
            TryDeleteEmpty(batch); throw;
        }
    }, token);

    public Task CleanupExpiredAsync() => Task.Run(() =>
    {
        try
        {
            if (!Directory.Exists(_root)) return; RequireOwnedRoot();
            foreach (var batch in Directory.EnumerateDirectories(_root))
            {
                try
                {
                    if (!Path.GetFileName(batch).StartsWith("b-", StringComparison.Ordinal) || !Guid.TryParseExact(Path.GetFileName(batch)[2..], "N", out _)) continue;
                    RejectLink(batch); var file = Path.Combine(batch, MarkerName);
                    if (!File.Exists(file) || new FileInfo(file).Length > 1024 * 1024) continue;
                    RejectLink(file);
                    using var input = File.OpenRead(file); var marker = JsonSerializer.Deserialize<Marker>(input);
                    if (marker is not { Version: 1, Names: { Length: > 0 and <= OrderFolderPlan.MaximumFolders } } ||
                        marker.CreatedAt > _clock.GetUtcNow().AddDays(-7) || marker.Names.Any(name => string.IsNullOrEmpty(name) || name != OrderFolderPlan.SafeName(name))) continue;
                    var entries = Directory.GetFileSystemEntries(batch);
                    if (entries.Length != marker.Names.Length + 1 || entries.Any(path =>
                        Path.GetFileName(path) != MarkerName && (!marker.Names.Contains(Path.GetFileName(path), StringComparer.Ordinal) ||
                        !Directory.Exists(path) || IsLink(path) || Directory.EnumerateFileSystemEntries(path).Any()))) continue;
                    input.Dispose();
                    foreach (var name in marker.Names) Directory.Delete(Path.Combine(batch, name));
                    File.Delete(file); Directory.Delete(batch);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    });
    private void RequireOwnedRoot()
    {
        RejectLink(_root); var marker = Path.Combine(_root, RootMarker); RejectLink(marker);
        if (new FileInfo(marker).Length > 64 || File.ReadAllText(marker) != RootIdentity) throw new IOException("Временная папка недоступна.");
    }
    private static bool IsLink(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    private static void RejectLink(string path) { if (IsLink(path)) throw new IOException("Временная папка недоступна."); }
    private static void TryDeleteEmpty(string path) { try { RejectLink(path); Directory.Delete(path); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { } }
}

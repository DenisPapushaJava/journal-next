using System.Text;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Jornal.Core.Results;

namespace Jornal.Desktop.Clipboard;

public interface IJournalClipboard
{
    int MaximumFolderNameLength { get; }
    Task WriteTextAsync(string text);
    Task WriteFoldersAsync(OrderFolderPlan plan);
}

public sealed class JournalClipboard(Func<IClipboard?> getClipboard, IStorageProvider storage, TemporaryFolderStore? folders = null) : IJournalClipboard
{
    private readonly TemporaryFolderStore _folders = folders ?? new();
    public int MaximumFolderNameLength => _folders.MaximumNameLength;
    private IClipboard Get() => getClipboard() ?? throw new NotSupportedException("Системный буфер обмена недоступен.");
    public async Task WriteTextAsync(string text)
    { var clipboard = Get(); await clipboard.SetTextAsync(text); await clipboard.FlushAsync(); }
    public async Task WriteFoldersAsync(OrderFolderPlan plan)
    {
        var clipboard = Get(); var paths = await _folders.PrepareAsync(plan);
        var items = new List<IStorageItem>();
        foreach (var path in paths)
        {
            var folder = await storage.TryGetFolderFromPathAsync(new Uri(path));
            if (folder is null || !folder.Path.IsFile) throw new IOException("Папки недоступны для копирования.");
            items.Add(folder);
        }
        await clipboard.SetDataAsync(FolderClipboardPayload.Create(items)); await clipboard.FlushAsync();
        await _folders.CleanupExpiredAsync();
    }
}

public static class FolderClipboardPayload
{
    public static DataFormat<byte[]> CopyFormat { get; } = DataFormat.CreateBytesPlatformFormat(OperatingSystem.IsWindows()
        ? "Preferred DropEffect" : "x-special/gnome-copied-files");
    public static DataTransfer Create(IReadOnlyList<IStorageItem> folders)
    {
        if (folders.Count == 0) throw new ArgumentException("Нет папок для копирования.");
        var data = new DataTransfer();
        for (var index = 0; index < folders.Count; index++)
        {
            var item = new DataTransferItem(); item.SetFile(folders[index]);
            if (index == 0 && (OperatingSystem.IsWindows() || OperatingSystem.IsLinux()))
                item.Set(CopyFormat, OperatingSystem.IsWindows() ? [1, 0, 0, 0] : Encoding.UTF8.GetBytes("copy\n" + string.Join("\n", folders.Select(folder => folder.Path.AbsoluteUri)) + "\n"));
            data.Add(item);
        }
        return data;
    }
}

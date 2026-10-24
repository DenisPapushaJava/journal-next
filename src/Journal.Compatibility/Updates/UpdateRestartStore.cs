using System.Text.Json;

namespace Jornal.Desktop.Updates;

public sealed record UpdateRestartDraft(int Version, string? DatabasePath, string ResultText, int[] ResultOrderIds);
public interface IUpdateRestartStore
{
    void Save(UpdateRestartDraft draft);
    UpdateRestartDraft? Take();
}

public sealed class JsonUpdateRestartStore(string path) : IUpdateRestartStore
{
    public static string DefaultPath => Path.Combine(Path.GetDirectoryName(DataSources.JsonOrderSourceSettingsStore.DefaultPath)!, "update-restart.json");
    public void Save(UpdateRestartDraft draft)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, draft);
                if (stream.Length > 16 * 1024 * 1024) throw new IOException("Слишком большой черновик результата.");
                stream.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public UpdateRestartDraft? Take()
    {
        if (!File.Exists(path)) return null;
        using var stream = File.OpenRead(path);
        if (stream.Length > 16 * 1024 * 1024) throw new InvalidDataException("Слишком большой черновик результата.");
        var draft = JsonSerializer.Deserialize<UpdateRestartDraft>(stream);
        if (draft is not { Version: 1, ResultText: not null, ResultOrderIds: not null })
            throw new InvalidDataException("Неизвестный формат черновика результата.");
        stream.Dispose();
        File.Delete(path);
        return draft;
    }
}

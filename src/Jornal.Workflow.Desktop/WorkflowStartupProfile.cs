using System.Text.Json;
using Jornal.Workflow.Core;

namespace Jornal.Workflow.Desktop;

// Display-only last confirmed profile. It must never become the author of a write.
public sealed record WorkflowStartupProfile(int Schema, string WorkspacePath, Guid? DatabaseId,
    Guid EmployeeId, string Name, uint? ColorArgb)
{
    public bool IsValid => Schema == 1 && Path.IsPathFullyQualified(WorkspacePath) && DatabaseId != Guid.Empty &&
        EmployeeId != Guid.Empty && !string.IsNullOrWhiteSpace(Name) && Name.Length <= 255 && !Name.Any(char.IsControl) &&
        (ColorArgb is null || ColorArgb.Value >> 24 == 255);
    internal bool Matches(WorkflowConnection connection, WorkflowPreferences preferences) => IsValid &&
        EmployeeId == preferences.EmployeeId && EmployeeId == connection.EmployeeId && DatabaseId == preferences.DatabaseId &&
        connection.Matches(preferences) && string.Equals(WorkspacePath, connection.Store.FilePath,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}

public sealed class WorkflowStartupProfileStore(string path)
{
    public const int MaximumBytes = 8 * 1024;
    public WorkflowStartupProfile? Load()
    {
        try
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (input.Length > MaximumBytes) return null;
            var snapshot = JsonSerializer.Deserialize<WorkflowStartupProfile>(input);
            return snapshot?.IsValid == true ? snapshot : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { return null; }
    }
    public void Save(WorkflowStartupProfile snapshot)
    {
        if (!snapshot.IsValid) return;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot);
        if (bytes.Length > MaximumBytes) return;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temporary, bytes); File.Move(temporary, path, overwrite: true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public void Remove(WorkflowStartupProfile expected)
    {
        if (Load() == expected) File.Delete(path);
    }
}

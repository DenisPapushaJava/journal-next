namespace Jornal.Workflow.Infrastructure;

internal static class WorkflowServiceFiles
{
    // Only explicitly known service paths, never all dotfiles or a recursive walk.
    public static void TryHide(string path)
    {
        if (!OperatingSystem.IsWindows()) return; // Linux already hides names beginning with a dot.
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Hidden) == 0)
                File.SetAttributes(path, (attributes & ~FileAttributes.Normal) | FileAttributes.Hidden);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Visibility is cosmetic. A share may allow work but deny attribute changes.
        }
    }
}

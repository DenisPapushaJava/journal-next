using Jornal.Workflow.Core;

namespace Jornal.Workflow.Infrastructure;

public static class WorkflowOfficePilotPath
{
    public const string FileName = "office-pilot.txt";

    public static string? Find(string? root, string nativePath)
    {
        var folder = WorkflowOfficeProjectPaths.InRoot(root, nativePath);
        if (folder is null) return null;
        var file = Path.Combine(folder, FileName);
        try { ShopFileSystem.RequireOrdinaryPath(file); }
        catch (WorkflowValidationException) { return null; }
        return File.Exists(file) ? file : null;
    }
}

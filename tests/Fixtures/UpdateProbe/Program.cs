using System.Text.Json;
using System.Security.Cryptography;
using Jornal.Desktop.Updates;
using Jornal.Workflow.Core;
using Jornal.Workflow.Desktop;
using Jornal.Workflow.Infrastructure;
using Velopack;
using Velopack.Sources;

VelopackApp.Build().SetAutoApplyOnStartup(false).Run();
var root = Environment.GetEnvironmentVariable("JOURNAL_NEXT_UPDATE_PROBE_ROOT") ?? throw new InvalidOperationException("Isolated test root is required.");
try
{
    var updater = new WorkflowAppUpdater(managerFactory: _ => new UpdateManager(new SimpleFileSource(new DirectoryInfo(Path.Combine(root, "releases"))),
        new UpdateOptions { ExplicitChannel = WorkflowIdentity.Channel, AllowVersionDowngrade = false }));
    if (!updater.IsInstalled) throw new InvalidOperationException("Baseline probe is not installed.");
    // Only create fresh synthetic profile data on the disposable runner.
    if (Directory.Exists(WorkflowIdentity.ProfileDirectory)) throw new IOException("Probe must not replace an existing Next profile.");
    Directory.CreateDirectory(WorkflowIdentity.ProfileDirectory);
    var store = new LocalWorkflowWorkspaceStore(WorkflowIdentity.WorkspacePath);
    var synthetic = await store.SaveEmployeeProfileAsync(null, "Тестовый технолог", 0xFF628DB2);
    var actor = synthetic.Employees.Single().Id;
    var created = await store.CreateOrderFromNamesAsync("Тест менеджер", "Тестовый заказчик", actor, DateTimeOffset.UtcNow);
    new WorkflowPreferencesStore(WorkflowIdentity.PreferencesPath).Save(new(true) { EmployeeId = actor, EntrySuggestions = new() { Managers = ["Не потерять список"] } });
    new JsonAppUpdatePreferencesStore(WorkflowIdentity.UpdatePreferencesPath).Save(new(IncludePreview: true, CheckAutomatically: false));
    const string text = "Текст результата Next перед обновлением\n";
    new WorkflowRestartStore(Path.Combine(WorkflowIdentity.ProfileDirectory, "update-restart.json")).Save(new(1, created.OrderId, text)
    { Results = [new(created.OrderId, 1, text, true, false)] });
    var update = await updater.CheckAsync(true, CancellationToken.None) ?? throw new InvalidOperationException("No release candidate found.");
    await updater.DownloadAsync(update, new Progress<int>(), CancellationToken.None);
    string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    File.WriteAllText(Path.Combine(root, "scheduled.json"), JsonSerializer.Serialize(new
    {
        update.Version, profile = WorkflowIdentity.ProfileDirectory, text,
        workspaceHash = Hash(WorkflowIdentity.WorkspacePath), preferencesHash = Hash(WorkflowIdentity.PreferencesPath),
        updatesHash = Hash(WorkflowIdentity.UpdatePreferencesPath)
    }));
    updater.ScheduleRestart(update);
}
catch (Exception exception)
{
    File.WriteAllText(Path.Combine(root, "failed.txt"), exception.ToString());
    Environment.ExitCode = 1;
}

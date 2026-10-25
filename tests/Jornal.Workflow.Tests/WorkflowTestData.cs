using Jornal.Workflow.Core;
using Jornal.Workflow.Infrastructure;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Jornal.Workflow.Tests;

// Synthetic people belong only to isolated tests; production starts with an empty employee catalog.
internal static class WorkflowTestData
{
    public static JsonObject LegacyJson(WorkflowWorkspace workspace, int schema)
    {
        var data = JsonSerializer.SerializeToNode(workspace)!.AsObject(); data["SchemaVersion"] = schema;
        foreach (var order in data["Orders"]!.AsArray())
        {
            var events = new JsonArray();
            if (order!["CreatedById"] is { } creator)
                events.Add(new JsonObject { ["At"] = order["CreatedAt"]!.DeepClone(), ["ActorId"] = creator.DeepClone(), ["Description"] = "Создана заявка" });
            order!["Events"] = events;
            foreach (var specification in order["Specifications"]!.AsArray())
            {
                var state = specification!["Current"]!.DeepClone().AsObject(); state["Id"] = Guid.NewGuid(); state["Number"] = 1;
                specification["Revisions"] = new JsonArray(state); specification.AsObject().Remove("Current");
            }
        }
        return data;
    }

    public static WorkflowWorkspace CreateWorkspace() => WorkflowWorkspace.Empty with
    {
        Employees =
        [
            new(Guid.Parse("10000000-0000-0000-0000-000000000001"), "Иванов") { IsTechnologist = true },
            new(Guid.Parse("10000000-0000-0000-0000-000000000002"), "Петров") { IsTechnologist = true },
            new(Guid.Parse("10000000-0000-0000-0000-000000000003"), "Сидоров") { IsTechnologist = true }
        ]
    };

    public static async Task<LocalWorkflowWorkspaceStore> CreateStoreAsync(string path, CancellationToken token)
    {
        var store = new LocalWorkflowWorkspaceStore(path);
        await store.WriteAsync(0, _ => CreateWorkspace(), token);
        return store;
    }
}

namespace Jornal.Workflow.Infrastructure;

public enum WorkflowShopStage { Discovery, Checking, Signing, Copying, Verifying, Recording, Clipboard }

/// <summary>Observes existing work. Counts are scoped to this stage, never an estimated overall percentage.</summary>
public sealed record WorkflowShopProgress(WorkflowShopStage Stage, string? Project = null, string? File = null,
    int? Completed = null, int? Total = null, string? Activity = null);

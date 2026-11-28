using System.Text.Json.Serialization;
using Jornal.Workflow.Core;

namespace Jornal.Workflow.Infrastructure;

public sealed record WorkflowOfficeCalculationDraft(Guid Id, Guid OrderId, long OrderVersion, Guid Actor,
    WorkflowOfficeCalculationDraftEntry[] Entries)
{
    public string? OrderNumber { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool QueuedForOffice { get; init; }
    public decimal PendingOfficeTotal() => Entries.Sum(entry => CuttingCalculation.Calculate(entry.Parameters, entry.Rate).TotalCost);
    public bool IsSavedIn(WorkflowOrder order) => !order.IsDeleted && order.Id == OrderId && Entries.All(entry =>
        order.ActiveProjects.Any(project => project.Id == entry.ProjectId) &&
        order.Specifications.Any(specification => specification.Id == entry.ProjectId && entry.Matches(specification.Current)));
}

public sealed record WorkflowOfficeCalculationDraftEntry(Guid ProjectId, string Number, DateOnly Date, CuttingParameters Parameters,
    CuttingRate Rate, string? Profile, string? ReportHash, AcceptedReport? Captured)
{
    public bool Matches(SpecificationState state) => state.DocumentNumber == Number.Trim() && state.DocumentDate == Date &&
        Parameters.SameInputs(state.Parameters) && state.Calculation == CuttingCalculation.Calculate(Parameters, Rate) &&
        state.ImportProfile == Profile && state.Report?.Sha256 == ReportHash &&
        (Captured is null || string.Equals(state.Report?.FileName, Captured.FileName, StringComparison.OrdinalIgnoreCase));
}

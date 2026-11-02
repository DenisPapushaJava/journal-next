using System.Globalization;

namespace Jornal.Workflow.Core;

// One comparison baseline for the last confirmed handoff, not a history of edits.
public sealed record WorkflowProjectHandoffInputs(Guid ManagerId, Guid CustomerId, string? Blcb,
    Guid MachineId, decimal ThicknessMm, string ProjectName, string? ImportProfile,
    Guid CalculationMachineId, decimal CalculationThicknessMm, string Material, string BlankSize,
    decimal BlankQuantity, bool HasBending, bool HasChamfer, string? ReportHash)
{
    public static WorkflowProjectHandoffInputs Capture(WorkflowOrder order, Guid projectId)
    {
        var project = order.Projects.Single(p => p.Id == projectId);
        var saved = order.Specifications.Single(s => s.Id == projectId).Current;
        var p = saved.Parameters;
        return new(order.ManagerId, order.CustomerId, order.Blcb?.Trim(), project.MachineId, project.ThicknessMm,
            project.Name, project.ImportProfile, p.MachineId, p.ThicknessMm, p.Material, p.BlankSize,
            p.BlankQuantity, p.HasBending, p.HasChamfer, saved.Report?.Sha256.ToUpperInvariant());
    }

    public string[] Changes(WorkflowProjectHandoffInputs current)
    {
        var result = new List<string>();
        void Field(string label, string? before, string? after)
        { if (before != after) result.Add(label + ": " + Show(before) + " → " + Show(after) + "."); }
        Field("БЛЦБ", Blcb, current.Blcb);
        if (ManagerId != current.ManagerId) result.Add("Изменён менеджер.");
        if (CustomerId != current.CustomerId) result.Add("Изменён заказчик.");
        if (MachineId != current.MachineId || CalculationMachineId != current.CalculationMachineId) result.Add("Изменён станок.");
        if (ThicknessMm != current.ThicknessMm || CalculationThicknessMm != current.CalculationThicknessMm)
            result.Add("Толщина: " + N(ThicknessMm != current.ThicknessMm ? ThicknessMm : CalculationThicknessMm) +
                " → " + N(ThicknessMm != current.ThicknessMm ? current.ThicknessMm : current.CalculationThicknessMm) + " мм.");
        if (ProjectName != current.ProjectName) result.Add("Изменено имя проекта.");
        if (ImportProfile != current.ImportProfile) result.Add("Изменён формат спецификации.");
        Field("Материал", Material, current.Material);
        Field("Заготовка", BlankSize, current.BlankSize);
        if (BlankQuantity != current.BlankQuantity) Field("Количество заготовок", N(BlankQuantity), N(current.BlankQuantity));
        if (HasChamfer != current.HasChamfer) result.Add(current.HasChamfer ? "Добавлена фаска." : "Снята отметка фаски.");
        if (HasBending != current.HasBending) result.Add(current.HasBending ? "Добавлена гибка." : "Снята отметка гибки.");
        if (!string.Equals(ReportHash, current.ReportHash, StringComparison.OrdinalIgnoreCase)) result.Add("Обновлена спецификация.");
        return result.ToArray();
    }

    public void Validate()
    {
        if (ManagerId == Guid.Empty || CustomerId == Guid.Empty || MachineId == Guid.Empty || CalculationMachineId == Guid.Empty ||
            ThicknessMm <= 0 || CalculationThicknessMm <= 0 || BlankQuantity <= 0 || string.IsNullOrWhiteSpace(ProjectName) ||
            Material is null || BlankSize is null || ReportHash is { } hash && (hash.Length != 64 || hash.Any(c => !Uri.IsHexDigit(c))))
            throw new WorkflowValidationException("Повреждены данные сравнения переданного проекта.");
    }
    private static string Show(string? value) => string.IsNullOrWhiteSpace(value) ? "не указан" : value;
    private static string N(decimal value) => value.ToString("0.############################", CultureInfo.GetCultureInfo("ru-RU"));
}

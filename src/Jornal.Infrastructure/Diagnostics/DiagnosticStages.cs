namespace Jornal.Infrastructure.Diagnostics;

// Никаких имён файлов, SQL или значений полей в названии этапа.
public enum DiagnosticStage
{
    Unknown, LaunchAccessHost, SendAccessRequest, ReadAccessResponse, ValidateAccessResponse,
    ValidateAccessRequest, OpenDatabase, BeginTransaction, ReadOrders, ReadPrices, CalculatePrice,
    CheckSnapshot, WriteRecord, ReadBack, CommitTransaction, RollbackTransaction, ReleaseResources,
    PrepareCuttingTime, ReadSpecification, ParseSpecification, NormalizeSpecification,
    CheckUpdates, DownloadUpdate, ApplyUpdate
}

public static class DiagnosticStages
{
    private static readonly object StageKey = new();

    // Отметка привязана к исключению: параллельные операции не делят «текущий этап».
    public static DiagnosticStage Of(Exception exception)
    {
        try
        {
            for (Exception? current = exception; current is not null; current = current.InnerException)
                if (current.Data[StageKey] is DiagnosticStage stage && Enum.IsDefined(stage)) return stage;
        }
        catch (Exception) { }
        return DiagnosticStage.Unknown;
    }

    public static void Tag(Exception exception, DiagnosticStage stage)
    {
        try { if (Enum.IsDefined(stage) && stage != DiagnosticStage.Unknown && Of(exception) == DiagnosticStage.Unknown) exception.Data[StageKey] = stage; }
        catch (Exception) { }
    }

    public static T Run<T>(DiagnosticStage stage, Func<T> action)
    {
        try { return action(); }
        catch (Exception exception) { Tag(exception, stage); throw; }
    }

    public static void Run(DiagnosticStage stage, Action action) => Run(stage, () => { action(); return true; });

    public static async Task<T> RunAsync<T>(DiagnosticStage stage, Func<Task<T>> action)
    {
        try { return await action(); }
        catch (Exception exception) { Tag(exception, stage); throw; }
    }

    public static async Task RunAsync(DiagnosticStage stage, Func<Task> action)
    {
        try { await action(); }
        catch (Exception exception) { Tag(exception, stage); throw; }
    }
}

namespace Jornal.Workflow.Infrastructure;

// Cursor and counters belong to one filter. Retained open cards do not change that filter.
internal sealed record WorkflowPageState(WorkflowReadQuery Filter)
{
    public (DateTimeOffset Time, long Number)? Cursor { get; init; }
    public bool HasMore { get; init; }
    public int TotalCount { get; init; }
    public bool Matches(WorkflowReadQuery query) => Filter.From == query.From && Filter.Until == query.Until &&
        Filter.Search == query.Search && Filter.Limit == query.Limit;

    public WorkflowPageState Accept(WorkflowAccessResponse response)
    {
        var ids = response.PageIds ?? throw new InvalidDataException("Не передана страница журнала MDB.");
        if (response.Orders is null) throw new InvalidDataException("Не переданы заявки страницы MDB.");
        var pageIds = ids.ToHashSet();
        var rows = response.Orders!.Where(o => pageIds.Contains(o.Id)).ToArray();
        if (pageIds.Count != ids.Length || rows.Length != ids.Length || response.HasMore && ids.Length == 0 || response.TotalCount < 0)
            throw new InvalidDataException("Некорректная страница журнала MDB.");
        var last = rows.OrderByDescending(o => o.UpdatedAt).ThenByDescending(o => o.Number).LastOrDefault();
        return this with { Cursor = last is null ? null : (last.UpdatedAt, last.Number), HasMore = response.HasMore, TotalCount = response.TotalCount };
    }
}

using Jornal.Core.Pricing;

namespace Jornal.Desktop.ViewModels;

// Снимок только для открытой формы/пакета. Явное обновление и закрытие прайсов сбрасывают его.
internal sealed class ThicknessPriceSource(Func<IPriceReader?> reader)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Task<PriceTable>> _tables = [];
    private readonly CancellationTokenSource _lifetime = new();
    public bool IsAvailable => reader() is not null;
    internal bool TryGet(string name, out PriceTable? table)
    {
        lock (_gate)
        {
            if (_tables.TryGetValue(name, out var task) && task.IsCompletedSuccessfully)
            { table = task.GetAwaiter().GetResult(); return true; }
        }
        table = null; return false;
    }
    public Task<PriceTable> ReadAsync(string name, CancellationToken cancellationToken)
    {
        Task<PriceTable> task;
        lock (_gate)
        {
            if (!_tables.TryGetValue(name, out task!))
            {
                task = ReadCoreAsync(name);
                _tables[name] = task;
                // Отмена ожидания одной карточки не отменяет запрос остальных и не оставляет ненаблюдённый отказ.
                _ = task.ContinueWith(failed => _ = failed.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        return task.WaitAsync(cancellationToken);
    }
    private async Task<PriceTable> ReadCoreAsync(string name) =>
        await (reader() ?? throw new PriceReadException("Прайсы недоступны.")).ReadAsync(name, _lifetime.Token);
    public void Invalidate() { lock (_gate) _tables.Clear(); }
    public void Stop() => _lifetime.Cancel();
}

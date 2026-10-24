using System.ComponentModel;
using System.Diagnostics;

namespace Jornal.Infrastructure.Access;

public sealed partial class AccessHostClient
{
    private async Task CleanupAsync(Process process, CancellationTokenSource requestCancellation,
        Stream? inputPipe, Stream? outputPipe, Stream? errorPipe, Task? input, Task? output, Task? error, Task? exit)
    {
        using var cleanup = new CancellationTokenSource(_cleanupTimeout);
        // CancelAsync может ждать callback на заполненном pipe: сначала запрашиваем отмену и остановку,
        // затем ждём. Хост работает в одном процессе, без дочерних исполнителей/обхода дерева Windows.
        var cancelling = requestCancellation.CancelAsync();
        var stopped = StopAndWaitAsync(process, cleanup.Token);
        // Process.Dispose не закрывает уже полученные клиентом StandardInput/Output/Error.
        // Освобождаем сами pipe без повторного Flush буфера StreamWriter после неудачной отправки.
        var settled = Task.WhenAll(ObserveAsync(cancelling), ObserveAsync(stopped),
            ObserveAsync(input), ObserveAsync(output), ObserveAsync(error), ObserveAsync(exit),
            DisposePipeAsync(inputPipe), DisposePipeAsync(outputPipe), DisposePipeAsync(errorPipe));
        try { await settled.WaitAsync(cleanup.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cleanup.IsCancellationRequested)
        {
            // ОС может отказать в остановке. Не подменяем исходную ошибку и не ждём бесконечно.
            // У каждой начатой задачи остаётся наблюдатель исключения, даже если она завершится позднее.
        }
    }

    private async Task StopAndWaitAsync(Process process, CancellationToken cleanupToken)
    {
        try
        {
            if (process.HasExited) return;
            try { _stop(process); }
            catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
            { /* Процесс мог выйти между проверкой и Kill; выход проверяется независимо от запроса. */ }
            await process.WaitForExitAsync(cleanupToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or OperationCanceledException)
        { /* Не запущен, уже завершён, недоступен либо истёк отдельный предел очистки. */ }
    }

    private static async Task ObserveAsync(Task? task)
    {
        if (task is null) return;
        try { await task.ConfigureAwait(false); } catch (Exception) { }
    }

    private static async Task DisposePipeAsync(Stream? pipe)
    {
        if (pipe is null) return;
        try { await pipe.DisposeAsync().ConfigureAwait(false); } catch (Exception) { }
    }
}

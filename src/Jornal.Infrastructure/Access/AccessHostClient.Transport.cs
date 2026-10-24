using System.Diagnostics;
using System.Text.Json;
using Jornal.Infrastructure.Diagnostics;

namespace Jornal.Infrastructure.Access;

public sealed partial class AccessHostClient
{
    internal static async Task<AccessHostResponse?> ReadResponseAsync(Stream source, CancellationToken cancellationToken)
    {
        try { return await JsonSerializer.DeserializeAsync<AccessHostResponse>(source, cancellationToken: cancellationToken).ConfigureAwait(false); }
        catch (JsonException exception)
        {
            DiagnosticStages.Tag(exception, DiagnosticStage.ValidateAccessResponse);
            throw;
        }
    }

    private static async Task WriteRequestAsync(Process process, AccessHostRequest request, CancellationToken cancellationToken)
    {
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), cancellationToken).ConfigureAwait(false);
        process.StandardInput.Close();
    }

    private static async Task AwaitWithReadersAsync(Task operation, Task output, Task error)
    {
        // WhenAll ждёт все задачи даже при ошибке парсера. Хост тогда может навсегда
        // заполнить stdout; ошибка любого читателя должна сразу перейти к общему finally.
        var pending = new List<Task>(3) { operation, output, error };
        while (!operation.IsCompleted)
        {
            var completed = await Task.WhenAny(pending).ConfigureAwait(false);
            if (operation.IsCompleted) break;
            await completed.ConfigureAwait(false);
            pending.Remove(completed);
        }
        await operation.ConfigureAwait(false);
    }
}

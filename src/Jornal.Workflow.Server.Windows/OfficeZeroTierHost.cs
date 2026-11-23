using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text.Json;
using Jornal.Workflow.Infrastructure;

namespace Jornal.Workflow.Server.Windows;

internal sealed record OfficeZeroTierListener(string Address, string InterfaceAlias, int PrefixLength)
{
    public static OfficeZeroTierListener Capture(ZeroTierBinding binding) =>
        new(binding.Address.ToString(), binding.InterfaceAlias, binding.PrefixLength);

    public Uri Url(int port) => new UriBuilder("http", Address, port).Uri;
}

internal static class OfficeZeroTierHost
{
    public static async Task RunAsync(OfficeSettings settings, OfficeWorkAccess? workAccess, CancellationToken cancellationToken)
    {
        if (settings.ZeroTierNetworkId is not { } id || !ZeroTierClient.IsNetworkId(id) || settings.ZeroTierListener is not { } listener)
            return;

        var client = new ZeroTierClient();
        CancellationTokenSource? serving = null;
        Task<int>? remote = null;
        long? lastAttempt = null;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        try
        {
            do
            {
                var binding = await ReadBindingAsync(client, id, cancellationToken);
                if (binding is null || OfficeZeroTierListener.Capture(binding) != listener)
                {
                    await StopAsync();
                    lastAttempt = null;
                    continue;
                }
                if (remote?.IsCompleted == true) await StopAsync();
                if (remote is null && (lastAttempt is null || Stopwatch.GetElapsedTime(lastAttempt.Value) >= TimeSpan.FromSeconds(15)))
                {
                    lastAttempt = Stopwatch.GetTimestamp();
                    serving = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    remote = OfficeServerApplication.RunAsync([], serving.Token, binding.Address, workAccess);
                }
            } while (await timer.WaitForNextTickAsync(cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally { await StopAsync(); }

        async Task StopAsync()
        {
            if (remote is null || serving is null) return;
            await serving.CancelAsync();
            try { await remote; }
            catch (OperationCanceledException) when (serving.IsCancellationRequested) { }
            catch (Exception exception) { Console.Error.WriteLine("Не удалось открыть порт в ZeroTier. " + exception.Message); }
            finally
            {
                serving.Dispose();
                serving = null;
                remote = null;
            }
        }
    }

    private static async Task<ZeroTierBinding?> ReadBindingAsync(ZeroTierClient client, string id, CancellationToken token)
    {
        try { return (await client.ReadAsync(id, token)).Binding; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or HttpRequestException or
            InvalidDataException or JsonException or NetworkInformationException) { return null; }
    }
}

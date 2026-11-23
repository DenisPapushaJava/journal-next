using Jornal.Workflow.Infrastructure;

namespace Jornal.Workflow.Desktop;

public sealed record WorkflowOfficeSetupDraft(string Address, string? NetworkId)
{
    public string? LinuxInterfaceName { get; init; }
}

internal enum WorkflowOfficeSetupStep
{
    Ready, Install, NetworkId, EnableManagedAddresses, WaitingService, WaitingApproval, WaitingConfiguration, WaitingRoute, CheckingServer, Error
}

internal sealed record WorkflowOfficeSetupState(WorkflowOfficeSetupStep Step, string? Message = null, string? NodeId = null,
    WorkflowOfficeRoute? Route = null);

internal sealed class WorkflowOfficeSetup(
    Func<Uri, WorkflowOfficeRoute?, CancellationToken, Task<WorkflowOfficeProbe>> checkHealth,
    Func<string, CancellationToken, Task<ZeroTierNetwork>> readNetwork,
    Func<string, CancellationToken, Task<bool>> requestJoin,
    Func<CancellationToken, Task<string>> readNodeId,
    TimeSpan retryDelay,
    int maxAttempts = 30,
    Func<string, CancellationToken, Task>? enableManagedAddresses = null,
    Func<string, CancellationToken, Task<WorkflowOfficeRoute?>>? findLinuxRoute = null)
{
    internal WorkflowOfficeSetup() : this(new ZeroTierClient()) { }

    private WorkflowOfficeSetup(ZeroTierClient client) : this(new WorkflowOfficeTransport().CheckHealthAsync,
        client.ReadAsync, client.RequestRestrictedJoinAsync, client.ReadNodeIdAsync, TimeSpan.FromSeconds(2),
        enableManagedAddresses: client.EnableManagedAddressesAsync,
        findLinuxRoute: WorkflowOfficeLinuxNetwork.FindRouteAsync) { }

    internal async Task<WorkflowOfficeSetupState> PrepareAsync(Uri server, string? networkId, bool windows, bool installed,
        Action<WorkflowOfficeSetupState> show, CancellationToken token, bool confirmManagedAddresses = false,
        WorkflowOfficeRoute? expectedRoute = null)
    {
        if (string.IsNullOrEmpty(networkId))
        {
            if (expectedRoute is null)
                return new(windows && !installed ? WorkflowOfficeSetupStep.Install : WorkflowOfficeSetupStep.NetworkId);

            return await CheckConfirmedRouteAsync(server, expectedRoute, windows && !installed, token);
        }
        if (!ZeroTierClient.IsNetworkId(networkId))
            return new(WorkflowOfficeSetupStep.NetworkId, "ID сети состоит из 16 шестнадцатеричных символов.");
        if (!windows)
        {
            if (findLinuxRoute is not null && await findLinuxRoute(networkId, token) is { } linuxRoute)
                return await CheckConfirmedRouteAsync(server, linuxRoute, false, token);
            return new(WorkflowOfficeSetupStep.NetworkId,
                "Сверьте ID в панели сервера. Подключите этот компьютер к сети в ZeroTier и подтвердите устройство в ZeroTier Central.");
        }
        if (!installed) return new(WorkflowOfficeSetupStep.Install);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(95));
        var attemptToken = deadline.Token;
        var route = new WorkflowOfficeSetupState(WorkflowOfficeSetupStep.WaitingRoute, "Настраиваем сетевое подключение…");
        var state = route;
        var unavailable = 0;
        var serviceFailures = 0;
        var joinRequested = false;
        var joinedHere = false;
        var managedRequested = false;
        string? nodeId = null;
        var nodeReads = 0;
        show(route);
        try
        {
            for (var attempt = 0; attempt < maxAttempts; attempt++)
            {
                ZeroTierNetwork network;
                try
                {
                    network = await readNetwork(networkId, attemptToken);
                    if (!joinRequested && network.Status == "NOT_JOINED")
                    {
                        joinedHere = await requestJoin(networkId, attemptToken);
                        joinRequested = true;
                    }
                    if (network.ManagedAddressesDisabled && !managedRequested)
                    {
                        if (!(joinedHere || confirmManagedAddresses) || enableManagedAddresses is null)
                            return new(WorkflowOfficeSetupStep.EnableManagedAddresses,
                                "Офисная сеть проверена. Разрешите получение адресов для этой сети ZeroTier.");
                        await enableManagedAddresses(networkId, attemptToken);
                        managedRequested = true;
                    }
                    serviceFailures = 0;
                }
                catch (ZeroTierServiceUnavailableException ex)
                {
                    if (++serviceFailures == 3 || attempt + 1 == maxAttempts) return new(WorkflowOfficeSetupStep.Error, ex.Message);
                    state = new(WorkflowOfficeSetupStep.WaitingService, "Ждём запуска службы ZeroTier…");
                    show(state);
                    await Task.Delay(retryDelay, attemptToken);
                    continue;
                }
                if (network.Binding is { } binding)
                {
                    var expected = new WorkflowOfficeRoute(binding.Address, binding.InterfaceAlias);
                    var probe = await checkHealth(server, expected, attemptToken);
                    var health = probe.Health;
                    if (health == WorkflowOfficeHealth.Reachable && probe.Route is { } observed && expected.Matches(observed))
                        return new(WorkflowOfficeSetupStep.Ready, Route: observed);
                    if (health is WorkflowOfficeHealth.UnexpectedResponse) return ServerError(health);
                    if (health is WorkflowOfficeHealth.Unavailable)
                    {
                        if (++unavailable == 3) return ServerError(health);
                        state = new(WorkflowOfficeSetupStep.CheckingServer, "Сеть подключена. Проверяем сервер…");
                    }
                    else { unavailable = 0; state = route; }
                }
                else if (network.Status == "ACCESS_DENIED")
                {
                    unavailable = 0;
                    if (nodeId is null && nodeReads < 3)
                    {
                        nodeReads++;
                        try { nodeId = await readNodeId(attemptToken); }
                        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or HttpRequestException or System.Text.Json.JsonException) { }
                    }
                    state = new(WorkflowOfficeSetupStep.WaitingApproval, "Ожидаем подтверждения администратора в ZeroTier Central.", nodeId);
                }
                else
                {
                    unavailable = 0;
                    state = network.Status switch
                    {
                        "REQUESTING_CONFIGURATION" or "NOT_JOINED" => new(WorkflowOfficeSetupStep.WaitingConfiguration, "Получаем настройки офисной сети ZeroTier…"),
                        "ADDRESS_PENDING" => route,
                        _ => new(WorkflowOfficeSetupStep.Error, network.Description)
                    };
                    if (state.Step == WorkflowOfficeSetupStep.Error) return state;
                }

                show(state);
                if (attempt + 1 < maxAttempts) await Task.Delay(retryDelay, attemptToken);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && deadline.IsCancellationRequested) { }
        return state.Step switch
        {
            WorkflowOfficeSetupStep.WaitingApproval => state,
            WorkflowOfficeSetupStep.WaitingService => new(WorkflowOfficeSetupStep.Error,
                "Служба ZeroTier недоступна. Откройте ZeroTier One и повторите подключение."),
            WorkflowOfficeSetupStep.WaitingConfiguration => new(WorkflowOfficeSetupStep.WaitingConfiguration,
                "ZeroTier ещё получает настройки сети. Проверьте интернет и повторите проверку."),
            WorkflowOfficeSetupStep.CheckingServer => ServerError(WorkflowOfficeHealth.Unavailable),
            _ => new(WorkflowOfficeSetupStep.WaitingRoute, "Windows пока не настроила маршрут через ZeroTier. Повторите проверку.")
        };
    }

    private async Task<WorkflowOfficeSetupState> CheckConfirmedRouteAsync(Uri server, WorkflowOfficeRoute expected,
        bool canInstall, CancellationToken token)
    {
        var probe = await checkHealth(server, expected, token);
        return probe switch
        {
            { Health: WorkflowOfficeHealth.Reachable, Route: { } route } when expected.Matches(route) =>
                new(WorkflowOfficeSetupStep.Ready, Route: route),
            _ when canInstall => new(WorkflowOfficeSetupStep.Install),
            { Health: WorkflowOfficeHealth.RouteMissing or WorkflowOfficeHealth.Reachable } => new(WorkflowOfficeSetupStep.NetworkId,
                "Маршрут к серверу не проходит через указанную сеть. Сверьте адрес и ID сети в панели «Сервер заявок»."),
            _ => ServerError(probe.Health)
        };
    }

    private static WorkflowOfficeSetupState ServerError(WorkflowOfficeHealth health) => new(WorkflowOfficeSetupStep.Error,
        health == WorkflowOfficeHealth.UnexpectedResponse ? "По этому адресу получен неожиданный ответ. Сверьте адрес с панелью «Сервер заявок»." :
        "Сервер не отвечает. Проверьте адрес и убедитесь, что на офисном ПК запущен «Сервер заявок».");
}

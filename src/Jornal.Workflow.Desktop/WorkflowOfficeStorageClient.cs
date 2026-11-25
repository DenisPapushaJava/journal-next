using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Jornal.Workflow.Core;
using Jornal.Workflow.Infrastructure;

namespace Jornal.Workflow.Desktop;

public sealed record WorkflowOfficeStorageSettings(string Address, Guid DatabaseId, Guid CredentialId, bool CanWrite)
{
    public string? ZeroTierInterfaceId { get; init; }
    internal string CredentialPath(string directory) => Path.Combine(directory, $"office-storage-{CredentialId:N}.json");
}

internal sealed partial class WorkflowOfficeStorageClient(WorkflowOfficeStorageSettings settings, string token, string profileDirectory,
    HttpClient http, HttpClient? fileHttp = null) : IWorkflowAccessTransport
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HttpClient StorageTransport = WorkflowOfficeTransport.CreateClient(TimeSpan.FromSeconds(55));
    private readonly Uri _server = WorkflowOfficeTransport.RequireServer(settings.Address);
    private sealed record ConnectionResult(Guid DatabaseId, string Token, bool CanWrite);
    private sealed record ServerError(string? Error, bool Conflict, bool OutcomeUnknown, bool ProjectPreparationRejected);
    internal WorkflowOfficeStorageSettings Settings => settings;
    internal WorkflowMdbStore CreateStore() =>
        new(Path.Combine(profileDirectory, $"office-{settings.DatabaseId:N}.mdb"), this, settings.DatabaseId);

    internal static async Task<WorkflowOfficeStorageClient> ConnectAsync(string? address, string? key,
        string profileDirectory, WorkflowOfficeRoute route, CancellationToken cancellation, HttpClient? connectionClient = null)
    {
        var server = WorkflowOfficeTransport.RequireServer(address);
        var interfaceId = RequireInterface(route.InterfaceId);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(server, "api/v1/storage-connect"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", WorkflowOfficeTransport.RequireAdminKey(key));
        WorkflowOfficeTransport.UseRoute(request, route);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            using var response = await SendAsync(connectionClient ?? WorkflowOfficeTransport.ConnectionClient, request, deadline.Token);
            await CheckAsync(response, deadline.Token, connecting: true);
            var result = await ReadAsync<ConnectionResult>(response, deadline.Token);
            if (result.DatabaseId == Guid.Empty || !WorkflowOfficeConnection.IsStorageToken(result.Token))
                throw new InvalidDataException();
            return new(new(server.AbsoluteUri, result.DatabaseId, Guid.NewGuid(), result.CanWrite)
                { ZeroTierInterfaceId = interfaceId }, result.Token, profileDirectory, StorageTransport);
        }
        catch (HttpRequestException ex) when (ex.InnerException is WorkflowOfficeConnectException connection)
        { throw new WorkflowValidationException(connection.Message); }
        catch (Exception ex) when (ex is HttpRequestException || ex is OperationCanceledException && !cancellation.IsCancellationRequested)
        { throw new WorkflowValidationException("Сервер не ответил при подключении. Проверьте соединение и повторите попытку."); }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or NotSupportedException)
        { throw new WorkflowValidationException("Сервер вернул неполные данные подключения. Сверьте адрес и проверьте версию сервера."); }
    }

    internal static WorkflowOfficeStorageClient Open(WorkflowOfficeStorageSettings settings, string directory,
        WorkflowOfficeRoute? route = null, HttpClient? storageClient = null)
    {
        var interfaceId = RequireInterface(settings.ZeroTierInterfaceId ?? route?.InterfaceId);
        if (route is not null && route.InterfaceId != interfaceId)
            throw new WorkflowValidationException("Подключение сохранено для другой сети ZeroTier. Выберите прежнюю сеть или введите ключ администратора заново.");
        var key = WorkflowOfficeConnection.Load(settings.CredentialPath(directory));
        if (key is null || key.Address != settings.Address || !WorkflowOfficeConnection.IsStorageToken(key.Token))
            throw new WorkflowValidationException("Ключ удалённого подключения недоступен. Подключитесь к серверу заново.");
        return new(settings with { ZeroTierInterfaceId = interfaceId }, key.Token, directory, storageClient ?? StorageTransport);
    }

    private static string RequireInterface(string? interfaceId) => !string.IsNullOrWhiteSpace(interfaceId) ? interfaceId :
        throw new WorkflowValidationException("Подтвердите сеть ZeroTier через «Настройки → Хранилище → Настроить подключение…».");

    internal void SaveCredential() => new WorkflowOfficeConnection(settings.Address, token).Save(settings.CredentialPath(profileDirectory));

    public Task<WorkflowAccessResponse> SendAsync(WorkflowAccessRequest request, CancellationToken cancellation = default) =>
        SendStorageAsync(request, cancellation);

    private async Task<WorkflowAccessResponse> SendStorageAsync(WorkflowAccessRequest request, CancellationToken cancellation, bool workingFiles = false)
    {
        var write = request.Operation is WorkflowAccessOperation.Apply or WorkflowAccessOperation.RecordShopHandoff or
            WorkflowAccessOperation.RecordProjectHandoffs or WorkflowAccessOperation.ClearShopHandoff;
        cancellation.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(55));
        using var message = Request(HttpMethod.Post, "api/v1/storage/database");
        if ((request.ShopHandoff?.OrderId ?? request.ProjectHandoffs?.OrderId) is { } orderId)
            AddShopLease(message, orderId, required: true);
        if (write && request.Patch is { } patch) request = request with { Patch = patch with { SupportsPlannedBending = true } };
        message.Content = JsonContent.Create(request with { Path = "", DatabaseId = settings.DatabaseId }, options: JsonOptions);
        try
        {
            using var response = await SendAsync(http, message, deadline.Token);
            if (workingFiles) await CheckWorkingFileResponseAsync(response, deadline.Token);
            else await CheckAsync(response, deadline.Token);
            var data = await ReadAsync<WorkflowAccessResponse>(response, deadline.Token);
            if (data.Protocol != 1 || data.DatabaseId != settings.DatabaseId || data.Orders is null)
                throw new InvalidDataException("Получен ответ другой MDB.");
            return data;
        }
        // A reconnect can fail after a previous stream was already handed to HTTP.
        catch (HttpRequestException ex) when (ex.InnerException is WorkflowOfficeConnectException connection &&
            !message.Options.TryGetValue(WorkflowOfficeTransport.ConnectedRoute, out _))
        {
            cancellation.ThrowIfCancellationRequested();
            if (workingFiles || !write) throw new WorkflowOfficeConnectionLostException(connection.Message, ex);
            throw new WorkflowValidationException(connection.Message);
        }
        catch (Exception ex) when (write && ex is HttpRequestException or IOException or JsonException or InvalidDataException or OperationCanceledException)
        { throw new WorkflowOutcomeUnknownException(ex); }
        catch (OperationCanceledException ex) when (!cancellation.IsCancellationRequested)
        { throw new WorkflowOfficeConnectionLostException("Офисный сервер не ответил вовремя. Проверьте связь с офисом.", ex); }
        catch (HttpRequestException ex)
        {
            cancellation.ThrowIfCancellationRequested();
            throw new WorkflowOfficeConnectionLostException("Офисный сервер недоступен. Проверьте подключение ZeroTier.", ex);
        }
    }

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, new Uri(_server, path))
            { Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) } };
        if (settings.ZeroTierInterfaceId is { } interfaceId)
            WorkflowOfficeTransport.UseRoute(request, new(null, null, interfaceId));
        return request;
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, HttpRequestMessage request, CancellationToken cancellation) =>
        client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellation)
    {
        await response.Content.LoadIntoBufferAsync(32 * 1024 * 1024, cancellation);
        return await response.Content.ReadFromJsonAsync<T>(cancellation) ?? throw new InvalidDataException("Сервер вернул пустой ответ.");
    }

    private static async Task CheckAsync(HttpResponseMessage response, CancellationToken cancellation, bool connecting = false)
    {
        if (response.IsSuccessStatusCode) return;
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new WorkflowValidationException(connecting ? "Сервер не принял ключ администратора." :
                "Ключ подключения истёк. Подключитесь к серверу заново в настройках.");
        if (response.StatusCode == HttpStatusCode.NotFound && connecting)
            throw new WorkflowValidationException("Обновите «Сервер заявок» до версии с удалённым хранилищем.");
        ServerError? problem = null;
        try
        {
            await response.Content.LoadIntoBufferAsync(4096, cancellation);
            problem = await response.Content.ReadFromJsonAsync<ServerError>(cancellation);
        }
        catch (Exception ex) when (ex is JsonException or HttpRequestException) { }
        if (problem?.OutcomeUnknown == true) throw new WorkflowOutcomeUnknownException();
        if (problem?.Conflict == true) throw new WorkflowConflictException();
        var message = problem?.Error switch
        {
            "remote-writes-disabled" => "Удалённое сохранение выключено в панели сервера.",
            "calculate-working-report" => "Этот файл связан с расчётом. Нажмите «Рассчитать», чтобы сохранить новую спецификацию.",
            "file-version-conflict" => "Файл изменился на сервере. Локальная копия сохранена.",
            "previous-version-needs-review" or "file-write-unconfirmed" => "Сервер не подтвердил замену файла. Проверьте обе версии.",
            "project-folder-unavailable" => "Рабочая папка проекта недоступна на сервере.",
            "operation-rejected" => "Сервер не смог обработать файлы проекта. Проверьте папку проекта или журнал сервера.",
            "operation-unavailable" => "Файлы проекта временно недоступны на сервере. Проверьте подключение и повторите действие.",
            { } detail => detail,
            _ => $"Не удалось обратиться к серверу (HTTP {(int)response.StatusCode})."
        };
        if (problem?.ProjectPreparationRejected == true)
            throw new WorkflowOfficeProjectNotStartedException(message);
        throw new WorkflowValidationException(message);
    }
}

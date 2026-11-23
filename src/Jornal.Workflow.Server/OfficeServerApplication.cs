using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Jornal.Workflow.Core;
using Jornal.Workflow.Infrastructure;

namespace Jornal.Workflow.Server;

public static class OfficeServerApplication
{
    private static readonly OfficeDiagnostics Diagnostics = new();

    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default, IPAddress? bindAddress = null,
        OfficeWorkAccess? workAccess = null, IWorkflowAccessTransport? accessTransport = null)
    {
        const string tokenVariable = "JOURNAL_OFFICE_SERVER_TOKEN";
        var token = Environment.GetEnvironmentVariable(tokenVariable);
        if (token is not { Length: 64 } || !token.All(Uri.IsHexDigit))
        {
            Console.Error.WriteLine($"Задайте {tokenVariable}: 64 шестнадцатеричных символа случайного ключа.");
            return 1;
        }
        var tokenBytes = Encoding.UTF8.GetBytes(token);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = Environments.Production
        });
        var settings = builder.Configuration.GetSection("OfficeServer");
        if (!int.TryParse(settings["Port"] ?? "5197", out var port) || port is < 1 or > 65535)
        {
            Console.Error.WriteLine("Укажите порт сервера от 1 до 65535 (OfficeServer:Port).");
            return 1;
        }
        string? databasePath = null;
        try
        {
            if (settings["DatabasePath"] is { } path && !string.IsNullOrWhiteSpace(path))
                databasePath = WorkflowMdbStore.NormalizePath(path);
        }
        catch (Exception exception) when (exception is WorkflowValidationException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            Console.Error.WriteLine("Укажите полный путь к базе MDB (OfficeServer:DatabasePath).");
            return 1;
        }
        var projectsRoot = settings["ProjectsRoot"];
        if (!string.IsNullOrWhiteSpace(projectsRoot) && !Path.IsPathFullyQualified(projectsRoot))
        {
            Console.Error.WriteLine("Укажите полный путь к рабочей папке проектов (OfficeServer:ProjectsRoot).");
            return 1;
        }
        var pilotHash = settings["OrderPilotSha256"] is { Length: 64 } hash && hash.All(Uri.IsHexDigit)
            ? Convert.FromHexString(hash) : null;
        var filePilotEnabled = bool.TryParse(settings["EnableFilePilot"], out var enableFilePilot) && enableFilePilot;

        builder.WebHost.PreferHostingUrls(false);
        builder.WebHost.ConfigureKestrel(server =>
        {
            // Do not inherit externally configured endpoints in this local prototype.
            server.Configure(new ConfigurationBuilder().Build());
            server.Listen(bindAddress ?? IPAddress.Loopback, port);
            server.AddServerHeader = false;
        });
        builder.Services.AddSingleton<IWorkflowAccessTransport>(_ => accessTransport ?? new WorkflowAccessClient());
        builder.Services.AddSingleton(provider => new OfficeJournal(databasePath, projectsRoot,
            provider.GetRequiredService<IWorkflowAccessTransport>(), provider.GetRequiredService<ILogger<OfficeJournal>>(), Diagnostics));
        if (filePilotEnabled)
            builder.Services.AddSingleton(provider => new OfficeProjectPilot(projectsRoot,
                provider.GetRequiredService<OfficeJournal>(), provider.GetRequiredService<ILogger<OfficeProjectPilot>>(), Diagnostics));
        if (pilotHash is not null)
            builder.Services.AddSingleton(provider => new OfficeOrderPilot(databasePath, pilotHash,
                provider.GetRequiredService<IWorkflowAccessTransport>(), provider.GetRequiredService<ILogger<OfficeOrderPilot>>(), Diagnostics));
        if (workAccess is not null)
        {
            builder.Services.AddDataProtection().SetApplicationName("JournalWorkflowOfficeServer");
            builder.Services.AddSingleton(provider => new OfficeWorkSessions(provider.GetRequiredService<IDataProtectionProvider>(), tokenBytes));
            builder.Services.AddScoped(provider => new OfficeWorkspace(databasePath, projectsRoot, workAccess.AllowWrites,
                provider.GetRequiredService<OfficeWorkIdentity>(), provider.GetRequiredService<IWorkflowAccessTransport>()));
            builder.Services.AddScoped<OfficeWorkIdentity>();
            builder.Services.AddScoped(provider => new OfficeWorkOrders(databasePath,
                provider.GetRequiredService<OfficeWorkIdentity>(), provider.GetRequiredService<OfficeJournal>(),
                provider.GetRequiredService<IWorkflowAccessTransport>(), provider.GetRequiredService<ILogger<OfficeWorkOrders>>(), Diagnostics));
            builder.Services.AddScoped(provider => new OfficeWorkProjects(projectsRoot, provider.GetRequiredService<OfficeWorkOrders>(),
                provider.GetRequiredService<ILogger<OfficeWorkProjects>>(), Diagnostics));
        }

        var app = builder.Build();
        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

        var api = app.MapGroup("/api/v1");
        api.AddEndpointFilter(async (invocation, next) =>
        {
            var context = invocation.HttpContext;
            if (Bearer(context) is not { Length: 64 } provided ||
                !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(provided), tokenBytes))
                return Challenge(context);
            return await next(invocation);
        });
        api.MapGet("/status", (OfficeJournal journal, CancellationToken token) => journal.CheckAsync(token));
        api.MapGet("/orders", (OfficeJournal journal, CancellationToken token) => journal.ReadOrdersAsync(token));
        api.MapGet("/diagnostics", Diagnostics.Read);
        if (pilotHash is not null)
            api.MapPost("/order-pilot", (OfficeOrderPilot orders, HttpRequest request, CancellationToken token) => orders.PublishAsync(request, token));
        if (workAccess is not null)
        {
            api.MapPost("/storage-connect", (OfficeWorkspace workspace, OfficeWorkSessions sessions, CancellationToken token) =>
                workspace.ConnectAsync(sessions, token));
            var storage = app.MapGroup("/api/v1/storage");
            storage.AddEndpointFilter(async (invocation, next) =>
            {
                var context = invocation.HttpContext;
                var sessions = context.RequestServices.GetRequiredService<OfficeWorkSessions>();
                if (Bearer(context) is not { } credential || !sessions.TryReadStorage(credential, out var database))
                    return Challenge(context);
                var identity = context.RequestServices.GetRequiredService<OfficeWorkIdentity>();
                identity.DatabaseId = database;
                identity.IsStorage = true;
                return await next(invocation);
            });
            storage.MapPost("/database", (WorkflowAccessRequest request, OfficeWorkspace workspace, CancellationToken token) =>
                workspace.ExecuteAsync(request, token));
            storage.MapGet("/capabilities", () => Results.Ok(new { calculation = true, guardedCalculation = true, plannedBending = true }));
            storage.MapPost("/orders/{orderId:guid}/projects/prepare", (Guid orderId, WorkflowOfficeProjectPreparation request, OfficeWorkspace workspace, CancellationToken token) =>
                workspace.PrepareProjectAsync(orderId, request, token));
            var files = storage.MapGroup("/orders/{orderId:guid}/projects/{projectId:guid}");
            files.MapPost("/calculation", (Guid orderId, Guid projectId, WorkflowOfficeCalculationPreparation request, OfficeWorkspace workspace, CancellationToken token) =>
                workspace.PrepareCalculationAsync(orderId, projectId, request, token));
            files.MapGet("/files", (Guid orderId, Guid projectId, string? reportProfile, string? reportName, OfficeWorkProjects projects, CancellationToken token) =>
                projects.ManifestAsync(orderId, projectId, token, reportProfile, reportName));
            files.MapGet("/file", (Guid orderId, Guid projectId, string path, OfficeWorkProjects projects, CancellationToken token) =>
                projects.DownloadAsync(orderId, projectId, path, token));
            files.MapPut("/file", (Guid orderId, Guid projectId, string path, OfficeWorkProjects projects, HttpRequest request, CancellationToken token) =>
                workAccess.AllowWrites ? projects.UploadAsync(orderId, projectId, path, request, token) :
                    Task.FromResult<IResult>(Results.Json(new { error = "remote-writes-disabled" }, statusCode: 403)));
            api.MapGet("/technologists", OfficeWorkSessions.ListTechnologistsAsync);
            api.MapPost("/connection", (OfficeWorkSessions.Selection selection, OfficeWorkSessions sessions, OfficeJournal journal, CancellationToken token) =>
                sessions.ConnectAsync(selection, journal, workAccess.AllowWrites, token));
            var work = app.MapGroup("/api/v1/work");
            work.AddEndpointFilter(async (invocation, next) =>
            {
                var context = invocation.HttpContext;
                if (Bearer(context) is not { } provided) return Challenge(context);
                var sessions = context.RequestServices.GetRequiredService<OfficeWorkSessions>();
                if (!sessions.TryRead(provided, out var database, out var employee))
                {
                    if (provided.Length != 64 || workAccess.DatabaseId is not { } configured) return Challenge(context);
                    var digest = SHA256.HashData(Encoding.UTF8.GetBytes(provided));
                    var key = workAccess.Keys.FirstOrDefault(entry => CryptographicOperations.FixedTimeEquals(entry.Hash, digest));
                    if (key is null) return Challenge(context);
                    database = configured;
                    employee = key.EmployeeId;
                }
                if (!workAccess.AllowWrites && !HttpMethods.IsGet(context.Request.Method))
                    return Results.Json(new { error = "remote-writes-disabled" }, statusCode: StatusCodes.Status403Forbidden);
                var identity = context.RequestServices.GetRequiredService<OfficeWorkIdentity>();
                identity.DatabaseId = database;
                identity.EmployeeId = employee;
                return await next(invocation);
            });
            work.MapGet("/session", (OfficeWorkIdentity identity, OfficeJournal journal, CancellationToken token) =>
                OfficeWorkSessions.ReadSessionAsync(identity, journal, workAccess.AllowWrites, token));
            var remoteOrders = work.MapGroup("/orders");
            remoteOrders.MapGet("/", (OfficeWorkOrders orders, CancellationToken token) => orders.ListAsync(token));
            remoteOrders.MapPost("/", (OfficeWorkOrders orders, OfficeWorkOrders.NewOrder draft, CancellationToken token) => orders.CreateAsync(draft, token));
            remoteOrders.MapGet("/{orderId:guid}", (Guid orderId, OfficeWorkOrders orders, CancellationToken token) => orders.DetailsAsync(orderId, token));
            remoteOrders.MapPut("/{orderId:guid}", (Guid orderId, OfficeWorkOrders orders, OfficeWorkOrders.OrderEdit edit, CancellationToken token) =>
                orders.UpdateAsync(orderId, edit, token));
            var project = remoteOrders.MapGroup("/{orderId:guid}/projects/{projectId:guid}");
            project.MapGet("/files", (Guid orderId, Guid projectId, OfficeWorkProjects files, CancellationToken token) =>
                files.ManifestAsync(orderId, projectId, token));
            project.MapGet("/file", (Guid orderId, Guid projectId, string path, OfficeWorkProjects files, CancellationToken token) =>
                files.DownloadAsync(orderId, projectId, path, token));
            project.MapPut("/file", (Guid orderId, Guid projectId, string path, OfficeWorkProjects files, HttpRequest request, CancellationToken token) =>
                files.UploadAsync(orderId, projectId, path, request, token));
        }
        if (filePilotEnabled)
        {
            var pilot = api.MapGroup("/orders/{orderId:guid}/file-pilot");
            pilot.MapGet("/", (Guid orderId, OfficeProjectPilot files, CancellationToken token) => files.ListAsync(orderId, token));
            pilot.MapGet("/{projectId:guid}", (Guid orderId, Guid projectId, OfficeProjectPilot files, CancellationToken token) =>
                files.DownloadAsync(orderId, projectId, token));
            pilot.MapPut("/{projectId:guid}", (Guid orderId, Guid projectId, OfficeProjectPilot files, HttpContext context, CancellationToken token) =>
                files.UploadAsync(orderId, projectId, context, token));
        }

        await app.RunAsync(cancellationToken);
        return 0;
    }

    private static string? Bearer(HttpContext context)
    {
        const string prefix = "Bearer ";
        context.Response.Headers.CacheControl = "no-store";
        var header = context.Request.Headers.Authorization;
        return header.Count == 1 && header[0] is { Length: > 7 and <= 519 } value && value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? value[prefix.Length..] : null;
    }

    private static IResult Challenge(HttpContext context)
    {
        context.Response.Headers.WWWAuthenticate = "Bearer";
        return Results.Unauthorized();
    }
}

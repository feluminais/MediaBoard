using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using MediaBoard.Core.Accounts;
using MediaBoard.Core.Remote;
using MediaBoard.Server.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using HttpProtocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols;

namespace MediaBoard.Server;

public sealed record SharingOptions(int Port, string ServerName, X509Certificate2 Certificate);

public sealed class PortInUseException(int port, Exception innerException) : Exception($"Port {port} is in use.", innerException)
{
    public int Port { get; } = port;
}

/// <summary>
/// The HTTPS server inside the desktop app. It listens only between <see cref="StartAsync"/> and <see cref="StopAsync"/>.
/// </summary>
/// <param name="logs">Where the host's warnings and errors go, or null for nowhere.</param>
/// <param name="time">The clock, or null for the real one.</param>
public sealed class SharingServer(
    AccountService service, AccountStore accounts, DeviceStore devices, SharedLibrary library, StreamRegistry streams,
    ILoggerProvider? logs = null, TimeProvider? time = null)
{
    private const int MaxBodyBytes = 64 * 1024;
    private const int MaxConnections = 256;
    private const string Api = "/api/v1";

    // Kestrel cuts whatever is still open after this, so a stuck client can't hold up closing the app.
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private WebApplication? _app;

    /// <summary>What <c>ping</c> answers with. <see cref="StartAsync"/> sets it from its options.</summary>
    public string ServerName { get; set; } = "";

    public bool IsRunning => Volatile.Read(ref _app) is not null;

    /// <exception cref="PortInUseException">Something else holds the port.</exception>
    /// <exception cref="InvalidOperationException">The server is already running.</exception>
    public async Task StartAsync(SharingOptions options, CancellationToken ct)
    {
        await _lifecycle.WaitAsync(ct);
        try
        {
            if (_app is not null) throw new InvalidOperationException("The server is already running.");

            ServerName = options.ServerName;
            var app = BuildHost(options);
            try
            {
                await app.StartAsync(ct);
            }
            catch (Exception e)
            {
                await app.DisposeAsync();
                if (e is IOException { InnerException: AddressInUseException }) throw new PortInUseException(options.Port, e);
                throw;
            }

            Volatile.Write(ref _app, app);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            if (_app is not { } app) return;

            Volatile.Write(ref _app, null);
            try
            {
                using var timeout = new CancellationTokenSource(StopTimeout);
                await app.StopAsync(timeout.Token);
            }
            finally
            {
                await app.DisposeAsync();
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>
    /// Maps every route. Anything not public lands in the authenticated group, and the fallback there answers whatever
    /// no route matched, so a stranger learns nothing about what exists.
    /// </summary>
    public void MapTo(WebApplication app)
    {
        app.Use(AnswerBadBodies);

        Public.Map(app.MapGroup(Api), this, service);

        var auth = new DeviceAuth(devices, accounts, _time);
        var authenticated = app.MapGroup("").AddEndpointFilter(auth).WithMetadata(auth);
        var api = authenticated.MapGroup(Api);
        Session.Map(api, service, accounts);
        Library.Map(api, library);
        Search.Map(api, library);

        var watch = new StreamWatch(streams, service, accounts, devices, library);
        Playback.Map(api, library, streams, watch);
        var events = new EventHub(service, streams, library.Owner, _time);
        Events.Map(api, events, watch, app.Lifetime.ApplicationStopping);
        // Not started by hand here: a start that fails on the port would leave these listening to the events for good.
        app.Lifetime.ApplicationStarted.Register(() =>
        {
            watch.Start();
            events.Start();
        });
        // Kestrel waits for open requests while it stops, and a stream would use up the whole timeout.
        app.Lifetime.ApplicationStopping.Register(() =>
        {
            events.Dispose();
            watch.Dispose();
            streams.CloseAll();
        });

        authenticated.MapFallback("{*path}", () => ApiResults.Error(ApiErrorCode.NotFound));
    }

    // Kestrel enforces the limit as a body is read, which a chunked body needs. A declared length can be refused at once.
    private static async Task AnswerBadBodies(HttpContext context, RequestDelegate next)
    {
        if (context.Request.ContentLength > MaxBodyBytes)
        {
            await ApiResults.Error(ApiErrorCode.TooLarge).ExecuteAsync(context);
            return;
        }

        try
        {
            await next(context);
        }
        catch (BadHttpRequestException e) when (!context.Response.HasStarted)
        {
            var code = e.StatusCode == StatusCodes.Status413PayloadTooLarge ? ApiErrorCode.TooLarge : ApiErrorCode.BadRequest;
            await ApiResults.Error(code).ExecuteAsync(context);
        }
    }

    private WebApplication BuildHost(SharingOptions options)
    {
        // The empty builder reads no appsettings, environment variables or command line, so nothing beside the app can
        // add a listener or turn request logging on.
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.Services.AddRoutingCore();
        // Below Warning, ASP.NET logs each request's path and query string.
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        if (logs is not null) builder.Logging.AddProvider(logs);

        builder.WebHost.UseKestrelCore().ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxRequestBodySize = MaxBodyBytes;
            kestrel.Limits.MaxConcurrentConnections = MaxConnections;
            kestrel.ListenAnyIP(options.Port, listen =>
            {
                // The client speaks HTTP/1.1 only, so nothing else is offered.
                listen.Protocols = HttpProtocols.Http1;
                listen.UseHttps(new HttpsConnectionAdapterOptions
                {
                    ServerCertificate = options.Certificate,
                    SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                });
            });
        });

        var app = builder.Build();
        MapTo(app);
        return app;
    }
}

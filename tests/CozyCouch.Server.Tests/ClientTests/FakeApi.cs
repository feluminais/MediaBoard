using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using CozyCouch.Core.Remote;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using HttpProtocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols;

namespace CozyCouch.Server.Tests.ClientTests;

/// <summary>
/// A TLS socket that answers however a test says, for what the real server won't do on demand: hang, go quiet, lie about
/// its version. It listens on both loopback addresses, so a test can reach it by name or by either address.
/// </summary>
internal sealed class FakeApi : IAsyncDisposable
{
    private readonly WebApplication _app;
    private bool _disposed;

    private FakeApi(WebApplication app, int port)
    {
        _app = app;
        Port = port;
    }

    public int Port { get; }

    public string Address => $"127.0.0.1:{Port}";

    /// <summary>Path and query of every request, in order.</summary>
    public ConcurrentQueue<string> Urls { get; } = new();

    /// <summary>The Authorization header of every request, empty where there was none.</summary>
    public ConcurrentQueue<string> Authorizations { get; } = new();

    public int Count(string route) => Urls.Count(url => url == "/api/v1/" + route);

    public static async Task<FakeApi> StartAsync(X509Certificate2 certificate, RequestDelegate answer, int? port = null)
    {
        var chosen = port ?? ServerFixture.FreePort();
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.Services.AddRoutingCore();
        builder.WebHost.UseKestrelCore().ConfigureKestrel(kestrel => kestrel.ListenLocalhost(chosen, listen =>
        {
            listen.Protocols = HttpProtocols.Http1;
            listen.UseHttps(new HttpsConnectionAdapterOptions { ServerCertificate = certificate });
        }));

        var app = builder.Build();
        var fake = new FakeApi(app, chosen);
        app.Run(context =>
        {
            fake.Urls.Enqueue(context.Request.Path + context.Request.QueryString);
            fake.Authorizations.Enqueue(context.Request.Headers.Authorization.ToString());
            return answer(context);
        });
        await app.StartAsync(TestContext.Current.CancellationToken);
        return fake;
    }

    /// <summary>A server of this API version: ping answers, and anything else is left to <paramref name="events"/>.</summary>
    public static Task<FakeApi> ForEventsAsync(X509Certificate2 certificate, Func<HttpContext, Task> events, int version = ApiVersion.Current, int? port = null) =>
        StartAsync(
            certificate,
            context => context.Request.Path.Value!.EndsWith("/ping", StringComparison.Ordinal)
                ? Results.Json(new PingReply(version), ApiJson.Options).ExecuteAsync(context)
                : events(context),
            port);

    /// <summary>Sends the headers now, as the real events route does.</summary>
    public static Task OpenEventsAsync(HttpContext context)
    {
        context.Response.StatusCode = (int)HttpStatusCode.OK;
        context.Response.ContentType = "text/event-stream";
        return context.Response.Body.FlushAsync(context.RequestAborted);
    }

    public static async Task SendAsync(HttpContext context, string frames)
    {
        await context.Response.WriteAsync(frames, context.RequestAborted);
        await context.Response.Body.FlushAsync(context.RequestAborted);
    }

    /// <summary>Holds a request open until the client leaves.</summary>
    public static async Task HangAsync(HttpContext context)
    {
        try
        {
            await Task.Delay(Timeout.Infinite, context.RequestAborted);
        }
        catch (OperationCanceledException)
        {
            // The client left, which is the normal end here.
        }
    }

    /// <summary>Can be called early, to take a server down in the middle of a test.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;

        _disposed = true;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await _app.StopAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            // A request a test left hanging. Disposing cuts it.
        }

        await _app.DisposeAsync();
    }
}

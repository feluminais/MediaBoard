using System.Net;
using MediaBoard.Core.Accounts;
using MediaBoard.Core.Remote;
using MediaBoard.Core.Settings;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Time.Testing;

namespace MediaBoard.Server.Tests.ClientTests;

/// <summary>
/// The client against a real TLS socket: the real server wherever it can show the thing, a <see cref="FakeApi"/> where a
/// test needs a misbehaving one. A clock the test moves makes the timing exact.
/// </summary>
public sealed class ApiClientTests
{
    private const string Token = "device-token-value";

    private static readonly TimeSpan Within = TimeSpan.FromSeconds(30);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ServerEndpoint Endpoint(string fingerprint, params string[] addresses) => new("Library", addresses, fingerprint);

    private static ApiClient NewClient(FakeApi api, string fingerprint, TimeProvider? time = null) =>
        new(Endpoint(fingerprint, api.Address), () => Token, _ => { }, time);

    private static string FingerprintOf(ServerFixture fixture) => CertificateStore.Fingerprint(fixture.NewCertificate());

    private sealed class Live : IAsyncDisposable
    {
        private Live(ServerFixture fixture, int port)
        {
            Fixture = fixture;
            Port = port;
            Fingerprint = FingerprintOf(fixture);
        }

        public ServerFixture Fixture { get; }

        public int Port { get; }

        public string Fingerprint { get; }

        public string Address => $"127.0.0.1:{Port}";

        public Account Sam { get; private set; } = null!;

        public IssuedDevice Phone { get; private set; } = null!;

        public static async Task<Live> StartAsync()
        {
            var fixture = new ServerFixture();
            var port = ServerFixture.FreePort();
            await fixture.Server.StartAsync(new SharingOptions(port, ServerFixture.ServerName, fixture.NewCertificate()) { LoopbackOnly = true }, Ct);
            var live = new Live(fixture, port) { Sam = fixture.NewPerson("Sam") };
            live.Phone = fixture.Join(live.Sam);
            return live;
        }

        public Task StartAgainAsync() =>
            Fixture.Server.StartAsync(new SharingOptions(Port, ServerFixture.ServerName, Fixture.NewCertificate()) { LoopbackOnly = true }, Ct);

        public ApiClient Client(Action<ConnectionEvent>? report = null) =>
            new(Endpoint(Fingerprint, Address), () => Phone.Token, report ?? (_ => { }));

        public async Task<int> AddFileAsync(int bytes)
        {
            var folder = Fixture.AddFolder("films");
            var id = await Fixture.AddFileAsync(folder, "Heat (1995).mkv", Fixture.Movie("Heat", 101));
            await using var file = File.OpenWrite(Path.Combine(folder.Path, "Heat (1995).mkv"));
            file.SetLength(bytes);
            return id;
        }

        public ValueTask DisposeAsync() => Fixture.DisposeAsync();
    }

    /// <summary>A monitor and its client, tied together as the app will tie them.</summary>
    private static (ConnectionMonitor Monitor, ApiClient Client) Watched(ServerEndpoint endpoint, string? token, TimeProvider? time = null)
    {
        ConnectionMonitor monitor = null!;
        var client = new ApiClient(endpoint, () => token, happened => monitor.Report(happened), time);
        monitor = new ConnectionMonitor(client, time);
        return (monitor, client);
    }

    private static async Task ReachAsync(ConnectionMonitor monitor, ConnectionState state)
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Check()
        {
            if (monitor.State == state) reached.TrySetResult();
        }

        monitor.Changed += Check;
        try
        {
            Check();
            await reached.Task.WaitAsync(Within, Ct);
        }
        finally
        {
            monitor.Changed -= Check;
        }
    }

    /// <summary>Runs a monitor for as long as the scope lasts.</summary>
    private sealed class Running : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        private readonly Task _run;

        public Running(ConnectionMonitor monitor) => _run = monitor.RunAsync(_stop.Token);

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            await _run.WaitAsync(Within, Ct);
            _stop.Dispose();
        }
    }

    // ---- Pinning ----

    [Fact]
    public async Task Pin_RejectsOtherCertificate_Throws_IdentityChanged()
    {
        await using var fixture = new ServerFixture();
        await using var other = new ServerFixture();
        await using var api = await FakeApi.StartAsync(fixture.NewCertificate(), FakeApi.HangAsync);
        var reports = new List<ConnectionEvent>();
        using var client = new ApiClient(Endpoint(FingerprintOf(other), api.Address), () => Token, reports.Add);

        await Assert.ThrowsAsync<IdentityChangedException>(() => client.GetAsync<PingReply>("ping", Ct));
        Assert.Throws<IdentityChangedException>(() => client.Get<PingReply>("ping"));

        Assert.All(reports, report => Assert.Equal(ConnectionEvent.IdentityChanged, report));
        Assert.Empty(api.Urls);
    }

    [Fact]
    public async Task Pin_AcceptsMatchingSelfSigned()
    {
        await using var live = await Live.StartAsync();
        using var client = live.Client();

        var reply = await client.GetAsync<PingReply>("ping", Ct);

        Assert.Equal(ServerFixture.ServerName, reply.Name);
    }

    [Fact]
    public async Task Pin_IgnoresHostName()
    {
        await using var fixture = new ServerFixture();
        await using var api = await FakeApi.ForEventsAsync(fixture.NewCertificate(), FakeApi.HangAsync);
        using var client = new ApiClient(Endpoint(FingerprintOf(fixture), $"localhost:{api.Port}"), () => Token, _ => { });

        Assert.Equal("Fake", (await client.GetAsync<PingReply>("ping", Ct)).Name);
    }

    [Fact]
    public async Task Pin_IgnoresCaseOfTheFingerprint()
    {
        await using var live = await Live.StartAsync();
        using var client = new ApiClient(Endpoint(live.Fingerprint.ToUpperInvariant(), live.Address), () => null, _ => { });

        Assert.Equal(ServerFixture.ServerName, (await client.GetAsync<PingReply>("ping", Ct)).Name);
    }

    [Fact]
    public async Task Pin_WrongFirstAddress_StillFindsTheRightServer()
    {
        await using var fixture = new ServerFixture();
        await using var stranger = new ServerFixture();
        await using var wrong = await FakeApi.ForEventsAsync(stranger.NewCertificate(), FakeApi.HangAsync);
        await using var right = await FakeApi.ForEventsAsync(fixture.NewCertificate(), FakeApi.HangAsync);
        var reports = new List<ConnectionEvent>();
        using var client = new ApiClient(Endpoint(FingerprintOf(fixture), wrong.Address, right.Address), () => Token, reports.Add);

        await client.GetAsync<PingReply>("ping", Ct);

        Assert.Empty(reports);
        Assert.Empty(wrong.Urls);
    }

    [Fact]
    public async Task Address_Ipv6InBrackets_Works()
    {
        await using var fixture = new ServerFixture();
        await using var api = await FakeApi.ForEventsAsync(fixture.NewCertificate(), FakeApi.HangAsync);
        using var client = new ApiClient(Endpoint(FingerprintOf(fixture), $"[::1]:{api.Port}"), () => Token, _ => { });

        Assert.Equal("Fake", (await client.GetAsync<PingReply>("ping", Ct)).Name);
    }

    [Fact]
    public async Task Address_ThatIsNoAddress_IsSkipped()
    {
        await using var live = await Live.StartAsync();
        using var client = new ApiClient(Endpoint(live.Fingerprint, "::1:47880:", "http s://", live.Address), () => null, _ => { });

        Assert.Equal(ServerFixture.ServerName, (await client.GetAsync<PingReply>("ping", Ct)).Name);
    }

    // ---- Addresses and timeouts ----

    [Fact]
    public async Task FirstAddressDown_TriesNext_RemembersWorkingOne()
    {
        await using var fixture = new ServerFixture();
        var downPort = ServerFixture.FreePort();
        await using var up = await FakeApi.ForEventsAsync(fixture.NewCertificate(), FakeApi.HangAsync);
        var reports = new List<ConnectionEvent>();
        using var client = new ApiClient(Endpoint(FingerprintOf(fixture), $"127.0.0.1:{downPort}", up.Address), () => Token, reports.Add);

        await client.GetAsync<PingReply>("ping", Ct);
        await using var cameUp = await FakeApi.ForEventsAsync(fixture.NewCertificate(), FakeApi.HangAsync, port: downPort);
        await client.GetAsync<PingReply>("ping", Ct);

        Assert.Equal(2, up.Count("ping"));
        Assert.Empty(cameUp.Urls);
        Assert.Empty(reports);
    }

    [Fact]
    public async Task EveryAddressDown_IsUnreachable()
    {
        await using var fixture = new ServerFixture();
        var reports = new List<ConnectionEvent>();
        using var client = new ApiClient(Endpoint(FingerprintOf(fixture), $"127.0.0.1:{ServerFixture.FreePort()}"), () => Token, reports.Add);

        await Assert.ThrowsAsync<ServerUnreachableException>(() => client.GetAsync<PingReply>("ping", Ct));

        Assert.Equal([ConnectionEvent.Unreachable], reports);
    }

    [Fact]
    public async Task Post_CutAfterItArrived_IsNotSentToTheNextAddress()
    {
        await using var fixture = new ServerFixture();
        await using var cut = await FakeApi.StartAsync(fixture.NewCertificate(), context =>
        {
            context.Abort();
            return Task.CompletedTask;
        });
        await using var next = await FakeApi.StartAsync(fixture.NewCertificate(), _ => Task.CompletedTask);
        using var client = new ApiClient(Endpoint(FingerprintOf(fixture), cut.Address, next.Address), () => Token, _ => { });

        await Assert.ThrowsAsync<ServerUnreachableException>(() => client.SendAsync(HttpMethod.Post, "invites/redeem", new RedeemRequest(), Ct));

        Assert.Empty(next.Urls);
    }

    [Fact]
    public async Task Timeout_IsUnreachable()
    {
        await using var fixture = new ServerFixture();
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var api = await FakeApi.StartAsync(fixture.NewCertificate(), context =>
        {
            arrived.TrySetResult();
            return FakeApi.HangAsync(context);
        });
        var clock = new FakeTimeProvider();
        using var client = NewClient(api, FingerprintOf(fixture), clock);

        var pending = client.GetAsync<PingReply>("ping", Ct);
        await arrived.Task.WaitAsync(Within, Ct);

        clock.Advance(TimeSpan.FromSeconds(9.9));
        await Task.Delay(100, Ct);
        Assert.False(pending.IsCompleted);

        clock.Advance(TimeSpan.FromSeconds(0.1));
        await Assert.ThrowsAsync<ServerUnreachableException>(() => pending.WaitAsync(Within, Ct));
    }

    [Fact]
    public async Task Timeout_InTheBlockingGet_IsUnreachable()
    {
        await using var fixture = new ServerFixture();
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var api = await FakeApi.StartAsync(fixture.NewCertificate(), context =>
        {
            arrived.TrySetResult();
            return FakeApi.HangAsync(context);
        });
        var clock = new FakeTimeProvider();
        using var client = NewClient(api, FingerprintOf(fixture), clock);

        var pending = Task.Run(() => client.Get<PingReply>("ping"), Ct);
        await arrived.Task.WaitAsync(Within, Ct);

        clock.Advance(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<ServerUnreachableException>(() => pending.WaitAsync(Within, Ct));
    }

    [Fact]
    public async Task Cancelled_ByTheCaller_IsNotUnreachable()
    {
        await using var fixture = new ServerFixture();
        await using var api = await FakeApi.StartAsync(fixture.NewCertificate(), FakeApi.HangAsync);
        var reports = new List<ConnectionEvent>();
        using var client = new ApiClient(Endpoint(FingerprintOf(fixture), api.Address), () => Token, reports.Add);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetAsync<PingReply>("ping", cancel.Token));

        Assert.Empty(reports);
    }

    [Fact]
    public async Task Get_OnAThreadThatNeverPumps_DoesNotDeadlock()
    {
        await using var live = await Live.StartAsync();
        using var client = live.Client();
        var finished = new TaskCompletionSource<PingReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        var uiThread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new NeverPumps());
            try
            {
                finished.SetResult(client.Get<PingReply>("ping"));
            }
            catch (Exception e)
            {
                finished.SetException(e);
            }
        }) { IsBackground = true };

        uiThread.Start();

        Assert.Equal(ServerFixture.ServerName, (await finished.Task.WaitAsync(Within, Ct)).Name);
    }

    /// <summary>A UI thread busy waiting on the caller: nothing posted to it ever runs.</summary>
    private sealed class NeverPumps : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
        {
        }

        public override void Send(SendOrPostCallback d, object? state)
        {
        }
    }

    // ---- Answers ----

    [Fact]
    public async Task Get_ReadsWhatTheServerSends()
    {
        await using var live = await Live.StartAsync();
        using var client = live.Client();

        Assert.Equal("Sam", (await client.GetAsync<MeReply>("me", Ct)).Name);
        Assert.Equal("Sam", client.Get<MeReply>("me").Name);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Send_CarriesItsBody(bool blocking)
    {
        const string NewPassword = "another long password";
        await using var live = await Live.StartAsync();
        using var client = live.Client();
        var body = new ChangePasswordRequest { Current = ServerFixture.Password, Next = NewPassword };

        if (blocking) client.Send(HttpMethod.Put, "me/password", body);
        else await client.SendAsync(HttpMethod.Put, "me/password", body, Ct);

        Assert.NotNull(live.Fixture.Service.SignIn("Sam", NewPassword, "Tablet", "198.51.100.1").Issued);
    }

    [Fact]
    public async Task Error_Body_MapsToApiException()
    {
        await using var live = await Live.StartAsync();
        using var client = live.Client();

        var thrown = await Assert.ThrowsAsync<ApiException>(() => client.GetAsync<MeReply>("titles/999999", Ct));
        var blocking = Assert.Throws<ApiException>(() => client.Get<MeReply>("titles/999999"));

        Assert.Equal(ApiErrorCode.NotFound, thrown.Code);
        Assert.Equal(ApiErrorCode.NotFound, blocking.Code);
    }

    [Fact]
    public async Task Error_WithNoCode_IsUnreachable()
    {
        await using var fixture = new ServerFixture();
        await using var api = await FakeApi.StartAsync(fixture.NewCertificate(), context =>
        {
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            return Task.CompletedTask;
        });
        using var client = NewClient(api, FingerprintOf(fixture));

        await Assert.ThrowsAsync<ServerUnreachableException>(() => client.GetAsync<PingReply>("ping", Ct));
    }

    [Fact]
    public async Task Answer_ThatIsNotJson_IsUnreachable()
    {
        await using var fixture = new ServerFixture();
        await using var api = await FakeApi.StartAsync(fixture.NewCertificate(), context => context.Response.WriteAsync("<html>", context.RequestAborted));
        using var client = NewClient(api, FingerprintOf(fixture));

        await Assert.ThrowsAsync<ServerUnreachableException>(() => client.GetAsync<PingReply>("ping", Ct));
    }

    [Fact]
    public async Task Unauthorized_SetsSignedOut()
    {
        await using var live = await Live.StartAsync();
        var (monitor, client) = Watched(Endpoint(live.Fingerprint, live.Address), "not-a-live-token");
        using (client)
        {
            var thrown = Assert.Throws<ApiException>(() => client.Get<MeReply>("me"));

            Assert.Equal(ApiErrorCode.Unauthorized, thrown.Code);
            Assert.Equal(ConnectionState.SignedOut, monitor.State);
        }
    }

    [Fact]
    public async Task WrongPassword_IsNotSignedOut()
    {
        await using var live = await Live.StartAsync();
        var (monitor, client) = Watched(Endpoint(live.Fingerprint, live.Address), null);
        using (client)
        {
            var body = new SignInRequest { Name = "Sam", Password = "wrong password here", DeviceName = "Tablet" };

            var thrown = await Assert.ThrowsAsync<ApiException>(() => client.SendAsync(HttpMethod.Post, "sign-in", body, Ct));

            Assert.Equal(ApiErrorCode.WrongPassword, thrown.Code);
            Assert.Equal(ConnectionState.Connecting, monitor.State);
        }
    }

    // ---- Streams ----

    [Fact]
    public async Task OpenStream_ForwardsTheRange()
    {
        await using var live = await Live.StartAsync();
        var file = await live.AddFileAsync(4096);
        using var client = live.Client();

        using var response = await client.OpenStreamAsync(file, "bytes=100-199", Ct);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal(100, (await response.Content.ReadAsByteArrayAsync(Ct)).Length);
    }

    [Fact]
    public async Task OpenStream_RangePastTheEnd_IsForThePlayerToRead()
    {
        await using var live = await Live.StartAsync();
        var file = await live.AddFileAsync(4096);
        using var client = live.Client();

        using var response = await client.OpenStreamAsync(file, "bytes=9999-", Ct);

        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, response.StatusCode);
    }

    [Fact]
    public async Task OpenStream_ThatIsRefused_ThrowsItsCode()
    {
        await using var live = await Live.StartAsync();
        using var client = live.Client();

        var thrown = await Assert.ThrowsAsync<ApiException>(() => client.OpenStreamAsync(999999, null, Ct));

        Assert.Equal(ApiErrorCode.NotFound, thrown.Code);
    }

    [Fact]
    public async Task OpenStream_CutMidway_FailsTheRead_AndARequestAgainGetsTheCode()
    {
        await using var live = await Live.StartAsync();
        var file = await live.AddFileAsync(64 * 1024 * 1024);
        using var client = live.Client();
        using var response = await client.OpenStreamAsync(file, null, Ct);
        await using var body = await response.Content.ReadAsStreamAsync(Ct);
        await body.ReadExactlyAsync(new byte[64 * 1024], Ct);

        live.Fixture.Streams.Stop(live.Fixture.Streams.Current.Single().Id);

        await Assert.ThrowsAnyAsync<IOException>(() => body.CopyToAsync(Stream.Null, Ct).WaitAsync(Within, Ct));
        var thrown = await Assert.ThrowsAsync<ApiException>(() => client.OpenStreamAsync(file, "bytes=0-0", Ct));
        Assert.Equal(ApiErrorCode.StreamStopped, thrown.Code);
    }

    [Fact]
    public async Task OpenStream_Body_HasNoTimeout()
    {
        await using var fixture = new ServerFixture();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var api = await FakeApi.StartAsync(fixture.NewCertificate(), async context =>
        {
            await FakeApi.SendAsync(context, "start");
            await release.Task;
            await FakeApi.SendAsync(context, "-end");
        });
        var clock = new FakeTimeProvider();
        using var client = NewClient(api, FingerprintOf(fixture), clock);

        using var response = await client.OpenStreamAsync(1, null, Ct);
        await using var body = await response.Content.ReadAsStreamAsync(Ct);
        await body.ReadExactlyAsync(new byte[5], Ct);
        clock.Advance(TimeSpan.FromHours(1));
        release.SetResult();

        using var rest = new StreamReader(body);
        Assert.Equal("-end", await rest.ReadToEndAsync(Ct).WaitAsync(Within, Ct));
    }

    [Fact]
    public async Task OpenStream_ThatNeverAnswers_TimesOut()
    {
        await using var fixture = new ServerFixture();
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var api = await FakeApi.StartAsync(fixture.NewCertificate(), context =>
        {
            arrived.TrySetResult();
            return FakeApi.HangAsync(context);
        });
        var clock = new FakeTimeProvider();
        using var client = NewClient(api, FingerprintOf(fixture), clock);

        var pending = client.OpenStreamAsync(1, null, Ct);
        await arrived.Task.WaitAsync(Within, Ct);
        clock.Advance(TimeSpan.FromSeconds(10));

        await Assert.ThrowsAsync<ServerUnreachableException>(() => pending.WaitAsync(Within, Ct));
    }

    // ---- Events ----

    [Fact]
    public async Task Events_YieldNames_AndSkipCommentsAndDataLines()
    {
        await using var fixture = new ServerFixture();
        await using var api = await FakeApi.StartAsync(fixture.NewCertificate(), async context =>
        {
            await FakeApi.OpenEventsAsync(context);
            await FakeApi.SendAsync(context, ": keep-alive\n\nevent: library-changed\ndata: \n\n: keep-alive\n\nevent: stream-stopped\ndata: \n\n");
        });
        using var client = NewClient(api, FingerprintOf(fixture));

        var names = await client.EventsAsync(Ct).ToListAsync(Ct);

        Assert.Equal(["library-changed", "stream-stopped"], names);
    }

    [Fact]
    public async Task Events_FromTheRealServer_EndAfterSignedOut()
    {
        await using var live = await Live.StartAsync();
        var listening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = live.Client(happened =>
        {
            if (happened == ConnectionEvent.Listening) listening.TrySetResult();
        });

        var names = client.EventsAsync(Ct).ToListAsync(Ct).AsTask();
        await listening.Task.WaitAsync(Within, Ct);
        live.Fixture.Service.SignOutDevice(live.Phone.Device.Id);

        Assert.Equal(["signed-out"], await names.WaitAsync(Within, Ct));
    }

    [Fact]
    public async Task Events_Cut_EndsTheSequence()
    {
        await using var fixture = new ServerFixture();
        var heard = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var api = await FakeApi.StartAsync(fixture.NewCertificate(), async context =>
        {
            await FakeApi.OpenEventsAsync(context);
            await FakeApi.SendAsync(context, "event: library-changed\ndata: \n\nevent: half");

            // Not before the client has it: an abort throws away whatever is still on its way.
            await heard.Task;
            context.Abort();
        });
        using var client = NewClient(api, FingerprintOf(fixture));

        await using var events = client.EventsAsync(Ct).GetAsyncEnumerator(Ct);
        Assert.True(await events.MoveNextAsync());
        Assert.Equal("library-changed", events.Current);
        heard.SetResult();

        Assert.False(await events.MoveNextAsync().AsTask().WaitAsync(Within, Ct));
    }

    [Fact]
    public async Task Events_SecondConnection_IsRefused_UntilTheFirstEnds()
    {
        await using var fixture = new ServerFixture();
        await using var api = await FakeApi.StartAsync(fixture.NewCertificate(), async context =>
        {
            await FakeApi.OpenEventsAsync(context);
            await FakeApi.SendAsync(context, "event: hello\ndata: \n\n");
            await FakeApi.HangAsync(context);
        });
        using var client = NewClient(api, FingerprintOf(fixture));

        await using (var first = client.EventsAsync(Ct).GetAsyncEnumerator(Ct))
        {
            Assert.True(await first.MoveNextAsync());

            await using var second = client.EventsAsync(Ct).GetAsyncEnumerator(Ct);
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await second.MoveNextAsync());
        }

        await using var third = client.EventsAsync(Ct).GetAsyncEnumerator(Ct);
        Assert.True(await third.MoveNextAsync());
        Assert.Equal(2, api.Count("events"));
    }

    [Fact]
    public async Task Events_OutliveTheRequestTimeout()
    {
        await using var fixture = new ServerFixture();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var api = await FakeApi.StartAsync(fixture.NewCertificate(), async context =>
        {
            await FakeApi.OpenEventsAsync(context);
            await release.Task;
            await FakeApi.SendAsync(context, "event: late\ndata: \n\n");
        });
        var clock = new FakeTimeProvider();
        var listening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new ApiClient(Endpoint(FingerprintOf(fixture), api.Address), () => Token, _ => listening.TrySetResult(), clock);

        var names = client.EventsAsync(Ct).ToListAsync(Ct).AsTask();
        await listening.Task.WaitAsync(Within, Ct);
        clock.Advance(TimeSpan.FromSeconds(30));
        release.SetResult();

        Assert.Equal(["late"], await names.WaitAsync(Within, Ct));
    }

    [Fact]
    public async Task Events_GoneQuiet_CountAsADrop()
    {
        await using var fixture = new ServerFixture();
        await using var api = await FakeApi.StartAsync(fixture.NewCertificate(), async context =>
        {
            await FakeApi.OpenEventsAsync(context);
            await FakeApi.HangAsync(context);
        });
        var clock = new FakeTimeProvider();
        var listening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new ApiClient(Endpoint(FingerprintOf(fixture), api.Address), () => Token, _ => listening.TrySetResult(), clock);

        var names = client.EventsAsync(Ct).ToListAsync(Ct).AsTask();
        await listening.Task.WaitAsync(Within, Ct);

        clock.Advance(TimeSpan.FromSeconds(44));
        await Task.Delay(100, Ct);
        Assert.False(names.IsCompleted);

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Empty(await names.WaitAsync(Within, Ct));
    }

    [Fact]
    public async Task Events_KeepAlive_CountsAsHearingFromTheServer()
    {
        await using var fixture = new ServerFixture();
        var keepAlive = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var api = await FakeApi.StartAsync(fixture.NewCertificate(), async context =>
        {
            await FakeApi.OpenEventsAsync(context);
            await keepAlive.Task;
            await FakeApi.SendAsync(context, ": keep-alive\n\n");
            await finish.Task;
            await FakeApi.SendAsync(context, "event: late\ndata: \n\n");
        });
        var clock = new FakeTimeProvider();
        var listening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new ApiClient(Endpoint(FingerprintOf(fixture), api.Address), () => Token, _ => listening.TrySetResult(), clock);

        var names = client.EventsAsync(Ct).ToListAsync(Ct).AsTask();
        await listening.Task.WaitAsync(Within, Ct);
        clock.Advance(TimeSpan.FromSeconds(30));
        keepAlive.SetResult();
        await Task.Delay(300, Ct);
        clock.Advance(TimeSpan.FromSeconds(30));
        finish.SetResult();

        Assert.Equal(["late"], await names.WaitAsync(Within, Ct));
    }

    [Fact]
    public async Task Events_ThatAreRefused_Throw()
    {
        await using var live = await Live.StartAsync();
        var (monitor, client) = Watched(Endpoint(live.Fingerprint, live.Address), "not-a-live-token");
        using (client)
        {
            await Assert.ThrowsAsync<ApiException>(async () => await client.EventsAsync(Ct).ToListAsync(Ct));

            Assert.Equal(ConnectionState.SignedOut, monitor.State);
        }
    }

    // ---- The monitor ----

    [Fact]
    public async Task Monitor_Reconnects_AfterServerReturns()
    {
        await using var live = await Live.StartAsync();
        var (monitor, client) = Watched(Endpoint(live.Fingerprint, live.Address), live.Phone.Token);
        using (client)
        {
            await using var running = new Running(monitor);
            await ReachAsync(monitor, ConnectionState.Connected);

            await live.Fixture.Server.StopAsync();
            await ReachAsync(monitor, ConnectionState.Unreachable);

            await live.StartAgainAsync();
            await ReachAsync(monitor, ConnectionState.Connected);
        }
    }

    [Fact]
    public async Task Monitor_EventsDrop_GoesUnreachable()
    {
        await using var fixture = new ServerFixture();
        var drop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var api = await FakeApi.ForEventsAsync(fixture.NewCertificate(), async context =>
        {
            await FakeApi.OpenEventsAsync(context);
            await drop.Task;
        });
        var (monitor, client) = Watched(Endpoint(FingerprintOf(fixture), api.Address), Token, new FakeTimeProvider());
        using (client)
        {
            await using var running = new Running(monitor);
            await ReachAsync(monitor, ConnectionState.Connected);

            drop.SetResult();

            await ReachAsync(monitor, ConnectionState.Unreachable);
        }
    }

    [Fact]
    public async Task Monitor_VersionMismatch()
    {
        await using var fixture = new ServerFixture();
        var clock = new FakeTimeProvider();
        await using var api = await FakeApi.ForEventsAsync(fixture.NewCertificate(), FakeApi.HangAsync, ApiVersion.Current + 1);
        var (monitor, client) = Watched(Endpoint(FingerprintOf(fixture), api.Address), Token, clock);
        using (client)
        {
            await using var running = new Running(monitor);

            await ReachAsync(monitor, ConnectionState.VersionMismatch);
            clock.Advance(TimeSpan.FromMinutes(5));
            await Task.Delay(200, Ct);

            Assert.Equal(1, api.Count("ping"));
            Assert.Equal(0, api.Count("events"));
        }
    }

    [Fact]
    public async Task Monitor_IdentityChanged_StopsTrying()
    {
        await using var fixture = new ServerFixture();
        await using var other = new ServerFixture();
        var clock = new FakeTimeProvider();
        await using var api = await FakeApi.ForEventsAsync(fixture.NewCertificate(), FakeApi.HangAsync);
        var (monitor, client) = Watched(Endpoint(FingerprintOf(other), api.Address), Token, clock);
        using (client)
        {
            await using var running = new Running(monitor);

            await ReachAsync(monitor, ConnectionState.IdentityChanged);
            clock.Advance(TimeSpan.FromMinutes(5));
            await Task.Delay(200, Ct);

            Assert.Equal(ConnectionState.IdentityChanged, monitor.State);
        }
    }

    [Fact]
    public async Task Monitor_SignedOutFrame_StopsTrying_UntilRetry()
    {
        await using var fixture = new ServerFixture();
        var clock = new FakeTimeProvider();
        await using var api = await FakeApi.ForEventsAsync(fixture.NewCertificate(), async context =>
        {
            await FakeApi.OpenEventsAsync(context);
            await FakeApi.SendAsync(context, "event: signed-out\ndata: \n\n");
        });
        var (monitor, client) = Watched(Endpoint(FingerprintOf(fixture), api.Address), Token, clock);
        using (client)
        {
            await using var running = new Running(monitor);

            await ReachAsync(monitor, ConnectionState.SignedOut);
            clock.Advance(TimeSpan.FromMinutes(5));
            await Task.Delay(200, Ct);
            Assert.Equal(1, api.Count("events"));

            monitor.Retry();

            await ReachAsync(monitor, ConnectionState.SignedOut);
            Assert.Equal(2, api.Count("events"));
        }
    }

    [Fact]
    public async Task Monitor_Retry_DoesNotWaitOutTheBackOff()
    {
        await using var fixture = new ServerFixture();
        var clock = new FakeTimeProvider();
        var port = ServerFixture.FreePort();
        var (monitor, client) = Watched(Endpoint(FingerprintOf(fixture), $"127.0.0.1:{port}"), Token, clock);
        using (client)
        {
            await using var running = new Running(monitor);
            await ReachAsync(monitor, ConnectionState.Unreachable);
            await using var api = await FakeApi.ForEventsAsync(
                fixture.NewCertificate(),
                async context =>
                {
                    await FakeApi.OpenEventsAsync(context);
                    await FakeApi.HangAsync(context);
                },
                port: port);

            monitor.Retry();

            await ReachAsync(monitor, ConnectionState.Connected);
        }
    }

    [Fact]
    public async Task Monitor_Retry_WhileConnected_LeavesTheConnectionAlone()
    {
        await using var live = await Live.StartAsync();
        var (monitor, client) = Watched(Endpoint(live.Fingerprint, live.Address), live.Phone.Token);
        using (client)
        {
            await using var running = new Running(monitor);
            await ReachAsync(monitor, ConnectionState.Connected);
            var changes = 0;
            monitor.Changed += () => Interlocked.Increment(ref changes);

            monitor.Retry();
            await Task.Delay(200, Ct);

            Assert.Equal(0, Volatile.Read(ref changes));
            Assert.Equal(ConnectionState.Connected, monitor.State);
        }
    }

    [Fact]
    public async Task Monitor_PassesEventsOn()
    {
        await using var fixture = new ServerFixture();
        await using var api = await FakeApi.ForEventsAsync(fixture.NewCertificate(), async context =>
        {
            await FakeApi.OpenEventsAsync(context);
            await FakeApi.SendAsync(context, "event: library-changed\ndata: \n\n");
            await FakeApi.HangAsync(context);
        });
        var (monitor, client) = Watched(Endpoint(FingerprintOf(fixture), api.Address), Token);
        using (client)
        {
            var heard = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            monitor.EventReceived += name => heard.TrySetResult(name);

            await using var running = new Running(monitor);

            Assert.Equal("library-changed", await heard.Task.WaitAsync(Within, Ct));
        }
    }

    [Fact]
    public async Task Monitor_BacksOff_WhenEventsKeepEnding_UpTo30Seconds()
    {
        await using var fixture = new ServerFixture();
        var clock = new FakeTimeProvider();
        var connections = 0;
        await using var api = await FakeApi.ForEventsAsync(fixture.NewCertificate(), async context =>
        {
            await FakeApi.OpenEventsAsync(context);
            Interlocked.Increment(ref connections);
        });
        var (monitor, client) = Watched(Endpoint(FingerprintOf(fixture), api.Address), Token, clock);
        using (client)
        {
            await using var running = new Running(monitor);

            List<double> gaps = [];
            for (var seen = 1; seen <= 7; seen++)
            {
                await WaitForAsync(() => Volatile.Read(ref connections) >= seen);
                var from = clock.GetUtcNow();
                await WaitWhileMovingAsync(clock, () => Volatile.Read(ref connections) > seen);
                gaps.Add((clock.GetUtcNow() - from).TotalSeconds);
            }

            double[] expected = [1, 2, 4, 8, 16, 30, 30];
            Assert.All(gaps.Zip(expected), pair => Assert.InRange(pair.First, pair.Second, pair.Second + 2));
        }
    }

    [Fact]
    public async Task Monitor_AfterASteadyConnection_RetriesAtOnceAgain()
    {
        await using var fixture = new ServerFixture();
        var clock = new FakeTimeProvider();
        var connections = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var api = await FakeApi.ForEventsAsync(fixture.NewCertificate(), async context =>
        {
            await FakeApi.OpenEventsAsync(context);
            var which = Interlocked.Increment(ref connections);
            if (which is 3) await release.Task;
        });
        var (monitor, client) = Watched(Endpoint(FingerprintOf(fixture), api.Address), Token, clock);
        using (client)
        {
            await using var running = new Running(monitor);
            await WaitForAsync(() => Volatile.Read(ref connections) >= 1);
            await WaitWhileMovingAsync(clock, () => Volatile.Read(ref connections) >= 3);

            await ReachAsync(monitor, ConnectionState.Connected);
            clock.Advance(TimeSpan.FromSeconds(60));
            release.SetResult();
            await ReachAsync(monitor, ConnectionState.Unreachable);

            var from = clock.GetUtcNow();
            await WaitWhileMovingAsync(clock, () => Volatile.Read(ref connections) >= 4);

            Assert.InRange((clock.GetUtcNow() - from).TotalSeconds, 1, 3);
        }
    }

    [Fact]
    public async Task Monitor_RequestThatFails_EndsTheConnection_AndMakesANewOne()
    {
        await using var fixture = new ServerFixture();
        var clock = new FakeTimeProvider();
        var ended = 0;
        await using var api = await FakeApi.ForEventsAsync(fixture.NewCertificate(), async context =>
        {
            await FakeApi.OpenEventsAsync(context);
            await FakeApi.HangAsync(context);
            Interlocked.Increment(ref ended);
        });
        var (monitor, client) = Watched(Endpoint(FingerprintOf(fixture), api.Address), Token, clock);
        using (client)
        {
            await using var running = new Running(monitor);
            await ReachAsync(monitor, ConnectionState.Connected);

            monitor.Report(ConnectionEvent.Unreachable);
            await ReachAsync(monitor, ConnectionState.Unreachable);
            await WaitWhileMovingAsync(clock, () => monitor.State == ConnectionState.Connected);

            await WaitForAsync(() => Volatile.Read(ref ended) == 1);
            Assert.Equal(2, api.Count("events"));
        }
    }

    [Fact]
    public async Task Monitor_LateFailure_DoesNotReplaceSignedOut()
    {
        await using var live = await Live.StartAsync();
        var (monitor, client) = Watched(Endpoint(live.Fingerprint, live.Address), "not-a-live-token");
        using (client)
        {
            monitor.Report(ConnectionEvent.SignedOut);

            monitor.Report(ConnectionEvent.Unreachable);

            Assert.Equal(ConnectionState.SignedOut, monitor.State);
        }
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Within;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "It never happened.");
            await Task.Delay(5, Ct);
        }
    }

    /// <summary>Moves the clock a second at a time until something happens, with a real pause for the loop to catch up.</summary>
    private static async Task WaitWhileMovingAsync(FakeTimeProvider clock, Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Within;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "It never happened.");
            clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(30, Ct);
        }
    }

    // ---- Secrets ----

    [Fact]
    public async Task Token_NotInAnyUrl()
    {
        await using var fixture = new ServerFixture();
        await using var api = await FakeApi.ForEventsAsync(fixture.NewCertificate(), async context =>
        {
            await FakeApi.OpenEventsAsync(context);
        });
        using var client = NewClient(api, FingerprintOf(fixture));

        client.Get<PingReply>("ping");
        client.Send(HttpMethod.Put, "files/1/progress", new { position = 1 });
        await client.GetAsync<PingReply>("ping", Ct);
        await client.SendAsync(HttpMethod.Put, "files/1/watched", null, Ct);
        (await client.OpenStreamAsync(1, null, Ct)).Dispose();
        await client.EventsAsync(Ct).ToListAsync(Ct);

        Assert.Equal(6, api.Urls.Count);
        Assert.DoesNotContain(api.Urls, url => url.Contains(Token, StringComparison.Ordinal));
        Assert.All(api.Authorizations, header => Assert.Equal("Bearer " + Token, header));
    }

    [Fact]
    public async Task NoToken_SendsNoHeader()
    {
        await using var fixture = new ServerFixture();
        await using var api = await FakeApi.ForEventsAsync(fixture.NewCertificate(), FakeApi.HangAsync);
        using var client = new ApiClient(Endpoint(FingerprintOf(fixture), api.Address), () => null, _ => { });

        await client.GetAsync<PingReply>("ping", Ct);

        Assert.Equal([""], api.Authorizations);
    }

    [Fact]
    public async Task Token_NotInAnyException()
    {
        await using var fixture = new ServerFixture();
        using var client = new ApiClient(Endpoint(FingerprintOf(fixture), $"127.0.0.1:{ServerFixture.FreePort()}"), () => Token, _ => { });

        var thrown = await Assert.ThrowsAsync<ServerUnreachableException>(() => client.GetAsync<MeReply>("me", Ct));

        Assert.DoesNotContain(Token, thrown.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Protect_RoundTrips()
    {
        var blob = RemoteSession.Protect(Token);

        Assert.DoesNotContain(Token, blob, StringComparison.Ordinal);
        Assert.Equal(Token, RemoteSession.Unprotect(blob));
    }

    [Fact]
    public void StoredConnection_ToString_LeavesOutTheToken()
    {
        var blob = RemoteSession.Protect(Token);
        var stored = new StoredConnection(Endpoint(new string('a', 64), "192.0.2.1:47880"), "Sam", "Laptop", blob);

        var printed = new AppSettings { Connection = stored }.ToString();

        Assert.DoesNotContain(blob, printed, StringComparison.Ordinal);
        Assert.Contains("Sam", printed, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("blob")]
    [InlineData(null)]
    public void StoredConnection_SurvivesTheSettingsFile(string? token)
    {
        var dir = Path.Combine(Path.GetTempPath(), "mediaboard-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new SettingsStore(Path.Combine(dir, "settings.json"));
            var stored = new StoredConnection(Endpoint(new string('a', 64), "192.0.2.1:47880", "[2001:db8::1]:47880"), "Sam", "Laptop", token);

            store.Save(new AppSettings { Connection = stored });

            var loaded = store.Load().Connection!;
            Assert.Equal(stored.Endpoint.Addresses, loaded.Endpoint.Addresses);
            Assert.Equal(stored.Endpoint.Fingerprint, loaded.Endpoint.Fingerprint);
            Assert.Equal(stored.Endpoint.ServerName, loaded.Endpoint.ServerName);
            Assert.Equal(("Sam", "Laptop", token), (loaded.AccountName, loaded.DeviceName, loaded.ProtectedToken));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

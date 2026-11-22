#nullable enable
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using NSubstitute;
using Shiro.Http;
using Shiro.Services;
using Shiro.Tests.TestInfrastructure;
using Xunit;

namespace Shiro.Tests;

[Collection("SerialGlobalState")]
public sealed class Phase3HttpGatewayAndRpcTests : IDisposable
{
    private readonly string? _originalWebServerPort;
    private readonly string? _originalAllowExternal;

    public Phase3HttpGatewayAndRpcTests()
    {
        AppSettings.ActiveIniFile ??= new IniFile();
        _originalWebServerPort = AppSettings.WebServer.Get(AppSettingsKeys.WebServer.WebServerPort);
        _originalAllowExternal = AppSettings.WebServer.Get("AllowExternalConnections");
        WebAuthPolicy.ResetLockoutsForTesting();
    }

    public void Dispose()
    {
        WebAuthPolicy.ResetLockoutsForTesting();
        if (_originalWebServerPort == null)
            AppSettings.WebServer.RemoveProperty(AppSettingsKeys.WebServer.WebServerPort);
        else
            AppSettings.WebServer.Set(AppSettingsKeys.WebServer.WebServerPort, _originalWebServerPort);

        if (_originalAllowExternal == null)
            AppSettings.WebServer.RemoveProperty("AllowExternalConnections");
        else
            AppSettings.WebServer.Set("AllowExternalConnections", _originalAllowExternal);
    }

    [Fact]
    public async Task RestClient_VaryingUserAgents_ShareDefaultPool_AndDeleteSupportsBody()
    {
        using var clientA = new RestClient("https://example.com", "Phase3-Agent-A");
        using var clientB = new RestClient("https://example.com", "Phase3-Agent-B");

        FieldInfo? innerField = typeof(RestClient).GetField("_httpClient", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(innerField);

        var innerA = Assert.IsType<HttpClient>(innerField!.GetValue(clientA));
        var innerB = Assert.IsType<HttpClient>(innerField.GetValue(clientB));
        Assert.Same(SharedHttpClient.Get(), innerA);
        Assert.Same(innerA, innerB);

        FieldInfo? poolField = typeof(SharedHttpClient).GetField("Clients", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(poolField);
        var poolDict = Assert.IsType<ConcurrentDictionary<string, Lazy<HttpClient>>>(poolField!.GetValue(null));
        Assert.DoesNotContain(poolDict.Keys, k => k.StartsWith("ua_", StringComparison.OrdinalIgnoreCase));

        using var defaultUaMsg = clientB.CreateHttpRequestMessage(new RestRequest("v1/ping", Method.Get));
        Assert.Equal("Phase3-Agent-B", defaultUaMsg.Headers.UserAgent.ToString());

        var overrideReq = new RestRequest("v1/ping", Method.Get).AddHeader("User-Agent", "PerRequest-Override-UA");
        using var overrideUaMsg = clientB.CreateHttpRequestMessage(overrideReq);
        Assert.Equal("PerRequest-Override-UA", overrideUaMsg.Headers.UserAgent.ToString());

        Assert.True(RestClient.IsBodyMethod(Method.Delete));

        var deleteReq = new RestRequest("v1/items/42", Method.Delete)
            .AddJsonBody(new { reason = "cleanup" });
        using var deleteMsg = clientB.CreateHttpRequestMessage(deleteReq);
        Assert.Equal(HttpMethod.Delete, deleteMsg.Method);
        Assert.NotNull(deleteMsg.Content);
        string builtBody = await deleteMsg.Content!.ReadAsStringAsync();
        Assert.Contains("cleanup", builtBody, StringComparison.Ordinal);

        string? capturedWireBody = null;
        HttpMethod? capturedMethod = null;
        using var mockHttp = new HttpClient(new FakeHttpHandler(async (req, ct) =>
        {
            capturedMethod = req.Method;
            capturedWireBody = req.Content != null ? await req.Content.ReadAsStringAsync(ct) : null;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"deleted\":true}") };
        }));
        using var customClient = new RestClient(mockHttp, "https://example.com", "Phase3-Agent-B");
        var resp = await customClient.ExecuteAsync(deleteReq);

        Assert.True(resp.IsSuccessful);
        Assert.Equal(HttpMethod.Delete, capturedMethod);
        Assert.Equal("{\"reason\":\"cleanup\"}", capturedWireBody);
    }

    [Fact]
    public async Task RestClient_SerializeRequestBodyForSigning_SupportsContentTypePropertyAndFormParams()
    {
        using var rc = new RestClient("https://apis.roblox.com/");

        // 1. Explicit form ContentType via AddStringBody + AddParameter -> form parameters win consistently
        var formParamsReq = new RestRequest("v1/submit", Method.Post)
            .AddStringBody("{\"a\":1}", "application/x-www-form-urlencoded")
            .AddParameter("k", "v");

        string? signed1 = RestClient.SerializeRequestBodyForSigning(formParamsReq);
        using HttpRequestMessage msg1 = rc.CreateHttpRequestMessage(formParamsReq);
        string wire1 = await msg1.Content!.ReadAsStringAsync();
        Assert.Equal("k=v", signed1);
        Assert.Equal(wire1, signed1);

        // 2. Form ContentType via AddStringBody with zero Parameters -> StringBody sent & signed consistently
        var formStringReq = new RestRequest("v1/submit", Method.Post)
            .AddStringBody("raw=1&x=hello+world", "application/x-www-form-urlencoded");

        string? signed2 = RestClient.SerializeRequestBodyForSigning(formStringReq);
        using HttpRequestMessage msg2 = rc.CreateHttpRequestMessage(formStringReq);
        string wire2 = await msg2.Content!.ReadAsStringAsync();
        Assert.Equal("raw=1&x=hello+world", signed2);
        Assert.Equal(wire2, signed2);
        Assert.Equal("application/x-www-form-urlencoded", msg2.Content.Headers.ContentType?.MediaType);

        // 3. Form Content-Type header + StringBody with zero Parameters -> StringBody sent & signed consistently
        var headerFormStringReq = new RestRequest("v1/submit", Method.Post)
            .AddHeader("Content-Type", "application/x-www-form-urlencoded")
            .AddStringBody("alpha=10", "application/x-www-form-urlencoded");

        string? signed3 = RestClient.SerializeRequestBodyForSigning(headerFormStringReq);
        using HttpRequestMessage msg3 = rc.CreateHttpRequestMessage(headerFormStringReq);
        string wire3 = await msg3.Content!.ReadAsStringAsync();
        Assert.Equal("alpha=10", signed3);
        Assert.Equal(wire3, signed3);

        // 4. Non-body GET request with legacy Parameters goes to query string, not signed body
        var getReq = new RestRequest("v1/search", Method.Get).AddParameter("q", "shiro");
        Assert.Null(RestClient.SerializeRequestBodyForSigning(getReq));
        using HttpRequestMessage getMsg = rc.CreateHttpRequestMessage(getReq);
        Assert.Null(getMsg.Content);
        Assert.Equal("?q=shiro", getMsg.RequestUri!.Query);
    }

    [Fact]
    public async Task AccountApiDispatcher_SwaggerAndDocs_UseConfiguredPortOrRequestHostPort()
    {
        var dispatcher = AccountApiDispatcher.Instance;

        var hostPortOpenApi = await dispatcher.ProcessApiRequestAsync(new AccountApiDispatcher.ApiRequestContext
        {
            AbsolutePath = "/openapi.json",
            IsLocal = true,
            Host = "localhost:8123"
        });
        var spec1 = JsonNode.Parse(hostPortOpenApi)!.AsObject();
        Assert.Equal("http://localhost:8123", spec1["servers"]![0]!["url"]!.GetValue<string>());
        Assert.DoesNotContain("7963", hostPortOpenApi, StringComparison.Ordinal);

        var hostPortDocs = await dispatcher.ProcessApiRequestAsync(new AccountApiDispatcher.ApiRequestContext
        {
            AbsolutePath = "/docs",
            IsLocal = true,
            Host = "localhost:8123"
        });
        Assert.Contains("ws://localhost:8123/ws", hostPortDocs, StringComparison.Ordinal);
        Assert.DoesNotContain("7963", hostPortDocs, StringComparison.Ordinal);

        var ipv6OpenApi = await dispatcher.ProcessApiRequestAsync(new AccountApiDispatcher.ApiRequestContext
        {
            AbsolutePath = "/openapi.json",
            IsLocal = true,
            Host = "[::1]:8443"
        });
        var specIpv6 = JsonNode.Parse(ipv6OpenApi)!.AsObject();
        Assert.Equal("http://localhost:8443", specIpv6["servers"]![0]!["url"]!.GetValue<string>());

        AppSettings.WebServer.Set(AppSettingsKeys.WebServer.WebServerPort, "8234");
        var cfgOpenApi = await dispatcher.ProcessApiRequestAsync(new AccountApiDispatcher.ApiRequestContext
        {
            AbsolutePath = "/openapi.json",
            IsLocal = true,
            Host = "localhost"
        });
        var specCfg = JsonNode.Parse(cfgOpenApi)!.AsObject();
        Assert.Equal("http://localhost:8234", specCfg["servers"]![0]!["url"]!.GetValue<string>());

        var cfgDocs = await dispatcher.ProcessApiRequestAsync(new AccountApiDispatcher.ApiRequestContext
        {
            AbsolutePath = "/docs",
            IsLocal = true,
            Host = "localhost"
        });
        Assert.Contains("ws://localhost:8234/ws", cfgDocs, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AccountApiDispatcher_BodyRead_RespectsContextRequestAbortedToken()
    {
        var dispatcher = AccountApiDispatcher.Instance;
        using var cts = new CancellationTokenSource();
        using var bodyStream = new BlockingSensitiveBodyStream(Encoding.UTF8.GetBytes("SENSITIVE_SECRET_TOKEN"));

        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Request.Host = new HostString("localhost", 7963);
        context.Request.Path = "/Running";
        context.Request.ContentLength = 64;
        context.Request.Body = bodyStream;
        context.RequestAborted = cts.Token;

        Task<string> responseTask = dispatcher.SendKestrelResponseAsync(context);
        await bodyStream.ReadEntered.WaitAsync(TimeSpan.FromSeconds(2));
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => responseTask.WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.NotNull(bodyStream.CapturedPooledArray);
        Assert.All(bodyStream.CapturedPooledArray!, b => Assert.Equal(0, b));
    }

    [Fact]
    public void DiscordRpcService_Start_WhenClientIdEmpty_DoesNotSpawnReconnectWorkerLoop()
    {
        var logger = Substitute.For<IAppLogger>();
        var settings = Substitute.For<ISettingsService>();
        string currentClientId = string.Empty;
        settings.Get<bool>(AppSettingsKeys.General.DiscordRpcEnabled, true).Returns(true);
        settings.Get(AppSettingsKeys.General.DiscordRpcEnabled, true).Returns(true);
        settings.Get<string>(AppSettingsKeys.General.DiscordRpcClientId, Arg.Any<string>())
            .Returns(_ => currentClientId);

        FieldInfo? ctsField = typeof(DiscordRpcService).GetField("_cts", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(ctsField);

        using var service = new DiscordRpcService(logger, settings);
        Assert.True(service.IsEnabled);

        service.Start();
        service.UpdateCurrentPresence();

        Assert.Equal(0, service.WorkerGenerationForTest);
        Assert.Null(ctsField!.GetValue(service));
        Assert.False(service.IsConnected);
        Assert.True(service.IsEnabled);

        currentClientId = "   ";
        service.Start();
        Assert.Equal(0, service.WorkerGenerationForTest);
        Assert.Null(ctsField.GetValue(service));

        currentClientId = "123456789012345678";
        service.Start();
        Assert.Equal(1, service.WorkerGenerationForTest);
        Assert.NotNull(ctsField.GetValue(service));

        currentClientId = string.Empty;
        service.ApplyConfigurationChanged(enabled: true, reconnect: true);
        Assert.Null(ctsField.GetValue(service));
        Assert.False(service.IsConnected);
    }

    private sealed class BlockingSensitiveBodyStream(byte[] secretBytes) : Stream
    {
        private readonly SemaphoreSlim _readEntered = new(0, 1);

        public SemaphoreSlim ReadEntered => _readEntered;
        public byte[]? CapturedPooledArray { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (MemoryMarshal.TryGetArray<byte>(buffer, out ArraySegment<byte> segment) && segment.Array != null)
            {
                CapturedPooledArray = segment.Array;
                int copyCount = Math.Min(secretBytes.Length, segment.Count);
                Array.Copy(secretBytes, 0, segment.Array, segment.Offset, copyCount);
            }

            _readEntered.Release();
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

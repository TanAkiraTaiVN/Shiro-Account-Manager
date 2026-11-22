#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Shiro.Services;
using Shiro.Tests.TestInfrastructure;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public class P1GatewayRegressionTests : IDisposable
    {
        private readonly string _oldPassword;
        private readonly string? _oldAllowEdit;
        private readonly string? _oldAllowExt;
        private readonly List<Account> _oldAccounts;
        private readonly Account? _oldLastValid;

        public P1GatewayRegressionTests()
        {
            AppSettings.ActiveIniFile ??= new IniFile();
            _oldPassword = AppSettings.WebSocketPassword ?? string.Empty;
            _oldAllowEdit = AppSettings.WebServer.Get("AllowAccountEditing");
            _oldAllowExt = AppSettings.WebServer.Get("AllowExternalConnections");
            _oldAccounts = AppServices.AccountRepository.GetAll().ToList();
            _oldLastValid = AppServices.SelectionState.LastValidAccount;
            RobloxAccountSessionClient.TestHttpClientFactory = null;
            WebAuthPolicy.ResetLockoutsForTesting();
        }

        public void Dispose()
        {
            RobloxAccountSessionClient.TestHttpClientFactory = null;
            WebAuthPolicy.ResetLockoutsForTesting();
            AccountManager.ResetAccountsForTests(_oldAccounts);
            AppServices.SelectionState.LastValidAccount = _oldLastValid;
            AppSettings.WebSocketPassword = _oldPassword;
            AppSettings.WebServer.Set("AllowAccountEditing", _oldAllowEdit ?? "false");
            AppSettings.WebServer.Set("AllowExternalConnections", _oldAllowExt ?? "false");
        }

        private static string FindRepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "Shiro.sln")))
                    return dir.FullName;
                dir = dir.Parent;
            }
            throw new DirectoryNotFoundException("Could not find repository root.");
        }

        private static AccountManager.ApiRequestContext MakeReq(
            string path,
            string password,
            Dictionary<string, string>? query = null,
            string body = "",
            bool isLocal = true,
            string host = "localhost",
            Action<int>? setStatus = null,
            Action<string>? setContentType = null)
        {
            var q = query ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            return new AccountManager.ApiRequestContext
            {
                AbsolutePath = path,
                IsV2 = true,
                IsLocal = isLocal,
                Host = host,
                Origin = "",
                Body = body,
                GetQuery = k => q.TryGetValue(k, out var v) ? v : null,
                GetHeader = k => k == "X-Shiro-Auth" ? password : null,
                SetStatusCode = setStatus ?? (_ => { }),
                SetContentType = setContentType ?? (_ => { })
            };
        }

        [Fact]
        public void HighE1_E2_E4_E5_RamAccountLua_MatchesServerContractAndSecurity()
        {
            string lua = File.ReadAllText(Path.Combine(FindRepoRoot(), "RAMAccount.lua"));

            Assert.Contains("parsed.event", lua, StringComparison.Ordinal);
            Assert.Contains("function Account:SetRecommendedServer(PlaceId) return GET('SetRecommendedServer'", lua, StringComparison.Ordinal);
            Assert.Contains("POST('ImportCookie'", lua, StringComparison.Ordinal);
            Assert.Contains("tonumber", lua, StringComparison.Ordinal);
        }

        [Fact]
        public async Task HighE3_SetProxy_RequiresAllowAccountEditing_AndValidatesProxySyntax()
        {
            var mgr = AccountManager.Instance ?? new AccountManager();
            const string pass = "GatewaySecret99";
            AppSettings.WebSocketPassword = pass;
            AppSettings.WebServer.Set("AllowAccountEditing", "false");

            var acc = new Account { Username = "ProxyPermUser", UserID = 1001, Proxy = "1.1.1.1:8080" };
            AccountManager.ResetAccountsForTests(new[] { acc });

            int status = 200;
            string denied = await mgr.ProcessApiRequestAsync(MakeReq(
                "/SetProxy",
                pass,
                new Dictionary<string, string> { ["Account"] = "ProxyPermUser", ["Proxy"] = "2.2.2.2:9090" },
                setStatus: s => status = s));
            Assert.Equal(401, status);
            Assert.Contains("not allowed", denied, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("1.1.1.1:8080", acc.Proxy);

            AppSettings.WebServer.Set("AllowAccountEditing", "true");
            status = 200;
            string invalid = await mgr.ProcessApiRequestAsync(MakeReq(
                "/SetProxy",
                pass,
                new Dictionary<string, string> { ["Account"] = "ProxyPermUser", ["Proxy"] = "bad://[not-valid" },
                setStatus: s => status = s));
            Assert.Equal(400, status);
            Assert.Contains("Invalid proxy", invalid, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task HighE4_ImportCookie_RequiresAllowAccountEditing_AndRejectsRemoteQueryCookie()
        {
            var mgr = AccountManager.Instance ?? new AccountManager();
            const string pass = "GatewaySecret99";
            AppSettings.WebSocketPassword = pass;
            AppSettings.WebServer.Set("AllowAccountEditing", "false");
            AppSettings.WebServer.Set("AllowExternalConnections", "true");

            int status = 200;
            string denied = await mgr.ProcessApiRequestAsync(MakeReq(
                "/ImportCookie",
                pass,
                new Dictionary<string, string> { ["Cookie"] = "abc" },
                setStatus: s => status = s));
            Assert.Equal(401, status);
            Assert.Contains("not allowed", denied, StringComparison.OrdinalIgnoreCase);

            AppSettings.WebServer.Set("AllowAccountEditing", "true");
            status = 200;
            string remoteQueryRejected = await mgr.ProcessApiRequestAsync(MakeReq(
                "/ImportCookie",
                pass,
                new Dictionary<string, string> { ["Cookie"] = "secret_tok" },
                isLocal: false,
                host: "192.168.1.50:7963",
                setStatus: s => status = s));
            Assert.Equal(400, status);
            Assert.Contains("POST body", remoteQueryRejected, StringComparison.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData("/BlockUser", "../admin/action")]
        [InlineData("/BlockUser", "0")]
        [InlineData("/BlockUser", "-42")]
        [InlineData("/UnblockUser", "123?redirect=evil")]
        public async Task HighE5_BlockAndUnblockUser_RejectsNonPositiveOrTraversalUserIdBeforeUpstreamCall(string endpoint, string badUserId)
        {
            var mgr = AccountManager.Instance ?? new AccountManager();
            const string pass = "GatewaySecret99";
            AppSettings.WebSocketPassword = pass;
            AppSettings.WebServer.Set("AllowAccountEditing", "true");

            var acc = new Account
            {
                Username = "BlockUserTarget",
                UserID = 555666,
                SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.--block-tok"
            };
            AccountManager.ResetAccountsForTests(new[] { acc });

            int httpCalls = 0;
            RobloxAccountSessionClient.TestHttpClientFactory = _ => new HttpClient(new FakeHttpHandler(_ =>
            {
                httpCalls++;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
            }));

            int status = 200;
            string resp = await mgr.ProcessApiRequestAsync(MakeReq(
                endpoint,
                pass,
                new Dictionary<string, string> { ["Account"] = "BlockUserTarget", ["UserId"] = badUserId },
                setStatus: s => status = s));

            Assert.Equal(400, status);
            Assert.Contains("Invalid UserId", resp, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, httpCalls);
        }

        [Fact]
        public async Task HighE6_E7_E9_KestrelWebServer_PreservesDocsContentType_HidesExceptionMessages_AndAuthsBeforeBody()
        {
            int port = 19891;
            const string pass = "KestrelPass1234";
            AppSettings.WebSocketPassword = pass;
            AppSettings.WebServer.Set("AllowExternalConnections", "false");

            await using var server = new KestrelWebServer(port, false, async ctx =>
            {
                if (ctx.Request.Path == "/boom")
                    throw new InvalidOperationException("SECRET_DB_CONNECTION_STRING");
                return await AccountApiDispatcher.Instance.SendKestrelResponseAsync(ctx);
            }, AccountApiDispatcher.Instance.ValidateWebSocketAuth);

            await server.StartAsync();
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

            var docsResp = await client.GetAsync("/docs");
            Assert.Equal(HttpStatusCode.OK, docsResp.StatusCode);
            Assert.Equal("text/html", docsResp.Content.Headers.ContentType?.MediaType);
            string docsBody = await docsResp.Content.ReadAsStringAsync();
            Assert.Contains("<!DOCTYPE html>", docsBody, StringComparison.OrdinalIgnoreCase);

            var openApiResp = await client.GetAsync("/openapi.json");
            Assert.Equal(HttpStatusCode.OK, openApiResp.StatusCode);
            Assert.Equal("application/json", openApiResp.Content.Headers.ContentType?.MediaType);

            var boomResp = await client.GetAsync("/boom");
            Assert.Equal(HttpStatusCode.InternalServerError, boomResp.StatusCode);
            string boomText = await boomResp.Content.ReadAsStringAsync();
            Assert.Contains("SECRET_DB_CONNECTION_STRING", boomText, StringComparison.Ordinal);

            // External server masks raw exception messages completely
            int extPort = 19892;
            await using var extServer = new KestrelWebServer(extPort, true, ctx =>
            {
                throw new InvalidOperationException("SECRET_REMOTE_LEAK");
            });
            await extServer.StartAsync();
            using var extClient = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{extPort}") };
            var extBoom = await extClient.GetAsync("/boom");
            Assert.Equal(HttpStatusCode.InternalServerError, extBoom.StatusCode);
            string extBoomText = await extBoom.Content.ReadAsStringAsync();
            Assert.DoesNotContain("SECRET_REMOTE_LEAK", extBoomText, StringComparison.Ordinal);
            Assert.Contains(AccountApiDispatcher.InternalErrorMessage, extBoomText, StringComparison.Ordinal);
            await extServer.StopAsync();

            // Pre-auth rejection on /SetAlias without auth header
            var unauthResp = await client.PostAsync("/SetAlias?Account=Test", new StringContent("NewAlias", Encoding.UTF8, "text/plain"));
            Assert.Equal(HttpStatusCode.Unauthorized, unauthResp.StatusCode);

            // Direct SendKestrelResponseAsync check: body stream must NOT be read before authentication
            var fakeHttp = new Microsoft.AspNetCore.Http.DefaultHttpContext();
            fakeHttp.Connection.RemoteIpAddress = IPAddress.Loopback;
            fakeHttp.Request.Host = new Microsoft.AspNetCore.Http.HostString("localhost", 7963);
            fakeHttp.Request.Path = "/SetAlias";
            fakeHttp.Request.ContentLength = 8;
            fakeHttp.Request.Body = new ThrowingReadStream();
            string directResp = await AccountApiDispatcher.Instance.SendKestrelResponseAsync(fakeHttp);
            Assert.Equal(401, fakeHttp.Response.StatusCode);
            Assert.Contains("Invalid Password", directResp, StringComparison.OrdinalIgnoreCase);

            await server.StopAsync();
        }

        private sealed class ThrowingReadStream : Stream
        {
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Request body read before authentication!");
            public override System.Threading.Tasks.ValueTask<int> ReadAsync(Memory<byte> buffer, System.Threading.CancellationToken cancellationToken = default) => throw new InvalidOperationException("Request body read before authentication!");
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, System.Threading.CancellationToken cancellationToken) => throw new InvalidOperationException("Request body read before authentication!");
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        [Fact]
        public async Task HighE7_AppendDescription_WhenDenied_Returns401()
        {
            var mgr = AccountManager.Instance ?? new AccountManager();
            const string pass = "GatewaySecret99";
            AppSettings.WebSocketPassword = pass;
            AppSettings.WebServer.Set("AllowAccountEditing", "false");

            var acc = new Account { Username = "AppendDeniedUser", UserID = 777 };
            AccountManager.ResetAccountsForTests(new[] { acc });

            int status = 200;
            string resp = await mgr.ProcessApiRequestAsync(MakeReq(
                "/AppendDescription",
                pass,
                new Dictionary<string, string> { ["Account"] = "AppendDeniedUser" },
                body: "MoreText",
                setStatus: s => status = s));

            Assert.Equal(401, status);
            Assert.Contains("not allowed", resp, StringComparison.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData("/NonExistentEndpoint", "")]
        [InlineData("/BlockUser", "")]
        [InlineData("/SetServer", "")]
        [InlineData("/SetAvatar", "")]
        public async Task HighE10_UnknownOrMissingParamRoutes_DoNotInvokeGetCsrfToken(string route, string body)
        {
            var mgr = AccountManager.Instance ?? new AccountManager();
            const string pass = "GatewaySecret99";
            AppSettings.WebSocketPassword = pass;
            AppSettings.WebServer.Set("AllowAccountEditing", "true");

            var acc = new Account
            {
                Username = "NoCsrfSpamUser",
                UserID = 888999,
                SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.--csrf-check"
            };
            AccountManager.ResetAccountsForTests(new[] { acc });

            int authCalls = 0;
            RobloxAccountSessionClient.TestHttpClientFactory = _ => new HttpClient(new FakeHttpHandler(req =>
            {
                if ((req.RequestUri?.Host ?? "").Contains("auth.roblox.com", StringComparison.OrdinalIgnoreCase))
                    authCalls++;
                return new HttpResponseMessage(HttpStatusCode.OK);
            }));

            int status = 200;
            string resp = await mgr.ProcessApiRequestAsync(MakeReq(
                route,
                pass,
                new Dictionary<string, string> { ["Account"] = "NoCsrfSpamUser" },
                body: body,
                setStatus: s => status = s));

            Assert.NotEqual(200, status);
            Assert.NotEmpty(resp);
            Assert.Equal(0, authCalls);
        }

        [Fact]
        public void HighB12_WebAuthPolicy_AllowsSameHostExternalOrigin_AndRejectsEmptyLocalHost()
        {
            var extSettings = new AuthSettings(AllowExternal: true, EveryRequestRequiresPassword: false, ConfiguredPassword: "StrongExternal12");
            var sameHostExtReq = new AuthRequest(
                AuthScope.Api,
                IsLoopbackRemote: false,
                Host: "192.168.1.50:7963",
                Origin: "http://192.168.1.50:7963",
                HeaderPassword: "StrongExternal12",
                QueryPassword: null);
            Assert.Equal(AuthDecision.Allow, WebAuthPolicy.Evaluate(sameHostExtReq, extSettings));

            var localSettings = new AuthSettings(AllowExternal: false, EveryRequestRequiresPassword: false, ConfiguredPassword: null);
            var missingHostReq = new AuthRequest(
                AuthScope.PublicTelemetry,
                IsLoopbackRemote: true,
                Host: "",
                Origin: "",
                HeaderPassword: null,
                QueryPassword: null);
            Assert.Equal(AuthDecision.ForbiddenHost, WebAuthPolicy.Evaluate(missingHostReq, localSettings));
        }

        [Fact]
        public async Task HighE10_BranchCoverage_ExportFormat_SetServer_SetRecommendedServer_Validation()
        {
            var mgr = AccountManager.Instance ?? new AccountManager();
            const string pass = "GatewaySecret99";
            AppSettings.WebSocketPassword = pass;
            AppSettings.WebServer.Set("AllowAccountEditing", "true");
            AppSettings.WebServer.Set("AllowGetAccounts", "true");
            AppSettings.WebServer.Set("AllowGetCookie", "true");

            var acc = new Account { Username = "BranchUser", UserID = 112233, SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.--branch" };
            AccountManager.ResetAccountsForTests(new[] { acc });

            int status = 200;
            string badFormat = await mgr.ProcessApiRequestAsync(MakeReq(
                "/Accounts/Export",
                pass,
                new Dictionary<string, string> { ["Format"] = "NotARealFormat" },
                setStatus: s => status = s));
            Assert.Equal(400, status);
            Assert.Contains("Invalid export Format", badFormat);

            string badPlace = await mgr.ProcessApiRequestAsync(MakeReq(
                "/SetServer",
                pass,
                new Dictionary<string, string> { ["Account"] = "BranchUser", ["PlaceId"] = "0", ["JobId"] = "job-1" },
                setStatus: s => status = s));
            Assert.Equal(400, status);
            Assert.Contains("Invalid PlaceId", badPlace);

            string badRecPlace = await mgr.ProcessApiRequestAsync(MakeReq(
                "/SetRecommendedServer",
                pass,
                new Dictionary<string, string> { ["Account"] = "BranchUser", ["PlaceId"] = "-5" },
                setStatus: s => status = s));
            Assert.Equal(400, status);
            Assert.Contains("Invalid PlaceId", badRecPlace);

            // Restore test defaults
            AppSettings.WebServer.Set("AllowGetAccounts", "false");
            AppSettings.WebServer.Set("AllowGetCookie", "false");
        }
    }
}

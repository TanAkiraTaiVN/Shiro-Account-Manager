#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Shiro.Services;
using Shiro.Tests.TestInfrastructure;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public sealed class GatewayApiAndOpenApiTests : IDisposable
    {
        private const string EmptyJsonObject = "{}";
        private const string ValidAvatarJson = "{\"playerAvatarType\":\"R15\"}";
        private const string StrongExternalPass = "GatewayStrong12";
        private readonly string _oldPassword;
        private readonly string? _oldAllowEdit;
        private readonly string? _oldAllowExt;
        private readonly string? _oldAllowAccounts;
        private readonly string? _oldAllowCookie;
        private readonly string? _oldAllowLaunch;
        private readonly long _oldCachedPlaceId;
        private readonly List<Account> _oldAccounts;

        public GatewayApiAndOpenApiTests()
        {
            AppSettings.ActiveIniFile ??= new IniFile();
            _oldPassword = AppSettings.WebSocketPassword ?? string.Empty;
            _oldAllowEdit = AppSettings.WebServer.Get("AllowAccountEditing");
            _oldAllowExt = AppSettings.WebServer.Get("AllowExternalConnections");
            _oldAllowAccounts = AppSettings.WebServer.Get("AllowGetAccounts");
            _oldAllowCookie = AppSettings.WebServer.Get("AllowGetCookie");
            _oldAllowLaunch = AppSettings.WebServer.Get("AllowLaunchAccount");
            _oldCachedPlaceId = AppServices.ServerBrowserState.CachedServersPlaceId;
            _oldAccounts = AppServices.AccountRepository.GetAll().ToList();
            Account.TestHttpClientFactory = null;
            AccountLaunchService.TestGetAuthTicketOverride = null;
            RobloxDirectLauncher.ResetCookieGuardStateForTest();
            WebAuthPolicy.ResetLockoutsForTesting();
        }

        public void Dispose()
        {
            Account.TestHttpClientFactory = null;
            AccountLaunchService.TestGetAuthTicketOverride = null;
            AccountLaunchService.TestStartNativeGameOverride = null;
            RobloxDirectLauncher.ResetCookieGuardStateForTest();
            WebAuthPolicy.ResetLockoutsForTesting();
            AppServices.ServerBrowserState.CachedServers.Clear();
            AppServices.ServerBrowserState.CachedServersPlaceId = _oldCachedPlaceId;
            AccountManager.ResetAccountsForTests(_oldAccounts);
            AppSettings.WebSocketPassword = _oldPassword;
            AppSettings.WebServer.Set("AllowAccountEditing", _oldAllowEdit ?? "false");
            AppSettings.WebServer.Set("AllowExternalConnections", _oldAllowExt ?? "false");
            AppSettings.WebServer.Set("AllowGetAccounts", _oldAllowAccounts ?? "false");
            AppSettings.WebServer.Set("AllowGetCookie", _oldAllowCookie ?? "false");
            AppSettings.WebServer.Set("AllowLaunchAccount", _oldAllowLaunch ?? "false");
        }

        private static AccountManager.ApiRequestContext MakeReq(
            string path,
            string password,
            Dictionary<string, string>? query = null,
            string body = "",
            bool isLocal = true,
            bool isV2 = false,
            Action<int>? setStatus = null)
        {
            var q = query ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            return new AccountManager.ApiRequestContext
            {
                AbsolutePath = path,
                IsV2 = isV2,
                IsLocal = isLocal,
                Host = isLocal ? "localhost" : "192.168.1.50:7963",
                Origin = string.Empty,
                Body = body,
                GetQuery = k => q.TryGetValue(k, out var v) ? v : null,
                GetHeader = k => k == "X-Shiro-Auth" ? password : null,
                SetStatusCode = setStatus ?? (_ => { }),
                SetContentType = _ => { }
            };
        }

        [Theory]
        [InlineData("/v2/getaccounts/", true)]
        [InlineData("/V2/GetAccounts/", true)]
        [InlineData("/GETACCOUNTS/", false)]
        [InlineData("/v2/antiafk/status/", true)]
        public async Task Dispatcher_TrailingSlashCaseInsensitiveAndV2Routes_WorkUniformly(string rawPath, bool expectV2Envelope)
        {
            var mgr = AccountManager.Instance ?? new AccountManager();
            AppSettings.WebSocketPassword = StrongExternalPass;
            AppSettings.WebServer.Set("AllowGetAccounts", "true");
            var acc = new Account { Username = "RouteParityUser", UserID = 1002 };
            AccountManager.ResetAccountsForTests(new[] { acc });

            var http = new DefaultHttpContext();
            http.Connection.RemoteIpAddress = IPAddress.Loopback;
            http.Request.Host = new HostString("localhost", 7963);
            http.Request.Path = rawPath;
            http.Request.Headers["X-Shiro-Auth"] = StrongExternalPass;

            string response = await AccountApiDispatcher.Instance.SendKestrelResponseAsync(http);
            Assert.Equal(200, http.Response.StatusCode);
            if (expectV2Envelope)
            {
                using var doc = JsonDocument.Parse(response);
                Assert.True(doc.RootElement.GetProperty("Success").GetBoolean());
            }
            else
            {
                Assert.Equal("RouteParityUser", response);
            }
        }

        [Fact]
        public async Task Dispatcher_SetProxy_BlocksRemoteQueryProxy_AndAllowsRemotePostBody()
        {
            var mgr = AccountManager.Instance ?? new AccountManager();
            AppSettings.WebSocketPassword = StrongExternalPass;
            AppSettings.WebServer.Set("AllowExternalConnections", "true");
            AppSettings.WebServer.Set("AllowAccountEditing", "true");

            var acc = new Account { Username = "RemoteProxyUser", UserID = 2001, Proxy = "1.1.1.1:8080" };
            AccountManager.ResetAccountsForTests(new[] { acc });

            int queryStatus = 200;
            string rejected = await mgr.ProcessApiRequestAsync(MakeReq(
                "/SetProxy",
                StrongExternalPass,
                new Dictionary<string, string> { ["Account"] = "RemoteProxyUser", ["Proxy"] = "http://user:pass@2.2.2.2:9090" },
                isLocal: false,
                isV2: true,
                setStatus: s => queryStatus = s));
            Assert.Equal(400, queryStatus);
            Assert.Contains("POST body", rejected, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("1.1.1.1:8080", acc.Proxy);

            int bodyStatus = 0;
            string accepted = await mgr.ProcessApiRequestAsync(MakeReq(
                "/SetProxy",
                StrongExternalPass,
                new Dictionary<string, string> { ["Account"] = "RemoteProxyUser" },
                body: "http://3.3.3.3:8080",
                isLocal: false,
                isV2: true,
                setStatus: s => bodyStatus = s));
            Assert.Equal(200, bodyStatus);
            Assert.Contains("Proxy updated", accepted, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("http://3.3.3.3:8080", acc.Proxy);
        }

        [Fact]
        public async Task Dispatcher_SetRecommendedServer_ValidatesCachedServersPlaceId()
        {
            var mgr = AccountManager.Instance ?? new AccountManager();
            AppSettings.WebSocketPassword = StrongExternalPass;

            var acc = new Account
            {
                Username = "RecPlaceUser",
                UserID = 3001,
                Valid = true,
                SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.--rec-place"
            };
            AccountManager.ResetAccountsForTests(new[] { acc });

            int csrfCalls = 0;
            Account.TestHttpClientFactory = _ => new HttpClient(new FakeHttpHandler(req =>
            {
                string url = req.RequestUri?.ToString() ?? string.Empty;
                if (url.Contains("auth.roblox.com", StringComparison.OrdinalIgnoreCase))
                {
                    csrfCalls++;
                    var r = new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent(EmptyJsonObject) };
                    r.Headers.TryAddWithoutValidation("x-csrf-token", "tok-rec");
                    return r;
                }
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(EmptyJsonObject) };
            }));

            AppServices.ServerBrowserState.CachedServers.Clear();
            AppServices.ServerBrowserState.CachedServers.Add(new ServerData { id = "srv-other-game" });
            AppServices.ServerBrowserState.CachedServersPlaceId = 9999;

            int mismatchStatus = 200;
            string mismatchResp = await mgr.ProcessApiRequestAsync(MakeReq(
                "/SetRecommendedServer",
                StrongExternalPass,
                new Dictionary<string, string> { ["Account"] = "RecPlaceUser", ["PlaceId"] = "1818" },
                isV2: true,
                setStatus: s => mismatchStatus = s));
            Assert.Equal(400, mismatchStatus);
            Assert.Equal(0, csrfCalls);
            Assert.Contains("\"Success\":false", mismatchResp, StringComparison.Ordinal);

            AppServices.ServerBrowserState.CachedServersPlaceId = 1818;
            int matchStatus = 0;
            string matchResp = await mgr.ProcessApiRequestAsync(MakeReq(
                "/SetRecommendedServer",
                StrongExternalPass,
                new Dictionary<string, string> { ["Account"] = "RecPlaceUser", ["PlaceId"] = "1818" },
                isV2: true,
                setStatus: s => matchStatus = s));
            Assert.Equal(200, matchStatus);
            Assert.Contains("Success", matchResp, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Dispatcher_LaunchAccountAndFollowUser_EnterCookieGuardScope()
        {
            var mgr = AccountManager.Instance ?? new AccountManager();
            AppSettings.WebSocketPassword = StrongExternalPass;
            AppSettings.WebServer.Set("AllowLaunchAccount", "true");

            var acc = new Account
            {
                Username = "GuardedApiUser",
                UserID = 4001,
                Valid = true,
                SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.--guard"
            };
            var targetAcc = new Account { Username = "TargetFollow", UserID = 999111 };
            AccountManager.ResetAccountsForTests(new[] { acc, targetAcc });

            bool guardObservedOnLaunch = false;
            AccountLaunchService.TestGetAuthTicketOverride = (_, _) =>
            {
                guardObservedOnLaunch = RobloxDirectLauncher.IsCookieGuardHeld;
                return Task.FromResult((false, "stop-before-spawn"));
            };

            string launchResp = await mgr.ProcessApiRequestAsync(MakeReq(
                "/LaunchAccount",
                StrongExternalPass,
                new Dictionary<string, string> { ["Account"] = "GuardedApiUser", ["PlaceId"] = "1818" },
                isV2: true));
            Assert.True(guardObservedOnLaunch);
            Assert.Contains("\"Success\":false", launchResp);
            Assert.False(RobloxDirectLauncher.IsCookieGuardHeld);

            bool guardObservedOnFollow = false;
            AccountLaunchService.TestGetAuthTicketOverride = (_, _) =>
            {
                guardObservedOnFollow = RobloxDirectLauncher.IsCookieGuardHeld;
                return Task.FromResult((false, "stop-before-spawn"));
            };

            string followResp = await mgr.ProcessApiRequestAsync(MakeReq(
                "/FollowUser",
                StrongExternalPass,
                new Dictionary<string, string> { ["Account"] = "GuardedApiUser", ["Username"] = "TargetFollow" },
                isV2: true));
            Assert.True(guardObservedOnFollow);
            Assert.Contains("\"Success\":false", followResp);
            Assert.False(RobloxDirectLauncher.IsCookieGuardHeld);

            bool successGuardHeld = false;
            AccountLaunchService.TestGetAuthTicketOverride = (_, _) =>
            {
                successGuardHeld = RobloxDirectLauncher.IsCookieGuardHeld;
                return Task.FromResult((true, "ticket-ok"));
            };
            AccountLaunchService.TestStartNativeGameOverride = (_, _, _, _, _, _, _) => Task.FromResult(0);
            string okLaunch = await mgr.ProcessApiRequestAsync(MakeReq(
                "/LaunchAccount",
                StrongExternalPass,
                new Dictionary<string, string> { ["Account"] = "GuardedApiUser", ["PlaceId"] = "1818" },
                isV2: true));
            Assert.True(successGuardHeld);
            Assert.Contains("\"Success\":true", okLaunch);
            Assert.False(RobloxDirectLauncher.IsCookieGuardHeld);
        }

        [Fact]
        public async Task Dispatcher_SetAvatar_ReflectsTaskBoolResultAccurately()
        {
            var mgr = AccountManager.Instance ?? new AccountManager();
            AppSettings.WebSocketPassword = StrongExternalPass;

            var acc = new Account
            {
                Username = "AvatarResultUser",
                UserID = 5001,
                Valid = true,
                SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.--avatar"
            };
            AccountManager.ResetAccountsForTests(new[] { acc });

            bool returnAvatarSuccess = false;
            Account.TestHttpClientFactory = _ => new HttpClient(new FakeHttpHandler(req =>
            {
                string url = req.RequestUri?.ToString() ?? string.Empty;
                if (url.Contains("auth.roblox.com", StringComparison.OrdinalIgnoreCase))
                {
                    var csrf = new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent(EmptyJsonObject) };
                    csrf.Headers.TryAddWithoutValidation("x-csrf-token", "av-csrf-1");
                    return csrf;
                }
                return new HttpResponseMessage(returnAvatarSuccess ? HttpStatusCode.OK : HttpStatusCode.BadRequest)
                {
                    Content = new StringContent(EmptyJsonObject)
                };
            }));

            Assert.False(await RobloxSettingsApiService.Instance.SetAvatarAsync(acc, EmptyJsonObject));

            int emptyPayloadStatus = 200;
            string emptyPayloadResp = await mgr.ProcessApiRequestAsync(MakeReq(
                "/SetAvatar",
                StrongExternalPass,
                new Dictionary<string, string> { ["Account"] = "AvatarResultUser" },
                body: EmptyJsonObject,
                isV2: true,
                setStatus: s => emptyPayloadStatus = s));
            Assert.Equal(400, emptyPayloadStatus);
            Assert.Contains("\"Success\":false", emptyPayloadResp);

            int upstreamFailStatus = 200;
            string upstreamFailResp = await mgr.ProcessApiRequestAsync(MakeReq(
                "/SetAvatar",
                StrongExternalPass,
                new Dictionary<string, string> { ["Account"] = "AvatarResultUser" },
                body: ValidAvatarJson,
                isV2: true,
                setStatus: s => upstreamFailStatus = s));
            Assert.Equal(400, upstreamFailStatus);
            Assert.Contains("\"Success\":false", upstreamFailResp);

            returnAvatarSuccess = true;
            int okStatus = 0;
            string okResp = await mgr.ProcessApiRequestAsync(MakeReq(
                "/SetAvatar",
                StrongExternalPass,
                new Dictionary<string, string> { ["Account"] = "AvatarResultUser" },
                body: ValidAvatarJson,
                isV2: true,
                setStatus: s => okStatus = s));
            Assert.Equal(200, okStatus);
            Assert.Contains("\"Success\":true", okResp);
        }

        [Fact]
        public async Task ApiDocumentationService_MatchesActualV2AndLegacyDispatcherRoutes_WithoutFakeEndpoints()
        {
            string openApiJson = ApiDocumentationService.GetOpenApiJson(7999);
            var root = JsonNode.Parse(openApiJson) as JsonObject;
            Assert.NotNull(root);
            Assert.Equal("http://localhost:7999", root!["servers"]?[0]?["url"]?.ToString());
            Assert.NotNull(root["components"]?["securitySchemes"]);

            var paths = root["paths"] as JsonObject;
            Assert.NotNull(paths);
            Assert.True(paths!.Count >= 50, $"Expected comprehensive v2 + legacy route coverage, got {paths.Count}");
            Assert.True(paths.ContainsKey("/v2/GetAccounts"));
            Assert.True(paths.ContainsKey("/SetAvatar"));
            Assert.True(paths.ContainsKey("/v2/SetAvatar"));
            Assert.True(paths.ContainsKey("/v2/events"));

            var mgr = AccountManager.Instance ?? new AccountManager();
            AppSettings.WebSocketPassword = StrongExternalPass;
            AppSettings.WebServer.Set("AllowGetAccounts", "true");
            AppSettings.WebServer.Set("AllowGetCookie", "true");
            AppSettings.WebServer.Set("AllowAccountEditing", "true");
            var acc = new Account { Username = "DocParityUser", UserID = 6001 };
            AccountManager.ResetAccountsForTests(new[] { acc });
            Account.TestHttpClientFactory = _ => new HttpClient(new FakeHttpHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent(EmptyJsonObject) }));

            bool prevMultiRbx = AppSettings.General.Get<bool>("EnableMultiRbx");
            bool prevPotato = AppSettings.General.Get<bool>("EnablePotatoMode");
            bool prevAntiAfk = AntiAfkService.IsEnabled;
            bool prevAutoReconnect = AutoReconnectService.Instance.IsEnabled;
            try
            {
                foreach (var kvp in paths)
                {
                    string route = kvp.Key;
                    if (route is "/ws" or "/events" or "/v2/events")
                        continue;

                    var http = new DefaultHttpContext();
                    http.Connection.RemoteIpAddress = IPAddress.Loopback;
                    http.Request.Host = new HostString("localhost", 7963);
                    http.Request.Path = route;
                    http.Request.Headers["X-Shiro-Auth"] = StrongExternalPass;
                    http.Request.QueryString = new QueryString("?Account=DocParityUser");

                    await AccountApiDispatcher.Instance.SendKestrelResponseAsync(http);
                    Assert.NotEqual(404, http.Response.StatusCode);
                }
            }
            finally
            {
                AppSettings.General.Set("EnableMultiRbx", prevMultiRbx ? "true" : "false");
                AppSettings.General.Set("EnablePotatoMode", prevPotato ? "true" : "false");
                if (!prevAntiAfk) AntiAfkService.Stop();
                AutoReconnectService.Instance.IsEnabled = prevAutoReconnect;
                RobloxSingletonService.Apply(true);
            }
        }

        [Fact]
        public async Task AccountManager_ShutdownBackend_DisposesKestrelWebServerCleanly()
        {
            int freePort;
            using (var probe = new TcpListener(IPAddress.Loopback, 0))
            {
                probe.Start();
                freePort = ((IPEndPoint)probe.LocalEndpoint).Port;
            }

            var prevSuppress = AccountManager.SuppressExitOnShutdown;
            var prevPort = AppSettings.WebServer.Get("WebServerPort");
            var prevEnable = AppSettings.Developer.Get("EnableWebServer");
            AccountManager? prevInstance = AccountManager.Instance;
            AccountManager.SuppressExitOnShutdown = true;
            AccountManager? tempMgr = null;
            try
            {
                AppSettings.WebServer.Set("WebServerPort", freePort.ToString());
                AppSettings.Developer.Set("EnableWebServer", "true");
                tempMgr = new AccountManager();
                tempMgr.InitializeBackendServices();
                await Task.Delay(150);

                tempMgr.ShutdownBackend();

                using var rebindProbe = new TcpListener(IPAddress.Loopback, freePort);
                rebindProbe.Start();
                rebindProbe.Stop();
                Assert.True(tempMgr.IsDisposed);
            }
            finally
            {
                AccountManager.SuppressExitOnShutdown = prevSuppress;
                AppSettings.WebServer.Set("WebServerPort", prevPort ?? "7963");
                AppSettings.Developer.Set("EnableWebServer", prevEnable ?? "false");
                AccountManager.Instance = prevInstance;
                tempMgr?.Dispose();
                RobloxSingletonService.Apply(true);
            }
        }

        [Fact]
        public void RamAccountLua_IsTracked_PassesSelfUsernameToImportCookie_AndChecksExternalPasswordLength()
        {
            string root = ArchitectureTests.RepoRoot;
            string gitignore = File.ReadAllText(Path.Combine(root, ".gitignore"));
            Assert.DoesNotContain("/RAMAccount.lua", gitignore, StringComparison.Ordinal);

            string lua = File.ReadAllText(Path.Combine(root, "RAMAccount.lua"));
            Assert.Contains("POST('ImportCookie', self.Username,", lua, StringComparison.Ordinal);
            Assert.DoesNotContain("POST('ImportCookie', '',", lua, StringComparison.Ordinal);
            Assert.Contains("12", lua, StringComparison.Ordinal);
        }
    }
}

using System;
using System.Net;
using System.Threading.Tasks;
using Shiro;
using Shiro.Classes;
using Shiro.Nexus;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public class WebAuthPolicyTests
    {
        [Theory]
        // (scope, loopback, allowExt, everyReq, configured, header, query, expected)
        [InlineData(AuthScope.NexusControl, true, false, false, null, null, null, AuthDecision.Allow)] // Nexus local cũ vẫn chạy
        [InlineData(AuthScope.NexusControl, false, true, false, null, null, null, AuthDecision.PasswordNotConfigured)] // lỗ hổng chính
        [InlineData(AuthScope.NexusControl, false, true, false, "secret1", null, "secret1", AuthDecision.Allow)] // Lua dùng query
        [InlineData(AuthScope.NexusControl, false, true, false, "secret1", null, "wrong!", AuthDecision.InvalidPassword)]
        [InlineData(AuthScope.NexusControl, true, false, false, "secret1", null, null, AuthDecision.InvalidPassword)]
        [InlineData(AuthScope.Api, true, false, false, null, null, null, AuthDecision.PasswordNotConfigured)]
        [InlineData(AuthScope.Api, true, false, false, "secret1", null, "secret1", AuthDecision.Allow)] // local được dùng query
        [InlineData(AuthScope.Api, false, true, false, "secret1", null, "secret1", AuthDecision.InvalidPassword)] // Api_External_QueryPasswordRejected
        [InlineData(AuthScope.Api, false, true, false, "secret1", "secret1", null, AuthDecision.Allow)]
        [InlineData(AuthScope.Api, false, false, false, "secret1", "secret1", null, AuthDecision.ForbiddenExternal)]
        [InlineData(AuthScope.PublicTelemetry, true, false, false, null, null, null, AuthDecision.Allow)]
        [InlineData(AuthScope.PublicTelemetry, true, false, true, "secret1", null, null, AuthDecision.InvalidPassword)]
        [InlineData(AuthScope.PublicTelemetry, false, true, false, "secret1", null, null, AuthDecision.InvalidPassword)]
        public void Evaluate_DecisionMatrix_MatchesExpected(
            AuthScope scope,
            bool loopback,
            bool allowExt,
            bool everyReq,
            string? configured,
            string? header,
            string? query,
            AuthDecision expected)
        {
            var settings = new AuthSettings(allowExt, everyReq, configured);
            var request = new AuthRequest(scope, loopback, "localhost", "http://localhost", header, query);

            var decision = WebAuthPolicy.Evaluate(request, settings);
            Assert.Equal(expected, decision);
        }

        [Fact]
        public void NexusAuth_ExternalWithShortPassword_IsRejected()
        {
            var settings = new AuthSettings(AllowExternal: true, EveryRequestRequiresPassword: false, ConfiguredPassword: "abc");
            var request = new AuthRequest(AuthScope.NexusControl, IsLoopbackRemote: false, "192.168.1.5:5242", null, "abc", null);
            Assert.NotEqual(AuthDecision.Allow, WebAuthPolicy.Evaluate(request, settings));
        }

        [Fact]
        public void Evaluate_LocalOnly_RejectsForeignOrigin()
        {
            var settings = new AuthSettings(AllowExternal: false, EveryRequestRequiresPassword: false, ConfiguredPassword: null);
            var request = new AuthRequest(
                AuthScope.PublicTelemetry,
                IsLoopbackRemote: true,
                Host: "localhost",
                Origin: "https://evil.example",
                HeaderPassword: null,
                QueryPassword: null);

            var decision = WebAuthPolicy.Evaluate(request, settings);
            Assert.Equal(AuthDecision.ForbiddenOrigin, decision);
        }

        [Fact]
        public void Evaluate_LocalOnly_RejectsForeignHost()
        {
            var settings = new AuthSettings(AllowExternal: false, EveryRequestRequiresPassword: false, ConfiguredPassword: null);
            var request = new AuthRequest(
                AuthScope.PublicTelemetry,
                IsLoopbackRemote: true,
                Host: "192.168.1.5",
                Origin: "http://localhost",
                HeaderPassword: null,
                QueryPassword: null);

            var decision = WebAuthPolicy.Evaluate(request, settings);
            Assert.Equal(AuthDecision.ForbiddenHost, decision);
        }

        [Fact]
        public void CanListenExternally_RequiresConfiguredPassword()
        {
            Assert.False(WebAuthPolicy.CanListenExternally(new AuthSettings(true, false, null)));
            Assert.False(WebAuthPolicy.CanListenExternally(new AuthSettings(true, false, "12345")));
            Assert.True(WebAuthPolicy.CanListenExternally(new AuthSettings(true, false, "123456")));
            Assert.False(WebAuthPolicy.CanListenExternally(new AuthSettings(false, false, "123456")));
        }

        [Fact]
        public void IsPasswordConfigured_ValidatesCorrectly()
        {
            Assert.False(WebAuthPolicy.IsPasswordConfigured(null));
            Assert.False(WebAuthPolicy.IsPasswordConfigured(""));
            Assert.False(WebAuthPolicy.IsPasswordConfigured("12345"));
            Assert.True(WebAuthPolicy.IsPasswordConfigured("123456"));
            Assert.True(WebAuthPolicy.IsPasswordConfigured("longerpassword"));
            Assert.False(WebAuthPolicy.IsStrongExternalPassword(null));
            Assert.False(WebAuthPolicy.IsStrongExternalPassword("123456"));
            Assert.False(WebAuthPolicy.IsStrongExternalPassword("12345678901"));
            Assert.True(WebAuthPolicy.IsStrongExternalPassword("123456789012"));
        }

        [Fact]
        public void RedactSensitiveQuery_MasksPasswordsAndTokens()
        {
            string raw = "http://localhost:7963/Nexus?name=Player1&Password=MySecret123&token=Tok456&Cookie=_|WARNING";
            string redacted = WebAuthPolicy.RedactSensitiveQuery(raw);
            Assert.DoesNotContain("MySecret123", redacted);
            Assert.DoesNotContain("Tok456", redacted);
            Assert.DoesNotContain("_|WARNING", redacted);
            Assert.Contains("Password=[REDACTED]", redacted);
            Assert.Contains("token=[REDACTED]", redacted);
            Assert.Contains("Cookie=[REDACTED]", redacted);
            Assert.Equal(string.Empty, WebAuthPolicy.RedactSensitiveQuery(null));
        }

        [Fact]
        public void FixedTimeEqualsPassword_HandlesShortAndLongPasswordsCorrectly()
        {
            Assert.False(CryptoHelper.FixedTimeEqualsSecret(null!, "secret"));
            Assert.False(CryptoHelper.FixedTimeEqualsSecret("secret", null!));
            Assert.True(CryptoHelper.FixedTimeEqualsSecret("secret123", "secret123"));
            Assert.False(CryptoHelper.FixedTimeEqualsSecret("secret123", "secret1234"));

            string longPassA = new string('X', 300);
            string longPassB = new string('X', 300);
            string longPassC = new string('X', 299) + "Y";
            Assert.True(CryptoHelper.FixedTimeEqualsSecret(longPassA, longPassB));
            Assert.False(CryptoHelper.FixedTimeEqualsSecret(longPassA, longPassC));
        }

        [Fact]
        public void ToStatusCode_MapsDecisionsAppropriately()
        {
            Assert.Equal(200, WebAuthPolicy.ToStatusCode(AuthDecision.Allow));
            Assert.Equal(403, WebAuthPolicy.ToStatusCode(AuthDecision.ForbiddenExternal));
            Assert.Equal(403, WebAuthPolicy.ToStatusCode(AuthDecision.ForbiddenHost));
            Assert.Equal(403, WebAuthPolicy.ToStatusCode(AuthDecision.ForbiddenOrigin));
            Assert.Equal(401, WebAuthPolicy.ToStatusCode(AuthDecision.PasswordNotConfigured));
            Assert.Equal(401, WebAuthPolicy.ToStatusCode(AuthDecision.InvalidPassword));
        }

        [Fact]
        public void ExtractPassword_Helpers_SeparateHeadersAndQuery()
        {
            Assert.Equal("hdr-sh", WebAuthPolicy.ExtractHeaderPassword(k => k == "X-Shiro-Auth" ? "hdr-sh" : null));
            Assert.Equal("hdr-ram", WebAuthPolicy.ExtractHeaderPassword(k => k == "X-RAM-Auth" ? "hdr-ram" : null));
            Assert.Equal("hdr-bearer", WebAuthPolicy.ExtractHeaderPassword(k => k == "Authorization" ? "Bearer hdr-bearer" : null));
            Assert.Null(WebAuthPolicy.ExtractHeaderPassword(_ => null));

            Assert.Equal("query-pass", WebAuthPolicy.ExtractQueryPassword(k => k == "Password" ? "query-pass" : null));
            Assert.Equal("query-pass2", WebAuthPolicy.ExtractQueryPassword(k => k == "password" ? "query-pass2" : null));
            Assert.Equal("query-tok", WebAuthPolicy.ExtractQueryPassword(k => k == "token" ? "query-tok" : null));
            Assert.Null(WebAuthPolicy.ExtractQueryPassword(_ => null));

            // Legacy ExtractRequestPassword wrapper still works for both
            Assert.Equal("from-h", WebAuthPolicy.ExtractRequestPassword(k => k == "X-Shiro-Auth" ? "from-h" : null, _ => "from-q"));
            Assert.Equal("from-q", WebAuthPolicy.ExtractRequestPassword(_ => null, k => k == "Password" ? "from-q" : null));
            Assert.Equal(string.Empty, WebAuthPolicy.ExtractRequestPassword(_ => null, _ => null));
        }

        [Fact]
        public async Task KestrelWebServer_ExternalWithoutPassword_FallsBackToLocalhost()
        {
            string oldPass = AppSettings.WebSocketPassword ?? string.Empty;
            try
            {
                AppSettings.WebSocketPassword = "";
                int testPort = 27981;
                var server = new KestrelWebServer(testPort, allowExternal: true, async ctx =>
                {
                    await Task.Yield();
                    return "ok";
                });

                await server.StartAsync();
                try
                {
                    Assert.False(server.IsListeningExternally);
                }
                finally
                {
                    await server.StopAsync();
                }
            }
            finally
            {
                AppSettings.WebSocketPassword = oldPass;
            }
        }

        [Fact]
        public async Task NexusWebSocketServer_ExternalWithoutPassword_FallsBackToLocalhost()
        {
            string oldPass = AppSettings.WebSocketPassword ?? string.Empty;
            try
            {
                AppSettings.WebSocketPassword = "";
                int testPort = 27982;
                var server = new NexusWebSocketServer(IPAddress.Any, testPort);

                await server.StartAsync();
                try
                {
                    Assert.False(server.IsListeningExternally);
                }
                finally
                {
                    await server.StopAsync();
                }
            }
            finally
            {
                AppSettings.WebSocketPassword = oldPass;
            }
        }

        [Fact]
        public async Task ProcessApiRequestAsync_ExternalQueryPassword_IsRejected()
        {
            var mgr = AccountManager.Instance ?? new AccountManager();


            string oldPass = AppSettings.WebSocketPassword ?? string.Empty;
            string? oldAllow = AppSettings.WebServer.Get("AllowExternalConnections");
            try
            {
                AppSettings.WebSocketPassword = "ValidPassword123";
                AppSettings.WebServer.Set("AllowExternalConnections", "true");

                int statusCode = 200;
                var ctx = new AccountManager.ApiRequestContext
                {
                    AbsolutePath = "/LaunchAccount",
                    IsV2 = true,
                    IsLocal = false, // External LAN
                    Host = "192.168.1.10",
                    Origin = "",
                    GetQuery = k => k == "Password" ? "ValidPassword123" : null, // Query password NOT accepted for external API
                    GetHeader = _ => null,
                    SetStatusCode = c => statusCode = c
                };

                string resp = await mgr.ProcessApiRequestAsync(ctx);
                Assert.Equal(401, statusCode);
                Assert.Contains("Invalid Password", resp, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                AppSettings.WebServer.Set("AllowExternalConnections", oldAllow);
                AppSettings.WebSocketPassword = oldPass;
            }
        }

        [Fact]
        public async Task ProcessApiRequestAsync_ExternalHeaderPassword_IsAccepted()
        {
            var mgr = AccountManager.Instance ?? new AccountManager();


            string oldPass = AppSettings.WebSocketPassword ?? string.Empty;
            string? oldAllow = AppSettings.WebServer.Get("AllowExternalConnections");
            try
            {
                AppSettings.WebSocketPassword = "ValidPassword123";
                AppSettings.WebServer.Set("AllowExternalConnections", "true");

                int statusCode = 200;
                var ctx = new AccountManager.ApiRequestContext
                {
                    AbsolutePath = "/Running",
                    IsV2 = true,
                    IsLocal = false, // External LAN
                    Host = "192.168.1.10",
                    Origin = "",
                    GetQuery = _ => null,
                    GetHeader = k => k == "X-Shiro-Auth" ? "ValidPassword123" : null,
                    SetStatusCode = c => statusCode = c
                };

                string resp = await mgr.ProcessApiRequestAsync(ctx);
                Assert.Equal(200, statusCode);
                Assert.Contains("Shiro is running", resp, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                AppSettings.WebServer.Set("AllowExternalConnections", oldAllow);
                AppSettings.WebSocketPassword = oldPass;
            }
        }

        [Theory]
        [InlineData("/running")]
        [InlineData("/RUNNING")]
        [InlineData("/Running")]
        public async Task ProcessApiRequestAsync_RunningEndpoint_IsCaseInsensitive(string path)
        {
            var mgr = new AccountManager();
            int statusCode = 200;
            var ctx = new AccountManager.ApiRequestContext
            {
                AbsolutePath = path,
                IsV2 = true,
                IsLocal = true,
                Host = "localhost",
                Origin = "",
                GetQuery = _ => null,
                GetHeader = _ => null,
                SetStatusCode = c => statusCode = c
            };

            string resp = await mgr.ProcessApiRequestAsync(ctx);
            Assert.Equal(200, statusCode);
            Assert.Contains("Shiro is running", resp, StringComparison.OrdinalIgnoreCase);
        }
    }
}

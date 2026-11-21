#nullable enable
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Shiro.Services;
using Shiro.Tests.TestInfrastructure;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public sealed class Phase1GatewayHardeningTests : IDisposable
    {
        private readonly string _prevPassword;
        private readonly bool _prevAllowEdit;
        private readonly bool _prevAllowGetCookie;
        private readonly IReadOnlyList<Account> _prevAccounts;
        private readonly Func<Account, HttpClient>? _prevHttpFactory;

        public Phase1GatewayHardeningTests()
        {
            AppSettings.ActiveIniFile ??= new IniFile();
            _ = AccountManager.Instance ?? new AccountManager();
            _prevPassword = AppSettings.WebSocketPassword ?? string.Empty;
            _prevAllowEdit = AppSettings.WebServer.Get<bool>("AllowAccountEditing");
            _prevAllowGetCookie = AppSettings.WebServer.Get<bool>("AllowGetCookie");
            _prevAccounts = AppServices.AccountRepository.GetAll();
            _prevHttpFactory = RobloxAccountSessionClient.TestHttpClientFactory;
        }

        public void Dispose()
        {
            AppSettings.WebSocketPassword = _prevPassword;
            AppSettings.WebServer.Set("AllowAccountEditing", _prevAllowEdit ? "true" : "false");
            AppSettings.WebServer.Set("AllowGetCookie", _prevAllowGetCookie ? "true" : "false");
            AccountManager.ResetAccountsForTests(_prevAccounts);
            RobloxAccountSessionClient.TestHttpClientFactory = _prevHttpFactory;
        }

        private static AccountManager.ApiRequestContext CreateReq(
            string path,
            string password,
            Dictionary<string, string> query,
            Action<int> setStatus,
            string body = "")
        {
            query["Password"] = password;
            return new AccountManager.ApiRequestContext
            {
                AbsolutePath = path,
                IsV2 = true,
                IsLocal = true,
                Host = "localhost",
                Origin = string.Empty,
                Body = body,
                GetQuery = k => query.TryGetValue(k, out string? v) ? v : null,
                GetHeader = _ => null,
                SetStatusCode = setStatus
            };
        }

        [Theory]
        [InlineData("/BlockUser", "12345", "", "", "")]
        [InlineData("/UnblockUser", "12345", "", "", "")]
        [InlineData("/UnblockEveryone", "", "", "", "")]
        [InlineData("/SetServer", "", "1818", "job-1", "")]
        [InlineData("/SetRecommendedServer", "", "1818", "", "")]
        [InlineData("/SetAvatar", "", "", "", "{\"playerAvatarType\":\"R15\"}")]
        public async Task MutatingEndpoints_Deny401_WhenAllowAccountEditingIsFalse(
            string path, string userId, string placeId, string jobId, string body)
        {
            const string pass = "Phase1GatePass12";
            var mgr = AccountManager.Instance ?? new AccountManager();
            AppSettings.WebSocketPassword = pass;
            AppSettings.WebServer.Set("AllowAccountEditing", "false");
            var acc = new Account { Username = "GateUser", UserID = 5001, SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.--tok", Valid = true };
            AccountManager.ResetAccountsForTests(new[] { acc });

            int httpCalls = 0;
            RobloxAccountSessionClient.TestHttpClientFactory = _ => new HttpClient(new FakeHttpHandler(_ =>
            {
                httpCalls++;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
            }));

            var q = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Account"] = "GateUser" };
            if (!string.IsNullOrEmpty(userId)) q["UserId"] = userId;
            if (!string.IsNullOrEmpty(placeId)) q["PlaceId"] = placeId;
            if (!string.IsNullOrEmpty(jobId)) q["JobId"] = jobId;

            int status = 200;
            string resp = await mgr.ProcessApiRequestAsync(CreateReq(path, pass, q, s => status = s, body));

            Assert.Equal(401, status);
            Assert.Contains("not allowed", resp, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, httpCalls);
        }

        [Fact]
        public async Task GetProxy_RedactsCredentialsUnlessAllowGetCookieIsEnabled()
        {
            const string pass = "Phase1GatePass12";
            var mgr = AccountManager.Instance ?? new AccountManager();
            AppSettings.WebSocketPassword = pass;
            var acc = new Account { Username = "ProxyUser", UserID = 5002, Proxy = "http://alice:s3cr3t@1.2.3.4:8080" };
            AccountManager.ResetAccountsForTests(new[] { acc });

            AppSettings.WebServer.Set("AllowGetCookie", "false");
            int redactedStatus = 200;
            string redacted = await mgr.ProcessApiRequestAsync(CreateReq(
                "/GetProxy", pass, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Account"] = "ProxyUser" }, s => redactedStatus = s));
            Assert.Equal(200, redactedStatus);
            Assert.Contains("http://1.2.3.4:8080", redacted, StringComparison.Ordinal);
            Assert.DoesNotContain("alice", redacted, StringComparison.Ordinal);
            Assert.DoesNotContain("s3cr3t", redacted, StringComparison.Ordinal);

            AppSettings.WebServer.Set("AllowGetCookie", "true");
            int fullStatus = 200;
            string full = await mgr.ProcessApiRequestAsync(CreateReq(
                "/GetProxy", pass, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Account"] = "ProxyUser" }, s => fullStatus = s));
            Assert.Equal(200, fullStatus);
            Assert.Contains("http://alice:s3cr3t@1.2.3.4:8080", full, StringComparison.Ordinal);
        }

        [Fact]
        public async Task SetAlias_SetDescription_AppendDescription_RejectOversizedPayloadsWith400()
        {
            const string pass = "Phase1GatePass12";
            var mgr = AccountManager.Instance ?? new AccountManager();
            AppSettings.WebSocketPassword = pass;
            AppSettings.WebServer.Set("AllowAccountEditing", "true");
            var acc = new Account { Username = "LenUser", UserID = 5003, Alias = "Orig", Description = new string('a', 4995) };
            AccountManager.ResetAccountsForTests(new[] { acc });

            int s1 = 200;
            string r1 = await mgr.ProcessApiRequestAsync(CreateReq(
                "/SetAlias", pass, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Account"] = "LenUser" }, s => s1 = s, new string('x', 51)));
            Assert.Equal(400, s1);
            Assert.Contains("50", r1, StringComparison.Ordinal);
            Assert.Equal("Orig", acc.Alias);

            int s2 = 200;
            string r2 = await mgr.ProcessApiRequestAsync(CreateReq(
                "/SetDescription", pass, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Account"] = "LenUser" }, s => s2 = s, new string('y', 5001)));
            Assert.Equal(400, s2);
            Assert.Contains("5000", r2, StringComparison.Ordinal);
            Assert.Equal(4995, acc.Description.Length);

            int s3 = 200;
            string r3 = await mgr.ProcessApiRequestAsync(CreateReq(
                "/AppendDescription", pass, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Account"] = "LenUser" }, s => s3 = s, new string('z', 10)));
            Assert.Equal(400, s3);
            Assert.Contains("5000", r3, StringComparison.Ordinal);
            Assert.Equal(4995, acc.Description.Length);
        }
    }
}

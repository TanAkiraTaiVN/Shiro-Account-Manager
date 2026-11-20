#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public class BugHuntNetworkRegressionTests : IDisposable
    {
        private sealed class DelegateHttpHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _responder;

            public DelegateHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
            {
                _responder = responder;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                _responder(request, cancellationToken);
        }

        private readonly Func<Account, HttpClient>? _origAccountHttpFactory;
        private readonly Func<string, string, string, string, string?, string?, CancellationToken, Task<int>>? _origLaunchOverride;
        private readonly Func<Account, CancellationToken, Task<(bool Success, string Ticket)>>? _origTicketOverride;
        private readonly Func<bool>? _origPrivacyConfirmOverride;
        private readonly Func<string, Task<int?>>? _origPingProbe;
        private readonly Func<string, CancellationToken, Task<string>>? _origGeoGet;

        public BugHuntNetworkRegressionTests()
        {
            _origAccountHttpFactory = RobloxAccountSessionClient.TestHttpClientFactory;
            _origLaunchOverride = AccountLaunchService.TestStartNativeGameOverride;
            _origTicketOverride = AccountLaunchService.TestGetAuthTicketOverride;
            _origPrivacyConfirmOverride = AccountLaunchService.TestPrivacyConfirmOverride;
            _origPingProbe = RobloxServerQueryService.TestPingProbe;
            _origGeoGet = RobloxServerQueryService.TestGeoHttpGetString;
        }

        public void Dispose()
        {
            RobloxAccountSessionClient.TestHttpClientFactory = _origAccountHttpFactory;
            AccountLaunchService.TestStartNativeGameOverride = _origLaunchOverride;
            AccountLaunchService.TestGetAuthTicketOverride = _origTicketOverride;
            AccountLaunchService.TestPrivacyConfirmOverride = _origPrivacyConfirmOverride;
            RobloxServerQueryService.TestPingProbe = _origPingProbe;
            RobloxServerQueryService.TestGeoHttpGetString = _origGeoGet;
        }

        [Fact]
        public async Task AccountLaunchService_VipPrivacyUpdate_RetriesOnceOnForbiddenCsrfChallenge()
        {
            using var suppressSave = Account.BeginSuppressDeferredSaveScope();
            var account = new Account
            {
                Username = "VipRetryUser",
                UserID = 123456,
                SecurityToken = "vip_sec_token",
                CSRFToken = "stale_csrf",
                Valid = true
            };

            var sentCsrfs = new List<string>();
            RobloxAccountSessionClient.TestHttpClientFactory = _ => new HttpClient(new DelegateHttpHandler((req, _) =>
            {
                string path = req.RequestUri?.AbsolutePath ?? string.Empty;
                if (path.EndsWith("/user-settings", StringComparison.OrdinalIgnoreCase) && req.Method == HttpMethod.Get)
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"whoCanInviteMeToPrivateServers\":\"Friends\"}")
                    });
                }

                if (path.EndsWith("/user-settings", StringComparison.OrdinalIgnoreCase) && req.Method == HttpMethod.Post)
                {
                    string csrf = req.Headers.TryGetValues("X-CSRF-TOKEN", out var vals) ? (vals.FirstOrDefault() ?? "") : "";
                    sentCsrfs.Add(csrf);
                    if (csrf == "stale_csrf")
                    {
                        var challenge = new HttpResponseMessage(HttpStatusCode.Forbidden);
                        challenge.Headers.TryAddWithoutValidation("x-csrf-token", "fresh_csrf_999");
                        return Task.FromResult(challenge);
                    }

                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{}")
                    });
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
            }));

            AccountLaunchService.TestGetAuthTicketOverride = (_, _) => Task.FromResult((true, "vip-ticket-1"));
            AccountLaunchService.TestPrivacyConfirmOverride = () => true;
            AccountLaunchService.TestStartNativeGameOverride = (_, _, _, _, _, _, _) => Task.FromResult(4242);

            var launchResult = await AccountLaunchService.Instance.JoinServerResultAsync(
                account,
                placeId: 1818,
                jobId: "access-code-1",
                joinVip: true);

            Assert.Equal(LaunchOutcome.Success, launchResult.Outcome);
            Assert.Equal(new[] { "stale_csrf", "fresh_csrf_999" }, sentCsrfs);
            Assert.Equal("fresh_csrf_999", account.CSRFToken);
        }

        [Fact]
        public async Task RobloxServerQueryService_GeoIpPrimaryHttpError_FallsBackToIpWhoIs()
        {
            using var suppressSave = Account.BeginSuppressDeferredSaveScope();
            const string testIp = "203.0.113.199";
            Assert.False(RobloxDatacenterService.Default.TryResolve(testIp, null, out _));

            var account = new Account { Username = "GeoFallbackUser", SecurityToken = "geo_tok", Valid = true };
            RobloxAccountSessionClient.TestHttpClientFactory = _ => new HttpClient(new DelegateHttpHandler((_, _) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($"{{\"joinScript\":{{\"MachineAddress\":\"{testIp}\"}}}}")
                })));

            var queriedUrls = new List<string>();
            RobloxServerQueryService.TestPingProbe = _ => Task.FromResult<int?>(33);
            RobloxServerQueryService.TestGeoHttpGetString = (url, _) =>
            {
                queriedUrls.Add(url);
                if (url.Contains("custom-geo.invalid", StringComparison.OrdinalIgnoreCase))
                    throw new HttpRequestException("Simulated 503 error", null, HttpStatusCode.ServiceUnavailable);

                return Task.FromResult("{\"success\":true,\"country_code\":\"JP\",\"city\":\"Tokyo\"}");
            };

            var result = await RobloxServerQueryService.Default.QueryServerRegionAndPingAsync(
                account,
                placeId: 1818,
                serverId: "server-abc",
                ipApiTemplate: "https://custom-geo.invalid/<ip>");

            Assert.Contains(queriedUrls, u => u.Contains("custom-geo.invalid", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(queriedUrls, u => u.StartsWith($"https://ipwho.is/{testIp}", StringComparison.OrdinalIgnoreCase));
            Assert.Equal("Tokyo, JP", result.Region);
            Assert.Equal("JP", result.CountryCode);
        }

        [Fact]
        public async Task Account_GetAuthTicketAsync_ForbiddenWithoutCsrfHeaderAndStaleCachedToken_RefreshesCsrfAndSucceeds()
        {
            using var suppressSave = Account.BeginSuppressDeferredSaveScope();
            var account = new Account
            {
                Username = "TicketCsrfFallbackUser",
                UserID = 778899,
                SecurityToken = "ticket_sec_tok",
                Valid = true
            };
            account.SetCsrfToken("stale_cached_csrf");
            account.TokenSet = DateTime.Now;

            var ticketRequestCsrfs = new List<string>();
            RobloxAccountSessionClient.TestHttpClientFactory = _ => new HttpClient(new DelegateHttpHandler((req, _) =>
            {
                string url = req.RequestUri?.AbsoluteUri ?? string.Empty;
                if (url.Contains("/v1/client-assertion/", StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });

                if (url.Contains("/v1/authentication-ticket", StringComparison.OrdinalIgnoreCase))
                {
                    string csrf = req.Headers.TryGetValues("X-CSRF-TOKEN", out var vals) ? (vals.FirstOrDefault() ?? "") : "";
                    ticketRequestCsrfs.Add(csrf);

                    if (csrf == "stale_cached_csrf")
                    {
                        // Simulate 403 Forbidden WITHOUT x-csrf-token response header
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
                    }

                    if (string.IsNullOrEmpty(csrf))
                    {
                        // GetCSRFTokenAsync fallback call (no X-CSRF-TOKEN header)
                        var resp = new HttpResponseMessage(HttpStatusCode.Forbidden);
                        resp.Headers.TryAddWithoutValidation("x-csrf-token", "recovered_csrf_123");
                        return Task.FromResult(resp);
                    }

                    if (csrf == "recovered_csrf_123")
                    {
                        var ok = new HttpResponseMessage(HttpStatusCode.OK);
                        ok.Headers.TryAddWithoutValidation("rbx-authentication-ticket", "TICKET_RECOVERED_OK");
                        return Task.FromResult(ok);
                    }
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
            }));

            var (success, ticket) = await account.GetAuthTicketAsync();

            Assert.True(success);
            Assert.Equal("TICKET_RECOVERED_OK", ticket);
            Assert.Contains("stale_cached_csrf", ticketRequestCsrfs);
            Assert.Contains("recovered_csrf_123", ticketRequestCsrfs);
        }
    }
}

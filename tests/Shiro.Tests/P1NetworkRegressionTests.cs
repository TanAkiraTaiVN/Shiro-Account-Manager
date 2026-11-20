#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Http;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public sealed class P1NetworkRegressionTests : IDisposable
    {
        private readonly Func<Account, HttpClient>? _origAccHttp = RobloxAccountSessionClient.TestHttpClientFactory;
        private readonly Func<HttpClient>? _origGameInfoHttp = GameInfoService.HttpClientFactoryForTests;
        private readonly ISettingsService _origProxySettings = ProxyBindingService.Settings;

        public void Dispose()
        {
            RobloxAccountSessionClient.TestHttpClientFactory = _origAccHttp;
            GameInfoService.HttpClientFactoryForTests = _origGameInfoHttp;
            ProxyBindingService.Settings = _origProxySettings;
            SharedHttpClient.ResetTestClient("default");
            SharedHttpClient.ResetTestClient("no-redirect");
            RobloxRateLimitCoordinator.Instance.ResetBackoff();
            ProxyBindingService.ClearClients();
            WebhookNotificationService.TelegramBotToken = null;
            WebhookNotificationService.TelegramChatId = null;
        }

        private sealed class DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> fn) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => fn(request, cancellationToken);
        }

        private sealed class DisposableTrackingContent(string body) : StringContent(body, Encoding.UTF8, "application/json")
        {
            public bool Disposed { get; private set; }
            protected override void Dispose(bool disposing)
            {
                Disposed = true;
                base.Dispose(disposing);
            }
        }

        [Fact]
        public async Task HighF1_HighF2_ModerationService_Inspects200Html_AndRejectsHttpRedirect()
        {
            var requestedUrls = new List<string>();
            RobloxAccountSessionClient.TestHttpClientFactory = _ => new HttpClient(new DelegateHandler((req, _) =>
            {
                string url = req.RequestUri!.ToString();
                requestedUrls.Add(url);
                if (url.Contains("not-approved", StringComparison.OrdinalIgnoreCase) && url.StartsWith("https://usermoderation", StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
                if (url.EndsWith("/home", StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("<html><body>Your account has been banned for 7 day. Terms of use violation.</body></html>", Encoding.UTF8, "text/html")
                    });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }));

            var acc = new Account { Username = "Ban200User", UserID = 101, Valid = true, SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.--tok" };
            var (isBanned, badge, _) = await RobloxModerationService.Default.CheckModerationStatusAsync(acc, acc.SecurityToken, CancellationToken.None);
            Assert.True(isBanned);
            Assert.False(string.IsNullOrEmpty(badge));

            requestedUrls.Clear();
            RobloxAccountSessionClient.TestHttpClientFactory = _ => new HttpClient(new DelegateHandler((req, _) =>
            {
                string url = req.RequestUri!.ToString();
                requestedUrls.Add(url);
                if (url.Contains("usermoderation", StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
                if (url.EndsWith("/home", StringComparison.OrdinalIgnoreCase))
                {
                    var redir = new HttpResponseMessage(HttpStatusCode.Redirect);
                    redir.Headers.Location = new Uri("http://www.roblox.com/not-approved");
                    return Task.FromResult(redir);
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }));

            var redirRes = await RobloxModerationService.Default.CheckModerationStatusAsync(acc, acc.SecurityToken, CancellationToken.None);
            Assert.False(redirRes.IsBanned);
            Assert.DoesNotContain(requestedUrls, u => u.StartsWith("http://", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task HighF3_HighF4_SettingsApi_SetServerSendsCsrf_ChangePasswordRequiresRotation_AndSetAvatarChecksSession()
        {
            var capturedHeaders = new List<(string Url, string? Csrf)>();
            bool returnRotatedCookie = false;
            var handler = new DelegateHandler((req, _) =>
            {
                string url = req.RequestUri!.ToString();
                string? csrf = req.Headers.TryGetValues("X-CSRF-TOKEN", out var vals) ? string.Join(",", vals) : null;
                capturedHeaders.Add((url, csrf));

                if (url.Contains("authentication-ticket", StringComparison.OrdinalIgnoreCase) || url.Contains("v2/login", StringComparison.OrdinalIgnoreCase))
                {
                    var r = new HttpResponseMessage(HttpStatusCode.Forbidden);
                    r.Headers.TryAddWithoutValidation("x-csrf-token", "csrf-xyz");
                    return Task.FromResult(r);
                }

                if (url.Contains("join-game-instance", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"jobId\":\"abc\",\"status\":2,\"joinScriptUrl\": null}", Encoding.UTF8, "application/json")
                    });
                }

                if (url.Contains("passwords/change", StringComparison.OrdinalIgnoreCase))
                {
                    var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
                    if (returnRotatedCookie)
                        r.Headers.TryAddWithoutValidation("Set-Cookie", $".ROBLOSECURITY=_|WARNING:-DO-NOT-SHARE-THIS.--rotated123-{new string('R', 100)}; path=/; HttpOnly");
                    return Task.FromResult(r);
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") });
            });

            using var apiScope = AppServices.Override<IRobloxApiClient>(new RobloxApiClient(handler));
            RobloxAccountSessionClient.TestHttpClientFactory = _ => new HttpClient(handler, disposeHandler: false);

            var acc = new Account { Username = "SetTester", UserID = 202, Valid = true, Password = "OldPass", SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.--orig" };
            var srvRes = await RobloxSettingsApiService.Default.SetServerAsync(acc, 1818, "job-1", CancellationToken.None);
            Assert.True(srvRes.Successful);
            Assert.Contains("\"joinScriptUrl\": null", srvRes.Message);
            Assert.Contains(capturedHeaders, h => h.Url.Contains("join-game-instance") && h.Csrf == "csrf-xyz");

            returnRotatedCookie = false;
            bool pwdChangedWithoutRotate = await RobloxSettingsApiService.Default.ChangePasswordAsync(acc, "OldPass", "NewPass", CancellationToken.None);
            Assert.False(pwdChangedWithoutRotate);
            Assert.Equal("OldPass", acc.Password);

            returnRotatedCookie = true;
            bool pwdChangedWithRotate = await RobloxSettingsApiService.Default.ChangePasswordAsync(acc, "OldPass", "NewPass", CancellationToken.None);
            Assert.True(pwdChangedWithRotate);
            Assert.Equal("NewPass", acc.Password);
            Assert.Contains("rotated123", acc.SecurityToken);

            capturedHeaders.Clear();
            var unauthAcc = new Account { Username = "NoTok", Valid = false, SecurityToken = "" };
            await RobloxSettingsApiService.Default.SetAvatarAsync(unauthAcc, "{\"assets\":[]}", CancellationToken.None);
            Assert.Empty(capturedHeaders);
        }

        [Fact]
        public async Task HighF5_SocialApi_RejectsInvalidUserId_AndUnblockEveryoneReportsFailureFast()
        {
            var calledUrls = new List<string>();
            var handler = new DelegateHandler((req, _) =>
            {
                string url = req.RequestUri!.ToString();
                calledUrls.Add(url);
                if (url.Contains("authentication-ticket") || url.Contains("v2/login"))
                {
                    var r = new HttpResponseMessage(HttpStatusCode.Forbidden);
                    r.Headers.TryAddWithoutValidation("x-csrf-token", "csrf-social");
                    return Task.FromResult(r);
                }
                if (url.Contains("get-blocked-users"))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"data\":{\"blockedUsers\":[{\"userId\":555}]}}", Encoding.UTF8, "application/json")
                    });
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
            });

            using var apiScope = AppServices.Override<IRobloxApiClient>(new RobloxApiClient(handler));
            RobloxAccountSessionClient.TestHttpClientFactory = _ => new HttpClient(handler, disposeHandler: false);
            var acc = new Account { Username = "SocTester", UserID = 303, Valid = true, SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.--tok" };

            Assert.Null(await RobloxSocialApiService.Default.BlockUserIdAsync(acc, "../admin"));
            Assert.Null(await RobloxSocialApiService.Default.BlockUserIdAsync(acc, "0"));
            Assert.Empty(calledUrls);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var (ok, msg) = await RobloxSocialApiService.Default.UnblockEveryoneAsync(acc, cts.Token);
            Assert.False(ok);
            Assert.False(string.IsNullOrWhiteSpace(msg));
        }

        [Fact]
        public async Task HighF6_AssetCommerce_FailsClosedOnUnpurchasedJson_AndPreservesResellerSellerId()
        {
            string? capturedPurchaseBody = null;
            string purchaseResponseJson = "{\"purchased\":false,\"reason\":\"InsufficientFunds\"}";

            var handler = new DelegateHandler(async (req, _) =>
            {
                string url = req.RequestUri!.ToString();
                if (url.Contains("authentication-ticket") || url.Contains("v2/login"))
                {
                    var r = new HttpResponseMessage(HttpStatusCode.Forbidden);
                    r.Headers.TryAddWithoutValidation("x-csrf-token", "csrf-buy");
                    return r;
                }
                if (url.Contains("items/details"))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            "[{\"collectibleProductId\":\"orig-prod\",\"hasResellers\":true,\"lowestAvailableResaleProductId\":\"resale-prod\",\"lowestAvailableResaleSellerId\":77777,\"creatorId\":99999}]",
                            Encoding.UTF8,
                            "application/json")
                    };
                }
                if (url.Contains("purchase-item"))
                {
                    capturedPurchaseBody = req.Content != null ? await req.Content.ReadAsStringAsync() : null;
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(purchaseResponseJson, Encoding.UTF8, "application/json")
                    };
                }
                return new HttpResponseMessage(HttpStatusCode.OK);
            });

            using var apiScope = AppServices.Override<IRobloxApiClient>(new RobloxApiClient(handler));
            RobloxAccountSessionClient.TestHttpClientFactory = _ => new HttpClient(handler, disposeHandler: false);
            var acc = new Account { Username = "Buyer", UserID = 404, Valid = true, SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.--tok" };

            var (ok1, err1) = await AssetCommerceService.Default.PurchaseAsync(acc, 1, 2, 100, 0, "citem-1", null);
            Assert.False(ok1);
            Assert.Contains("InsufficientFunds", err1);
            Assert.NotNull(capturedPurchaseBody);
            Assert.Contains("\"expectedSellerId\":77777", capturedPurchaseBody);

            purchaseResponseJson = "{\"errorMessage\":\"ItemPriceChanged\"}";
            var (ok2, err2) = await AssetCommerceService.Default.PurchaseAsync(acc, 1, 2, 100, 77777, "citem-1", "resale-prod");
            Assert.False(ok2);
            Assert.Contains("ItemPriceChanged", err2);

            purchaseResponseJson = "{\"purchased\":true}";
            var (ok3, _) = await AssetCommerceService.Default.PurchaseAsync(acc, 1, 2, 100, 77777, "citem-1", "resale-prod");
            Assert.True(ok3);
        }

        [Fact]
        public async Task HighF7_HighF8_PresenceService_DisposeSafety_AndSanitizedTitle()
        {
            Assert.Equal("Cool Game", GameInfoService.SanitizeDisplayTitle("🔥 Cool Game 🎉"));

            using var presenceSvc = new PresenceService();
            var refreshEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var handler = new DelegateHandler(async (req, ct) =>
            {
                if (req.RequestUri!.ToString().Contains("presence/users"))
                {
                    refreshEntered.TrySetResult();
                    await releaseRefresh.Task.WaitAsync(ct);
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"userPresences\":[{\"userId\":505,\"userPresenceType\":2,\"universeId\":888}]}", Encoding.UTF8, "application/json")
                    };
                }
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"data\":[{\"id\":888,\"name\":\"🚀 Space Obby 🔥\"}]}", Encoding.UTF8, "application/json")
                };
            });

            using var apiScope = AppServices.Override<IRobloxApiClient>(new RobloxApiClient(handler));
            var acc = new Account { Username = "PresUser", UserID = 505, Valid = true };
            var prev = AppServices.AccountRepository.GetAll().ToList();
            AccountManager.ReplaceAccountsList(new List<Account> { acc });
            try
            {
                var task = presenceSvc.RefreshAsync(new long[] { 505 }, CancellationToken.None);
                await refreshEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                presenceSvc.Dispose();
                releaseRefresh.TrySetResult();
                await task;

                Assert.Equal("Space Obby", acc.Presence?.gameName);
            }
            finally
            {
                AccountManager.ReplaceAccountsList(prev);
            }
        }

        [Fact]
        public async Task HighF9_GameInfoService_CallerCancellationDoesNotPoisonSharedInFlightLookup()
        {
            var uniGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            GameInfoService.HttpClientFactoryForTests = () => new HttpClient(new DelegateHandler(async (req, ct) =>
            {
                string url = req.RequestUri!.ToString();
                if (url.Contains("universes/v1/places", StringComparison.OrdinalIgnoreCase))
                {
                    await uniGate.Task.WaitAsync(ct);
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"universeId\":9001}", Encoding.UTF8, "application/json") };
                }
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"data\":[{\"sourceName\":\"Shared Experience\"}]}", Encoding.UTF8, "application/json") };
            }));

            using var cts1 = new CancellationTokenSource();
            var t1 = GameInfoService.ResolvePlaceNameAsync(777001, cts1.Token);
            var t2 = GameInfoService.ResolvePlaceNameAsync(777001, CancellationToken.None);

            cts1.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await t1);

            uniGate.TrySetResult();
            string? resolved = await t2;
            Assert.Equal("Shared Experience", resolved);
        }

        [Fact]
        public async Task HighF10_RateLimitCoordinator_NestedGeneralReentrancyDoesNotDeadlock()
        {
            var coordinator = RobloxRateLimitCoordinator.Instance;
            coordinator.ResetBackoff();

            var tasks = Enumerable.Range(0, 8).Select(_ =>
                coordinator.ExecuteWithPacingAsync(
                    RateLimitCategory.General,
                    () => coordinator.ExecuteWithPacingAsync(
                        RateLimitCategory.General,
                        () => Task.FromResult(42),
                        minIntervalMs: 1),
                    minIntervalMs: 1)).ToArray();

            var completed = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.All(completed, v => Assert.Equal(42, v));
        }

        [Fact]
        public void HighF11_DiscordRpc_JsonOmitsNullFields()
        {
            byte[] utf8 = DiscordRpcService.BuildSetActivityPayload(new DiscordActivityItem
            {
                Details = "Playing Roblox",
                State = "Online",
                StartTimestamp = 1700000000,
                LargeImageKey = "shiro_logo",
                LargeImageText = "Shiro",
                SmallImageKey = null,
                SmallImageText = null,
                Buttons = null
            }, nonce: "n1");

            string json = Encoding.UTF8.GetString(utf8);
            Assert.DoesNotContain("\"small_image\"", json);
            Assert.DoesNotContain("\"small_text\"", json);
            Assert.DoesNotContain("\"buttons\"", json);
        }

        [Fact]
        public void HighF12_F13_F14_F15_ProxySecurityEncryptionCustomRetentionAndIpv6()
        {
            var invalidEx = Assert.Throws<InvalidOperationException>(() =>
                ProxyBindingService.GetClient("http://alice:TopSecret99@invalid host:8080"));
            Assert.DoesNotContain("TopSecret99", invalidEx.Message);
            Assert.DoesNotContain("alice", invalidEx.Message);

            var colonRaw = ProxyBindingService.ParseWebProxy("10.0.0.1:8080:bob:pass%20literal") as WebProxy;
            Assert.NotNull(colonRaw);
            Assert.Equal("pass%20literal", ((NetworkCredential)colonRaw!.Credentials!).Password);

            var ipv6WithSlash = ProxyManagerService.ParseProxy("socks5://user1:p%3A4@[2001:db8::1]:1080/");
            Assert.NotNull(ipv6WithSlash);
            Assert.Equal("socks5", ipv6WithSlash!.Protocol);
            Assert.Equal("2001:db8::1", ipv6WithSlash.Host);
            Assert.Equal(1080, ipv6WithSlash.Port);

            var ipv6ColonPass = ProxyManagerService.ParseProxy("[::1]:8080:u:p:1234:more");
            Assert.NotNull(ipv6ColonPass);
            Assert.Equal("::1", ipv6ColonPass!.Host);
            Assert.Equal(8080, ipv6ColonPass.Port);
            Assert.Equal("u", ipv6ColonPass.Username);
            Assert.Equal("p:1234:more", ipv6ColonPass.Password);

            Assert.Equal("http://[2001:db8::1]:8080", NetworkSignatureHelper.NormalizeAccountProxyEndpoint("http://u:p@[2001:db8::1]:8080/"));

            var manualAcc = new Account { Username = "ManualProxy", Proxy = "127.0.0.99:9999" };
            var poolAcc = new Account { Username = "PoolProxy", Proxy = null };
            ProxyBindingService.DistributeProxiesSticky(new[] { manualAcc, poolAcc }, new[] { "10.0.0.1:8080" });
            Assert.Equal("127.0.0.99:9999", manualAcc.Proxy);
            Assert.Equal("10.0.0.1:8080", poolAcc.Proxy);

            string tempDir = Path.Combine(Path.GetTempPath(), "ShiroProxyEnc_" + Guid.NewGuid().ToString("N"));
            try
            {
                using (StoragePaths.OverrideDataDirectoryForTesting(tempDir))
                {
                    ProxyManagerService.InvalidateCache();
                    ProxyManagerService.SaveProxies(new[]
                    {
                        new ProxyItem { RawEntry = "10.0.0.5:8080:secretUser:UltraSecretPwd", Host = "10.0.0.5", Port = 8080, Username = "secretUser", Password = "UltraSecretPwd" }
                    });
                    string diskContent = File.ReadAllText(ProxyManagerService.ProxiesFilePath);
                    Assert.StartsWith("dpapi:", diskContent, StringComparison.Ordinal);
                    Assert.DoesNotContain("UltraSecretPwd", diskContent);

                    ProxyManagerService.InvalidateCache();
                    var loaded = ProxyManagerService.LoadProxies();
                    Assert.Single(loaded);
                    Assert.Equal("UltraSecretPwd", loaded[0].Password);
                }
            }
            finally
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            }
        }

        [Fact]
        public async Task HighF16_F17_ServerQuery_HandlesIntegerStatus_AndRacePingDoesNotDisposeEarly()
        {
            var handler = new DelegateHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"status\":22}", Encoding.UTF8, "application/json")
            }));
            using var apiScope = AppServices.Override<IRobloxApiClient>(new RobloxApiClient(handler));
            RobloxAccountSessionClient.TestHttpClientFactory = _ => new HttpClient(handler, disposeHandler: false);

            var acc = new Account { Username = "QueryUser", UserID = 606, Valid = true, SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.--tok" };
            var res = await RobloxServerQueryService.Default.QueryServerRegionAndPingAsync(acc, 1818, "job-id-1");
            Assert.False(res.Success);
            Assert.Contains("22", res.Region);

            var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int? raced = await RobloxServerQueryService.RacePingProbesAsync(
                _ => Task.FromResult<int?>(12),
                async ct =>
                {
                    secondStarted.TrySetResult();
                    await Task.Delay(80, CancellationToken.None);
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    return linked.IsCancellationRequested ? null : 25;
                });
            await secondStarted.Task;
            Assert.Equal(12, raced);
        }

        [Fact]
        public async Task HighF19_F20_AndUpdateCheck_WebhookValidation_AndHttpResponseDisposal()
        {
            Assert.False(WebhookNotificationService.IsAllowedDiscordWebhookUrl("https://discord.com/login"));
            Assert.False(WebhookNotificationService.IsAllowedDiscordWebhookUrl("http://discord.com/api/webhooks/1/a"));
            Assert.True(WebhookNotificationService.IsAllowedDiscordWebhookUrl("https://discord.com/api/webhooks/123/token"));

            int telegramCalls = 0;
            var spyClient = new HttpClient(new DelegateHandler((_, _) =>
            {
                Interlocked.Increment(ref telegramCalls);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }));
            SharedHttpClient.SetTestClient("no-redirect", spyClient);
            SharedHttpClient.SetTestClient("default", spyClient);

            WebhookNotificationService.DiscordWebhookUrl = null;
            WebhookNotificationService.TelegramBotToken = "12345:abc/evil?x=1";
            WebhookNotificationService.TelegramChatId = "-1001234";
            await WebhookNotificationService.SendAlertAsync(new WebhookAlert { EventType = WebhookEventType.Info, Description = "Test" });
            Assert.Equal(0, telegramCalls);

            var trackedContent = new DisposableTrackingContent("{\"tag_name\":\"v5.0.0\"}");
            SharedHttpClient.SetTestClient("default", new HttpClient(new DelegateHandler((_, _) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = trackedContent }))));

            bool hasNew = await UpdateCheckService.CheckForUpdatesInternalAsync(CancellationToken.None);
            Assert.False(hasNew);
            Assert.True(trackedContent.Disposed);
        }
    }
}

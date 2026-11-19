#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using NSubstitute;
using Shiro.AvaloniaUI.Views;
using Shiro.Services;
using Shiro.Tests.TestInfrastructure;
using Shiro.ViewModels;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public sealed class P1AuditRemediationTests
    {
        [Fact]
        public void MainViewModel_LanguageSwitch_EnToViToEn_ResetsDefaultReadyStatusSymmetrically()
        {
            var prevLang = LocalizationService.CurrentLanguage;
            var prevDispatcher = AppDispatcher.CustomDispatcher;
            AppDispatcher.CustomDispatcher = static action => action();
            try
            {
                var isolatedSettings = Substitute.For<ISettingsService>();
                isolatedSettings.Exists("EnablePotatoMode").Returns(true);
                isolatedSettings.Get<bool>("EnablePotatoMode").Returns(true);

                LocalizationService.SetLanguage(AppLanguage.English, saveSetting: false);
                using var vm = new MainViewModel(null, isolatedSettings);
                Assert.True(vm.IsPotatoMode);
                string enReady = LocalizationService.Get("Status_ReadyDefault");
                Assert.Equal(enReady, vm.StatusMessage);

                LocalizationService.SetLanguage(AppLanguage.Vietnamese, saveSetting: false);
                string viReady = LocalizationService.Get("Status_ReadyDefault");
                Assert.NotEqual(enReady, viReady);
                Assert.Equal(viReady, vm.StatusMessage);

                LocalizationService.SetLanguage(AppLanguage.English, saveSetting: false);
                Assert.Equal(enReady, vm.StatusMessage);

                vm.StatusMessage = "Custom toast status";
                LocalizationService.SetLanguage(AppLanguage.Vietnamese, saveSetting: false);
                LocalizationService.SetLanguage(AppLanguage.English, saveSetting: false);
                Assert.Equal("Custom toast status", vm.StatusMessage);
            }
            finally
            {
                AppDispatcher.CustomDispatcher = prevDispatcher;
                LocalizationService.SetLanguage(prevLang, saveSetting: false);
            }
        }

        [Fact]
        public void ServerListViewModel_LanguageSwitch_EnToViToEn_ResetsDefaultReadyStatusSymmetrically()
        {
            var prevLang = LocalizationService.CurrentLanguage;
            var prevDispatcher = AppDispatcher.CustomDispatcher;
            AppDispatcher.CustomDispatcher = static action => action();
            try
            {
                LocalizationService.SetLanguage(AppLanguage.English, saveSetting: false);
                using var vm = new ServerListViewModel();
                string enReady = LocalizationService.Get("Status_Ready");
                Assert.Equal(enReady, vm.LoadingStatus);

                LocalizationService.SetLanguage(AppLanguage.Vietnamese, saveSetting: false);
                string viReady = LocalizationService.Get("Status_Ready");
                Assert.NotEqual(enReady, viReady);
                Assert.Equal(viReady, vm.LoadingStatus);

                LocalizationService.SetLanguage(AppLanguage.English, saveSetting: false);
                Assert.Equal(enReady, vm.LoadingStatus);

                vm.LoadingStatus = "Custom server status";
                LocalizationService.SetLanguage(AppLanguage.Vietnamese, saveSetting: false);
                LocalizationService.SetLanguage(AppLanguage.English, saveSetting: false);
                Assert.Equal("Custom server status", vm.LoadingStatus);
            }
            finally
            {
                AppDispatcher.CustomDispatcher = prevDispatcher;
                LocalizationService.SetLanguage(prevLang, saveSetting: false);
            }
        }

        [Fact]
        public void CommandPaletteCatalog_GetBuiltinCommands_ReturnsSameCachedInstance()
        {
            var first = CommandPaletteCatalog.GetBuiltinCommands();
            var second = CommandPaletteCatalog.GetBuiltinCommands();

            Assert.NotEmpty(first);
            Assert.Same(first, second);
        }

        [AvaloniaFact]
        public void SettingsView_InitialShow_LoadsViewModelOnce_AndReloadsWhenReopened()
        {
            var innerGeneral = AppServices.GeneralSettings;
            var generalSpy = Substitute.For<ISettingsService>();
            int loadProbeCount = 0;

            generalSpy.Exists(Arg.Any<string>()).Returns(ci =>
            {
                string key = ci.Arg<string>();
                if (string.Equals(key, AppSettingsKeys.General.AggressiveRamTrimming, StringComparison.Ordinal))
                    loadProbeCount++;
                return innerGeneral.Exists(key);
            });
            generalSpy.Get(Arg.Any<string>()).Returns(ci => innerGeneral.Get(ci.Arg<string>()));
            generalSpy.Get(Arg.Any<string>(), Arg.Any<bool>()).Returns(ci => innerGeneral.Get(ci.Arg<string>(), ci.Arg<bool>()));
            generalSpy.Get<bool>(Arg.Any<string>()).Returns(ci => innerGeneral.Get<bool>(ci.Arg<string>()));
            generalSpy.Get(Arg.Any<string>(), Arg.Any<int>()).Returns(ci => innerGeneral.Get(ci.Arg<string>(), ci.Arg<int>()));
            generalSpy.Get<int>(Arg.Any<string>()).Returns(ci => innerGeneral.Get<int>(ci.Arg<string>()));
            generalSpy.Get(Arg.Any<string>(), Arg.Any<double>()).Returns(ci => innerGeneral.Get(ci.Arg<string>(), ci.Arg<double>()));
            generalSpy.When(s => s.Set(Arg.Any<string>(), Arg.Any<string?>()))
                .Do(ci => innerGeneral.Set(ci.ArgAt<string>(0), ci.ArgAt<string?>(1)));

            using var _ = AppServices.Override<ISettingsService>(generalSpy);
            Window? window = null;
            try
            {
                var hiddenView = new SettingsView { IsVisible = false };
                window = new Window { Width = 900, Height = 700, Content = hiddenView };
                window.Show();
                Dispatcher.UIThread.RunJobs();

                Assert.Equal(0, loadProbeCount);

                hiddenView.IsVisible = true;
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(1, loadProbeCount);

                hiddenView.IsVisible = false;
                Dispatcher.UIThread.RunJobs();
                hiddenView.IsVisible = true;
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(2, loadProbeCount);
            }
            finally
            {
                window?.Close();
            }

            loadProbeCount = 0;
            Window? visibleWindow = null;
            try
            {
                var visibleView = new SettingsView();
                visibleWindow = new Window { Width = 900, Height = 700, Content = visibleView };
                visibleWindow.Show();
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(1, loadProbeCount);
            }
            finally
            {
                visibleWindow?.Close();
            }
        }

        [Fact]
        public void LocalizationService_GetWithDictionaries_FallsBackToEnglishOnlyWhenNotEnglishPrimary()
        {
            var en = new Dictionary<string, string>(StringComparer.Ordinal) { ["MissingInVi"] = "English Fallback" };
            var vi = new Dictionary<string, string>(StringComparer.Ordinal) { ["OnlyInVi"] = "Chi co trong Vi" };

            Assert.Equal("English Fallback", LocalizationService.GetWithDictionaries("MissingInVi", null, vi, en));
            Assert.Equal("custom_fb", LocalizationService.GetWithDictionaries("MissingInBoth", "custom_fb", vi, en));
            Assert.Equal("MissingInBoth", LocalizationService.GetWithDictionaries("MissingInBoth", null, vi, en));
            Assert.Equal("OnlyInVi", LocalizationService.GetWithDictionaries("OnlyInVi", "OnlyInVi", en, en));
        }

        [Fact]
        public void DiscordRpcService_WorkerGenerationGuard_PreventsStaleWorkerCleanup()
        {
            Assert.False(DiscordRpcService.ShouldCleanupWorkerSession(workerGeneration: 1, activeGeneration: 2));
            Assert.True(DiscordRpcService.ShouldCleanupWorkerSession(workerGeneration: 2, activeGeneration: 2));

            var logger = Substitute.For<IAppLogger>();
            var settings = Substitute.For<ISettingsService>();
            settings.Get(AppSettingsKeys.General.DiscordRpcEnabled, Arg.Any<bool>()).Returns(true);
            settings.Get<bool>(AppSettingsKeys.General.DiscordRpcEnabled, Arg.Any<bool>()).Returns(true);

            using var rpc = new DiscordRpcService(logger, settings);
            long genBefore = rpc.WorkerGenerationForTest;

            rpc.ApplyConfigurationChanged(true, reconnect: true);
            rpc.ApplyConfigurationChanged(true, reconnect: true);
            long genAfterReconnects = rpc.WorkerGenerationForTest;

            rpc.Stop();
            long genAfterStop = rpc.WorkerGenerationForTest;

            Assert.True(genAfterReconnects > genBefore);
            Assert.True(genAfterStop > genAfterReconnects);
        }

        [Fact]
        public void StreamerModeService_Shutdown_StopsAndAllowsCleanReinitialize()
        {
            var ex = Record.Exception(() =>
            {
                StreamerModeService.InitializeForTest();
                StreamerModeService.Shutdown();
                StreamerModeService.Shutdown();
                StreamerModeService.InitializeForTest();
                StreamerModeService.Shutdown();
            });
            Assert.Null(ex);
            Assert.False(StreamerModeService.IsStreamingAppDetected);
        }

        private static string BuildPresenceEnvelopeJson(params (long UserId, long UniverseId)[] entries)
        {
            var list = entries.Select(e => new
            {
                userPresenceType = (int)UserPresenceType.InGame,
                universeId = e.UniverseId,
                userId = e.UserId
            }).ToList();
            return JsonSerializer.Serialize(new Dictionary<string, object> { ["userPresences"] = list });
        }

        private static string BuildGamesEnvelopeJson(long universeId, string name) =>
            JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["data"] = new[] { new { id = universeId, name } }
            });

        [Fact]
        public async Task Task4_Presence_PartitionsUpdatePresenceGetPresenceAndPopulateGameNamesByEffectiveProxy()
        {
            var prevAccounts = AppServices.AccountRepository.GetAll().ToList();
            var prevProxyFactory = Presence.TestProxyHttpClientFactory;
            var prevProxySettings = ProxyBindingService.Settings;

            var directRequests = new List<(string Url, string? Body)>();
            var proxy1Requests = new List<(string Url, string? Body)>();
            var proxy2Requests = new List<(string Url, string? Body)>();

            HttpResponseMessage HandleMock(string url, string? body, List<(string Url, string? Body)> sink)
            {
                sink.Add((url, body));
                if (url.Contains("presence/users", StringComparison.OrdinalIgnoreCase))
                {
                    using var doc = JsonDocument.Parse(body!);
                    var ids = doc.RootElement.GetProperty("userIds").EnumerateArray().Select(x => x.GetInt64()).ToArray();
                    var entries = ids.Select(id => (id, id switch { 101 or 102 => 901L, 103 => 903L, _ => 904L })).ToArray();
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(BuildPresenceEnvelopeJson(entries), Encoding.UTF8, "application/json")
                    };
                }

                if (url.Contains("v1/games", StringComparison.OrdinalIgnoreCase))
                {
                    if (url.Contains("901", StringComparison.Ordinal))
                        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(BuildGamesEnvelopeJson(901, "Proxy1 Game"), Encoding.UTF8, "application/json") };
                    if (url.Contains("903", StringComparison.Ordinal))
                        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(BuildGamesEnvelopeJson(903, "Proxy2 Game"), Encoding.UTF8, "application/json") };
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(BuildGamesEnvelopeJson(904, "Direct Game"), Encoding.UTF8, "application/json") };
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            var directHandler = new FakeHttpHandler(async (req, _) =>
            {
                string? body = req.Content != null ? await req.Content.ReadAsStringAsync() : null;
                return HandleMock(req.RequestUri!.ToString(), body, directRequests);
            });
            var proxy1Handler = new FakeHttpHandler(async (req, _) =>
            {
                string? body = req.Content != null ? await req.Content.ReadAsStringAsync() : null;
                return HandleMock(req.RequestUri!.ToString(), body, proxy1Requests);
            });
            var proxy2Handler = new FakeHttpHandler(async (req, _) =>
            {
                string? body = req.Content != null ? await req.Content.ReadAsStringAsync() : null;
                return HandleMock(req.RequestUri!.ToString(), body, proxy2Requests);
            });

            using var directClient = new HttpClient(directHandler);
            using var proxy1Client = new HttpClient(proxy1Handler);
            using var proxy2Client = new HttpClient(proxy2Handler);

            var accA1 = new Account { Username = "PA1", UserID = 101, Proxy = "http://10.0.0.1:8080", Valid = true };
            var accA2 = new Account { Username = "PA2", UserID = 102, Proxy = "10.0.0.1:8080", Valid = true };
            var accB = new Account { Username = "PB1", UserID = 103, Proxy = "http://10.0.0.2:8080", Valid = true };
            var accDirect = new Account { Username = "PD1", UserID = 104, Proxy = null, Valid = true };

            var ini = new IniFile();
            var settings = ini.Section("General");
            settings.Set(AppSettingsKeys.General.UseProxies, "true");
            ProxyBindingService.Settings = settings;

            try
            {
                RobloxRateLimitCoordinator.Instance.ResetBackoff();
                using var apiScope = AppServices.Override<IRobloxApiClient>(new RobloxApiClient(directHandler));
                Presence.TestProxyHttpClientFactory = scope => scope switch
                {
                    "http://10.0.0.1:8080" => proxy1Client,
                    "http://10.0.0.2:8080" => proxy2Client,
                    _ => throw new InvalidOperationException($"Unexpected proxy scope: {scope}")
                };
                AccountManager.ResetAccountsForTests(new[] { accA1, accA2, accB, accDirect });

                await Presence.UpdatePresence(101, 102, 103, 104);

                Assert.Contains(proxy1Requests, r => r.Url.Contains("presence/users") && r.Body!.Contains("101") && r.Body.Contains("102") && !r.Body.Contains("103") && !r.Body.Contains("104"));
                Assert.Contains(proxy1Requests, r => r.Url.Contains("v1/games") && r.Url.Contains("901") && !r.Url.Contains("903") && !r.Url.Contains("904"));
                Assert.Contains(proxy2Requests, r => r.Url.Contains("presence/users") && r.Body!.Contains("103") && !r.Body.Contains("101") && !r.Body.Contains("104"));
                Assert.Contains(proxy2Requests, r => r.Url.Contains("v1/games") && r.Url.Contains("903") && !r.Url.Contains("901") && !r.Url.Contains("904"));
                Assert.Contains(directRequests, r => r.Url.Contains("presence/users") && r.Body!.Contains("104") && !r.Body.Contains("101") && !r.Body.Contains("103"));
                Assert.Contains(directRequests, r => r.Url.Contains("v1/games") && r.Url.Contains("904") && !r.Url.Contains("901") && !r.Url.Contains("903"));

                Assert.Equal("Proxy1 Game", accA1.Presence?.gameName);
                Assert.Equal("Proxy2 Game", accB.Presence?.gameName);
                Assert.Equal("Direct Game", accDirect.Presence?.gameName);

                directRequests.Clear();
                proxy1Requests.Clear();
                proxy2Requests.Clear();

                var fetched = await Presence.GetPresence(101, 103, 104);
                Assert.NotNull(fetched);
                Assert.Equal(3, fetched!.Count);
                Assert.Single(proxy1Requests);
                Assert.Single(proxy2Requests);
                Assert.Single(directRequests);
            }
            finally
            {
                Presence.TestProxyHttpClientFactory = prevProxyFactory;
                ProxyBindingService.Settings = prevProxySettings;
                AccountManager.ResetAccountsForTests(prevAccounts);
                RobloxRateLimitCoordinator.Instance.ResetBackoff();
            }
        }

        [Fact]
        public async Task Task4_SharedHttpClient_SendAsync_Reports429ToRateLimitCoordinator()
        {
            RobloxRateLimitCoordinator.Instance.ResetBackoff();
            try
            {
                int fastCalls = 0;
                var fastHandler = new FakeHttpHandler(req =>
                {
                    if (req.RequestUri?.AbsoluteUri.Contains("universeIds=1", StringComparison.Ordinal) == true)
                    {
                        Interlocked.Increment(ref fastCalls);
                        var resp = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                        resp.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(5));
                        return resp;
                    }
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                });
                SharedHttpClient.SetTestClient("default", new HttpClient(fastHandler));

                using var req1 = new HttpRequestMessage(HttpMethod.Get, "https://games.roblox.com/v1/games?universeIds=1");
                using var resp1 = await SharedHttpClient.SendAsync(req1, retryTransient: false);
                Assert.Equal(HttpStatusCode.TooManyRequests, resp1.StatusCode);
                Assert.Equal(1, fastCalls);
                Assert.True(RobloxRateLimitCoordinator.Instance.IsBackingOffCategory(RateLimitCategory.General));
                Assert.False(RobloxRateLimitCoordinator.Instance.IsBackingOffCategory(RateLimitCategory.Presence));
                Assert.InRange(RobloxRateLimitCoordinator.Instance.GetRemainingBackoff(RateLimitCategory.General).TotalSeconds, 4.0, 7.0);

                RobloxRateLimitCoordinator.Instance.ResetBackoff();

                int rateLimitAlerts = 0;
                void OnRateLimitAlert(WebhookAlert alert)
                {
                    if (alert.EventType == WebhookEventType.RateLimited &&
                        (alert.Description?.Contains("Presence", StringComparison.Ordinal) ?? false))
                    {
                        Interlocked.Increment(ref rateLimitAlerts);
                    }
                }

                WebhookNotificationService.AlertDispatched += OnRateLimitAlert;
                try
                {
                    int retryCalls = 0;
                    var retryHandler = new FakeHttpHandler(req =>
                    {
                        if (req.RequestUri?.AbsoluteUri.Contains("presence/users", StringComparison.Ordinal) == true)
                        {
                            Interlocked.Increment(ref retryCalls);
                            var resp = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                            resp.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
                            return resp;
                        }
                        return new HttpResponseMessage(HttpStatusCode.NotFound);
                    });
                    SharedHttpClient.SetTestClient("default", new HttpClient(retryHandler));

                    using var req2 = new HttpRequestMessage(HttpMethod.Get, "https://presence.roblox.com/v1/presence/users");
                    using var resp2 = await SharedHttpClient.SendAsync(req2, retryTransient: true);
                    Assert.Equal(HttpStatusCode.TooManyRequests, resp2.StatusCode);
                    Assert.Equal(1, retryCalls);
                    Assert.True(RobloxRateLimitCoordinator.Instance.IsBackingOffCategory(RateLimitCategory.Presence));
                    Assert.Equal(1, RobloxRateLimitCoordinator.Instance.ConsecutiveRateLimitHits);
                    Assert.Equal(1, rateLimitAlerts);
                }
                finally
                {
                    WebhookNotificationService.AlertDispatched -= OnRateLimitAlert;
                }

                RobloxRateLimitCoordinator.Instance.ResetBackoff();

                int transientCalls = 0;
                var transientHandler = new FakeHttpHandler(req =>
                {
                    if (req.RequestUri?.AbsoluteUri.Contains("universeIds=2", StringComparison.Ordinal) == true)
                    {
                        int callNum = Interlocked.Increment(ref transientCalls);
                        return callNum == 1
                            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("retry later") }
                            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") };
                    }
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                });
                SharedHttpClient.SetTestClient("default", new HttpClient(transientHandler));

                using var req3 = new HttpRequestMessage(HttpMethod.Get, "https://games.roblox.com/v1/games?universeIds=2");
                using var resp3 = await SharedHttpClient.SendAsync(req3, retryTransient: true);
                Assert.Equal(HttpStatusCode.OK, resp3.StatusCode);
                Assert.Equal(2, transientCalls);
                Assert.False(RobloxRateLimitCoordinator.Instance.IsBackingOffCategory(RateLimitCategory.General));
                Assert.Equal(0, RobloxRateLimitCoordinator.Instance.ConsecutiveRateLimitHits);
            }
            finally
            {
                SharedHttpClient.ResetTestClient("default");
                RobloxRateLimitCoordinator.Instance.ResetBackoff();
            }
        }
    }
}

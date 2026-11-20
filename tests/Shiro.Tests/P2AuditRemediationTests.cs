#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using NSubstitute;
using Shiro.AvaloniaUI.Controls;
using Shiro.Http;
using Shiro.Services;
using Shiro.Tests.TestInfrastructure;
using Shiro.ViewModels;
using Xunit;

namespace Shiro.Tests
{
    [Collection("StaticSeams")]
    public sealed class P2AuditRemediationTests : IDisposable
    {
        private readonly IRobloxApiClient _originalClient = AppServices.RobloxApi;

        public void Dispose()
        {
            RobloxAccountSessionClient.TestHttpClientFactory = null;
            GameInfoService.HttpClientFactoryForTests = null;
            AppServices.Override<IRobloxApiClient>(_originalClient);
        }

        [Theory]
        [InlineData("7", "Bot07", null, null, 0L, true)]
        [InlineData("07", "Bot7", null, null, 0L, true)]
        [InlineData("7", "UnrelatedUser", null, "7up", 0L, true)]
        [InlineData("7", "UnrelatedUser", null, "7", 0L, true)]
        [InlineData("7", "Jackpot70", null, "Main", 123745L, false)]
        public void AccountSearchQuery_ZeroPaddedNumericAndShortGroup_MatchesConsistently(
            string query, string username, string? alias, string? group, long userId, bool expected)
        {
            var q = AccountSearchQuery.Create(query);
            Assert.Equal(expected, q.IsMatch(username, alias, group, userId));
        }

        [Theory]
        [InlineData("5.0.0", "v5.0.1-hotfix", true)]
        [InlineData("5.0.0", "5.0.1+build.42", true)]
        [InlineData("5.0.1-beta", "5.0.1", true)]
        [InlineData("5.0.1", "5.0.1-beta", false)]
        [InlineData("5.0.1-beta", "v5.0.1-beta", false)]
        public void UpdateCheckService_IsRemoteVersionNewer_HandlesSemVerTags(
            string currentVer, string remoteTag, bool expectedNewer)
        {
            Assert.Equal(expectedNewer, UpdateCheckService.IsRemoteVersionNewer(currentVer, remoteTag));
        }

        [Fact]
        public async Task GameInfoService_AddRecentGameAsync_EnforcesExactMaxLimit()
        {
            string prevMax = AppServices.GeneralSettings.Get("MaxRecentGames") ?? string.Empty;
            bool hadKey = AppServices.GeneralSettings.Exists("MaxRecentGames");
            var prevRecent = AppServices.ServerBrowserState.RecentGames?.ToList() ?? new List<Game>();

            try
            {
                GameInfoService.HttpClientFactoryForTests = () =>
                    new HttpClient(new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
                AppServices.GeneralSettings.Set("MaxRecentGames", "3");
                AppServices.ServerBrowserState.RecentGames = new List<Game>();

                for (int i = 1; i <= 4; i++)
                {
                    var game = new Game(1000 + i, $"RecentGame{i}");
                    await GameInfoService.AddRecentGameAsync(game, loading: true);
                }

                Assert.Equal(3, AppServices.ServerBrowserState.RecentGames.Count);
                Assert.DoesNotContain(AppServices.ServerBrowserState.RecentGames, g => g.Details?.placeId == 1001);
                Assert.Contains(AppServices.ServerBrowserState.RecentGames, g => g.Details?.placeId == 1004);
            }
            finally
            {
                AppServices.ServerBrowserState.RecentGames = prevRecent;
                if (hadKey)
                    AppServices.GeneralSettings.Set("MaxRecentGames", prevMax);
                else
                    AppServices.GeneralSettings.RemoveProperty("MaxRecentGames");
            }
        }

        [Fact]
        public void WebhookNotificationService_FormatDiscordPayload_TruncatesOversizedDescriptionAndField()
        {
            var alert = new WebhookAlert
            {
                AccountUsername = "BigAlertUser",
                EventType = WebhookEventType.ModerationWarning,
                Description = new string('X', 5000),
                Timestamp = DateTimeOffset.UtcNow
            };

            string json = WebhookNotificationService.FormatDiscordPayload(alert);
            using var doc = JsonDocument.Parse(json);
            var embed = doc.RootElement.GetProperty("embeds")[0];
            string desc = embed.GetProperty("description").GetString() ?? string.Empty;
            Assert.InRange(desc.Length, 1, 4096);

            foreach (var field in embed.GetProperty("fields").EnumerateArray())
            {
                string val = field.GetProperty("value").GetString() ?? string.Empty;
                Assert.InRange(val.Length, 1, 1024);
            }
        }

        [Fact]
        public async Task RobloxSocialApiService_SendFriendRequestAsync_IncludesJsonBody()
        {
            string targetUsername = "P2FriendTarget_" + Guid.NewGuid().ToString("N")[..8];
            var handler = new FakeHttpHandler()
                .Enqueue(HttpStatusCode.OK, $$$"""{"data":[{"id":999111,"name":"{{{targetUsername}}}"}]}""")
                .Enqueue(HttpStatusCode.OK, """{"success":true}""");

            AppServices.Override<IRobloxApiClient>(new RobloxApiClient(handler));
            RobloxAccountSessionClient.TestHttpClientFactory = _ => new HttpClient(handler);

            var account = new Account
            {
                Username = "FriendRequester",
                UserID = 1234,
                SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.--" + new string('A', 80),
                CSRFToken = "csrf-ok",
                TokenSet = DateTime.Now
            };

            bool ok = await RobloxSocialApiService.Instance.SendFriendRequestAsync(account, targetUsername, CancellationToken.None);

            Assert.True(ok);
            Assert.Equal(2, handler.Requests.Count);
            Assert.NotNull(handler.RequestBodies[1]);
            Assert.Contains("{", handler.RequestBodies[1], StringComparison.Ordinal);
        }

        [AvaloniaFact]
        public void CardRowAndUniformPanel_ClampColumnsToMaximumOfFour()
        {
            var acc = new AccountViewModel(new Account { Username = "GridCard", UserID = 111 });
            var row = new AccountRowViewModel(6, acc);
            var panel = new UniformRowPanel { Columns = 6 };

            Assert.Equal(4, row.ColumnCount);
            Assert.Equal(4, panel.Columns);
        }

        [Fact]
        public void Localization_VietnameseBtnReLogin_IsTranslated()
        {
            var prev = LocalizationService.CurrentLanguage;
            try
            {
                LocalizationService.SetLanguage(AppLanguage.Vietnamese, saveSetting: false);
                string text = LocalizationService.Get("Btn_ReLogin");
                Assert.NotEqual("Re-login", text);
                Assert.False(string.IsNullOrWhiteSpace(text));
            }
            finally
            {
                LocalizationService.SetLanguage(prev, saveSetting: false);
            }
        }

        [Theory]
        [InlineData(HttpStatusCode.OK, true)]
        [InlineData(HttpStatusCode.NoContent, true)]
        [InlineData(HttpStatusCode.Unauthorized, true)]
        [InlineData(HttpStatusCode.Forbidden, false)]
        [InlineData(HttpStatusCode.BadGateway, false)]
        public void ProxyBindingService_IsProbeHttpSuccess_RejectsForbiddenAndAllowsAuthOrOk(
            HttpStatusCode statusCode, bool expected)
        {
            Assert.Equal(expected, ProxyBindingService.IsProbeHttpSuccess(statusCode));
        }

        [Theory]
        [InlineData("<!DOCTYPE html><html></html>", "text/html", false)]
        [InlineData("   {\"errors\":[{\"code\":0}]}", "application/json", false)]
        [InlineData("   <html>error</html>", null, false)]
        [InlineData("", "image/png", false)]
        public void ThumbnailBatchHelper_IsPlausibleImageBytes_RejectsHtmlJsonAndEmpty(
            string body, string? contentType, bool expected)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(body);
            Assert.Equal(expected, ThumbnailBatchHelper.IsPlausibleImageBytes(bytes, contentType));
        }

        private const string ValidCurrencyJson = "{\"robux\":12345}";
        private const string ZeroCurrencyJson = "{\"robux\":0}";

        [Fact]
        public void ThumbnailBatchHelper_IsPlausibleImageBytes_RequiresRealImageMagicHeaders()
        {
            Assert.True(ThumbnailBatchHelper.IsPlausibleImageBytes(
                new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, "image/png"));
            Assert.True(ThumbnailBatchHelper.IsPlausibleImageBytes(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, "image/jpeg"));
            Assert.True(ThumbnailBatchHelper.IsPlausibleImageBytes(Encoding.ASCII.GetBytes("GIF89a\x01\x00"), "image/gif"));
            Assert.True(ThumbnailBatchHelper.IsPlausibleImageBytes(Encoding.ASCII.GetBytes("GIF87a\x01\x00"), "image/gif"));
            Assert.True(ThumbnailBatchHelper.IsPlausibleImageBytes(Encoding.ASCII.GetBytes("RIFF\x00\x00\x00\x00WEBP"), "image/webp"));

            Assert.False(ThumbnailBatchHelper.IsPlausibleImageBytes(new byte[] { 1, 2, 3, 4 }, "application/octet-stream"));
            Assert.False(ThumbnailBatchHelper.IsPlausibleImageBytes(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, "image/png"));
            Assert.False(ThumbnailBatchHelper.IsPlausibleImageBytes(new byte[] { 0xFF, 0xD8 }, "image/jpeg"));
            Assert.False(ThumbnailBatchHelper.IsPlausibleImageBytes(Encoding.ASCII.GetBytes("GIF89"), "image/gif"));
            Assert.False(ThumbnailBatchHelper.IsPlausibleImageBytes(Encoding.ASCII.GetBytes("RIFF\x00\x00\x00\x00WEB"), "image/webp"));
            Assert.False(ThumbnailBatchHelper.IsPlausibleImageBytes(
                new byte[] { (byte)' ', 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, "image/png"));
        }

        [Fact]
        public void Win32NativeHelper_OpenSafeProcess_InvalidPidReturnsInvalidHandleAndDisposesSafely()
        {
            using var handle = Win32NativeHelper.OpenSafeProcess(0x1000, 0);
            Assert.NotNull(handle);
            Assert.True(handle.IsInvalid);
            Assert.False(handle.IsClosed);
            handle.Dispose();
            Assert.True(handle.IsClosed);
        }

        [Fact]
        public void RobloxEconomyService_TryParseCurrencyBalance_ParsesValidJsonAndRejectsInvalidOrHtml()
        {
            Assert.True(RobloxEconomyService.TryParseCurrencyBalance(ValidCurrencyJson, out long robux));
            Assert.Equal(12345, robux);

            Assert.True(RobloxEconomyService.TryParseCurrencyBalance(ZeroCurrencyJson, out long zeroRobux));
            Assert.Equal(0, zeroRobux);

            Assert.False(RobloxEconomyService.TryParseCurrencyBalance(null, out long nullRobux));
            Assert.Equal(0, nullRobux);
            Assert.False(RobloxEconomyService.TryParseCurrencyBalance("   ", out _));
            Assert.False(RobloxEconomyService.TryParseCurrencyBalance("<html><body>Login</body></html>", out _));
            Assert.False(RobloxEconomyService.TryParseCurrencyBalance("not-json", out _));
        }

        [Fact]
        public void RobloxProcessIdentityHelper_BrowserTrackerArgRegex_ExtractsTrackerId()
        {
            var match = RobloxProcessIdentityHelper.BrowserTrackerArgRegex().Match("RobloxPlayerBeta.exe -t ticket -b 123456789 -j url");
            Assert.True(match.Success);
            Assert.Equal("123456789", match.Groups[1].Value);

            var noMatch = RobloxProcessIdentityHelper.BrowserTrackerArgRegex().Match("RobloxPlayerBeta.exe --app");
            Assert.False(noMatch.Success);
        }

        [Fact]
        public void ClientSettingsPatcher_GetDefaultRobloxVersionRootsAndNormalizePath_ReturnExpectedValues()
        {
            string[] roots = ClientSettingsPatcher.GetDefaultRobloxVersionRoots();
            Assert.NotEmpty(roots);
            Assert.Contains(roots, r => r.EndsWith(Path.Combine("Roblox", "Versions"), StringComparison.OrdinalIgnoreCase));

            Assert.Equal(
                @"C:\Roblox\Versions\version-123\RobloxPlayerBeta.exe",
                ClientSettingsPatcher.NormalizeRegistryExecutablePath("  \"C:\\Roblox\\Versions\\version-123\\RobloxPlayerBeta.exe\",0  "));
            Assert.Equal(
                string.Empty,
                ClientSettingsPatcher.NormalizeRegistryExecutablePath("   "));
        }

        [Fact]
        public void WebhookNotificationService_ResolveRetryAfterDelay_ParsesAndClampsHeaders()
        {
            using var noHeader = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            Assert.Equal(TimeSpan.FromMilliseconds(1000), WebhookNotificationService.ResolveRetryAfterDelay(noHeader));

            using var withRetryAfter = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            withRetryAfter.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(2));
            Assert.Equal(TimeSpan.FromSeconds(2), WebhookNotificationService.ResolveRetryAfterDelay(withRetryAfter));

            using var withResetHeader = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            withResetHeader.Headers.TryAddWithoutValidation("X-RateLimit-Reset-After", "0.5");
            Assert.Equal(TimeSpan.FromMilliseconds(500), WebhookNotificationService.ResolveRetryAfterDelay(withResetHeader));

            using var extreme = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            extreme.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(60));
            Assert.Equal(TimeSpan.FromMilliseconds(5000), WebhookNotificationService.ResolveRetryAfterDelay(extreme));
        }

        [Theory]
        [InlineData("https://www.roblox.com/not-approved", true)]
        [InlineData("https://www.roblox.com/account-locked", true)]
        [InlineData("https://www.roblox.com/security-notification", true)]
        [InlineData("https://www.roblox.com/banned-users", true)]
        [InlineData("https://www.roblox.com/home", false)]
        [InlineData(null, false)]
        public void RobloxModerationHelper_IsModerationOrLockedUrl_ClassifiesExpectedPaths(string? url, bool expected)
        {
            Assert.Equal(expected, RobloxModerationHelper.IsModerationOrLockedUrl(url));
        }

        private const string QuickLoginEnterCodeTemplate = "{\"deviceInfo\":\"{0}\",\"location\":\"VN\"}";
        private const string EmptyJsonObjectResponse = "{}";

        [Fact]
        public void WebhookNotificationService_FormatDiscordPayload_DoesNotDuplicateDetailsFieldAndDescription()
        {
            string json = WebhookNotificationService.FormatDiscordPayload(new WebhookAlert
            {
                AccountUsername = "AlertUser",
                EventType = WebhookEventType.RateLimited,
                Description = "HTTP 429 hit",
                Timestamp = DateTimeOffset.UtcNow
            });

            Assert.Single(Regex.Matches(json, "HTTP 429 hit"));
            using var doc = JsonDocument.Parse(json);
            var embed = doc.RootElement.GetProperty("embeds")[0];
            Assert.Equal("HTTP 429 hit", embed.GetProperty("description").GetString());
            foreach (var field in embed.GetProperty("fields").EnumerateArray())
            {
                Assert.NotEqual("Details", field.GetProperty("name").GetString());
            }
        }

        [AvaloniaFact]
        public async Task RobloxSocialApiService_QuickLogInAsync_MarshalsPromptViaDispatcher()
        {
            string uniqueDevice = "TestDevice-" + Guid.NewGuid().ToString("N")[..8];
            string enterBody = QuickLoginEnterCodeTemplate.Replace("{0}", uniqueDevice, StringComparison.Ordinal);
            string promptKey = $"Quick Log In.Please confirm you are logging in with this device.Device: {uniqueDevice}\nLocation: VN";
            string promptHash = CryptoHelper.SHA256(promptKey);

            var handler = new FakeHttpHandler()
                .Enqueue(HttpStatusCode.OK, enterBody)
                .Enqueue(HttpStatusCode.OK, EmptyJsonObjectResponse);

            AppServices.Override<IRobloxApiClient>(new RobloxApiClient(handler));
            RobloxAccountSessionClient.TestHttpClientFactory = _ => new HttpClient(handler);

            bool? promptOnUiThread = null;
            var fakeDialogs = Substitute.For<IDialogService>();
            fakeDialogs.ShowConfirmation(Arg.Any<string>(), Arg.Any<string>())
                .Returns(_ =>
                {
                    promptOnUiThread = Dispatcher.UIThread.CheckAccess();
                    return true;
                });
            using var dialogScope = AppServices.Override<IDialogService>(fakeDialogs);

            var account = new Account
            {
                Username = "QuickLoginUiUser",
                UserID = 4321,
                SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.--" + new string('B', 80),
                CSRFToken = "csrf-qli",
                TokenSet = DateTime.Now
            };

            try
            {
                bool ok = await Task.Run(() =>
                    RobloxSocialApiService.Instance.QuickLogInAsync(account, "123456", CancellationToken.None));

                Assert.True(ok);
                Assert.True(promptOnUiThread);
            }
            finally
            {
                AppServices.PromptsSettings.RemoveProperty(promptHash);
            }
        }

        [AvaloniaFact]
        public void FlagIconService_GetFlagImage_RendersEuDefsAndUseStars()
        {
            var img = Assert.IsType<DrawingImage>(FlagIconService.GetFlagImage("eu"));
            var rootGroup = Assert.IsType<DrawingGroup>(img.Drawing);

            // Expect transparent aspect rect + blue background + 12 rendered stars via <defs>/<use>.
            Assert.True(rootGroup.Children.Count > 10, $"Expected eu.svg <defs>/<use> stars to render, got {rootGroup.Children.Count} children.");
        }
    }

    [Collection("SerialGlobalState")]
    public sealed class P2ToastRemediationTests : IDisposable
    {
        private readonly Action<Action>? _originalDispatcher = AppDispatcher.CustomDispatcher;

        public void Dispose()
        {
            AppDispatcher.CustomDispatcher = _originalDispatcher;
            ToastService.ClearAll();
        }

        [Fact]
        public void ToastService_CoalescedToast_CancelsPriorAutoDismissTimerAndClearAllCleansUp()
        {
            AppDispatcher.CustomDispatcher = action => action();
            ToastService.ClearAll();

            ToastService.ShowInfo("CoalesceKey", "SameBody", durationSeconds: 30.0);
            ToastItem item = Assert.Single(ToastService.ActiveToasts);
            CancellationTokenSource? firstCts = item.AutoDismissCts;
            Assert.NotNull(firstCts);
            Assert.False(firstCts!.IsCancellationRequested);

            ToastService.ShowInfo("CoalesceKey", "SameBody", durationSeconds: 30.0);
            ToastItem sameItem = Assert.Single(ToastService.ActiveToasts);
            Assert.Same(item, sameItem);

            CancellationTokenSource? secondCts = sameItem.AutoDismissCts;
            Assert.NotNull(secondCts);
            Assert.NotSame(firstCts, secondCts);
            Assert.True(firstCts.IsCancellationRequested);
            Assert.False(secondCts!.IsCancellationRequested);

            ToastService.ClearAll();
            Assert.Empty(ToastService.ActiveToasts);
            Assert.True(secondCts.IsCancellationRequested);
        }
    }
}

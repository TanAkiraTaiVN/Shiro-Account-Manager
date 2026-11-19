#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
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
            Account.TestHttpClientFactory = null;
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
            Account.TestHttpClientFactory = _ => new HttpClient(handler);

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

        [Fact]
        public void ThumbnailBatchHelper_IsPlausibleImageBytes_AllowsBinaryImagePayloads()
        {
            Assert.True(ThumbnailBatchHelper.IsPlausibleImageBytes(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, "image/png"));
            Assert.True(ThumbnailBatchHelper.IsPlausibleImageBytes(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, "image/jpeg"));
            Assert.True(ThumbnailBatchHelper.IsPlausibleImageBytes(new byte[] { 1, 2, 3, 4 }, "application/octet-stream"));
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

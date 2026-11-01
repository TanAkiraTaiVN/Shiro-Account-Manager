using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Classes;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    public class WebhookNotificationTests
    {
        [Fact]
        public void FormatDiscordPayload_CreatesValidEmbedJson()
        {
            var alert = new WebhookAlert
            {
                AccountUsername = "Player1",
                EventType = WebhookEventType.Disconnected,
                Description = "Roblox client disconnected with Error 277.",
                Timestamp = DateTimeOffset.UtcNow
            };

            string json = WebhookNotificationService.FormatDiscordPayload(alert);

            Assert.Contains("\"embeds\":", json);
            Assert.Contains("Player1", json);
            Assert.Contains("Error 277", json);

            // Verify it parses as valid JSON
            using var doc = JsonDocument.Parse(json);
            Assert.True(doc.RootElement.TryGetProperty("embeds", out var embeds));
            Assert.True(embeds.GetArrayLength() > 0);
        }

        [Fact]
        public void FormatTelegramMessage_EscapesMarkdownCharactersCorrectly()
        {
            var alert = new WebhookAlert
            {
                AccountUsername = "Player_1",
                EventType = WebhookEventType.RateLimited,
                Description = "HTTP 429 received. Waiting 60s.",
                Timestamp = DateTimeOffset.UtcNow
            };

            string message = WebhookNotificationService.FormatTelegramMessage(alert);

            Assert.Contains("Player", message);
            Assert.Contains("429", message);
            // Verify underscore in Player_1 is escaped
            Assert.Contains(@"Player\_1", message);
        }

        [Fact]
        public void EscapeTelegramMarkdown_EscapesAllSpecialCharacters()
        {
            string input = @"_ * [ ] ( ) ~ \ > # + - = | { } . !";
            string escaped = WebhookNotificationService.EscapeTelegramMarkdown(input);

            Assert.Equal(@"\_ \* \[ \] \( \) \~ \\ \> \# \+ \- \= \| \{ \} \. \!", escaped);
        }

        [Fact]
        public async Task SendAlertAsync_WithNullOrEmptyConfig_CompletesGracefully()
        {
            // Null alert should be a no-op
            await WebhookNotificationService.SendAlertAsync(null!);

            // When no Discord or Telegram endpoint is configured, should exit cleanly
            var alert = new WebhookAlert
            {
                AccountUsername = "GhostUser",
                EventType = WebhookEventType.Info,
                Description = "Informational test message",
                Timestamp = DateTimeOffset.UtcNow
            };

            var ex = await Record.ExceptionAsync(() => WebhookNotificationService.SendAlertAsync(alert));
            Assert.Null(ex);
        }

        [Fact]
        public void AppSettingsKeys_ContainsExpectedWebhookKeys()
        {
            Assert.Equal("DiscordWebhookUrl", AppSettingsKeys.General.DiscordWebhookUrl);
            Assert.Equal("TelegramBotToken", AppSettingsKeys.General.TelegramBotToken);
            Assert.Equal("TelegramChatId", AppSettingsKeys.General.TelegramChatId);
            Assert.Equal("NotifyOnDisconnect", AppSettingsKeys.General.NotifyOnDisconnect);
            Assert.Equal("NotifyOnModeration", AppSettingsKeys.General.NotifyOnModeration);
        }

        [Fact]
        public void AutoReconnectService_NotifyDisconnected_DispatchesWebhookAlert()
        {
            var dispatchedAlerts = new List<WebhookAlert>();
            void OnAlert(WebhookAlert a) => dispatchedAlerts.Add(a);

            WebhookNotificationService.AlertDispatched += OnAlert;
            try
            {
                using var service = new AutoReconnectService();
                var account = new Account { Username = "TestDisconnectUser" };
                service.RegisterSession(account, 12345, "job-1", false, false, 7777);

                service.NotifyDisconnected(7777, 277);

                // Wait briefly for asynchronous dispatch
                Thread.Sleep(50);

                Assert.Contains(dispatchedAlerts, a =>
                    a.AccountUsername == "TestDisconnectUser" &&
                    a.EventType == WebhookEventType.Disconnected &&
                    a.Description != null && a.Description.Contains("277"));
            }
            finally
            {
                WebhookNotificationService.AlertDispatched -= OnAlert;
            }
        }

        [Fact]
        public void AutoReconnectService_MaxRetriesExceeded_DispatchesWebhookAlert()
        {
            var dispatchedAlerts = new List<WebhookAlert>();
            void OnAlert(WebhookAlert a) => dispatchedAlerts.Add(a);

            WebhookNotificationService.AlertDispatched += OnAlert;
            try
            {
                using var service = new AutoReconnectService();
                var account = new Account { Username = "TestMaxRetryUser" };
                service.RegisterSession(account, 54321, "job-2", false, false, 8888);

                // Trigger MaxRetriesExceeded event
                service.NotifyMaxRetriesExceededForTesting(account.Username);

                Thread.Sleep(50);

                Assert.Contains(dispatchedAlerts, a =>
                    a.AccountUsername == "TestMaxRetryUser" &&
                    a.EventType == WebhookEventType.MaxRetriesExceeded);
            }
            finally
            {
                WebhookNotificationService.AlertDispatched -= OnAlert;
            }
        }

        [Fact]
        public void RobloxRateLimitCoordinator_RecordRateLimitHit_DispatchesRateLimitedAlert()
        {
            var dispatchedAlerts = new List<WebhookAlert>();
            void OnAlert(WebhookAlert a) => dispatchedAlerts.Add(a);

            WebhookNotificationService.AlertDispatched += OnAlert;
            try
            {
                RobloxRateLimitCoordinator.Instance.ResetBackoff();
                RobloxRateLimitCoordinator.Instance.RecordRateLimitHit(RateLimitCategory.SessionCheck, defaultBackoffSeconds: 5);

                Assert.Contains(dispatchedAlerts, a =>
                    a.EventType == WebhookEventType.RateLimited &&
                    a.Description != null &&
                    a.Description.Contains("HTTP 429 Rate-Limit"));
            }
            finally
            {
                WebhookNotificationService.AlertDispatched -= OnAlert;
                RobloxRateLimitCoordinator.Instance.ResetBackoff();
            }
        }

        [Fact]
        public void EnqueueAlert_BoundedChannel_EnqueuesSuccessfully()
        {
            var alert = new WebhookAlert
            {
                AccountUsername = "ChannelQueueUser",
                EventType = WebhookEventType.Info,
                Description = "Channel queuing test",
                Timestamp = DateTimeOffset.UtcNow
            };

            var ex = Record.Exception(() => WebhookNotificationService.EnqueueAlert(alert));
            Assert.Null(ex);
        }
    }
}

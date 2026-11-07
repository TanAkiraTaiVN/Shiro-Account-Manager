#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public class NetworkServicesBehaviorTests
    {
        [Fact]
        public void WebhookNotificationService_EscapeTelegramMarkdown_EscapesAllSpecialCharacters()
        {
            Assert.Equal(string.Empty, WebhookNotificationService.EscapeTelegramMarkdown(null));
            Assert.Equal(string.Empty, WebhookNotificationService.EscapeTelegramMarkdown(string.Empty));

            string specials = "_*[]()~`>#+-=|{}.!\\";
            string escaped = WebhookNotificationService.EscapeTelegramMarkdown(specials);
            Assert.Equal(specials.Length * 2, escaped.Length);
            foreach (char c in specials)
            {
                Assert.Contains("\\" + c, escaped);
            }

            string normal = "Hello World 123";
            Assert.Equal("Hello World 123", WebhookNotificationService.EscapeTelegramMarkdown(normal));
        }

        [Fact]
        public void WebhookNotificationService_FormatDiscordPayload_HandlesAllEventTypes()
        {
            Assert.Equal("{}", WebhookNotificationService.FormatDiscordPayload(null!));

            var types = new[]
            {
                WebhookEventType.Disconnected,
                WebhookEventType.RateLimited,
                WebhookEventType.ModerationWarning,
                WebhookEventType.MaxRetriesExceeded,
                WebhookEventType.Info
            };

            foreach (var type in types)
            {
                var alert = new WebhookAlert
                {
                    AccountUsername = "AlertUser",
                    EventType = type,
                    Description = "Test event description",
                    Timestamp = DateTimeOffset.UtcNow
                };

                string payload = WebhookNotificationService.FormatDiscordPayload(alert);
                Assert.Contains("AlertUser", payload);
                Assert.Contains(type.ToString(), payload);
                Assert.Contains("Test event description", payload);
            }
        }

        [Fact]
        public void WebhookNotificationService_FormatTelegramMessage_GeneratesMarkdown()
        {
            Assert.Equal(string.Empty, WebhookNotificationService.FormatTelegramMessage(null!));

            var alert = new WebhookAlert
            {
                AccountUsername = "TgUser",
                EventType = WebhookEventType.RateLimited,
                Description = "Too many requests",
                Timestamp = DateTimeOffset.UtcNow
            };

            string msg = WebhookNotificationService.FormatTelegramMessage(alert);
            Assert.Contains("TgUser", msg);
            Assert.Contains("Rate Limited", msg);
            Assert.Contains("Too many requests", msg);
        }

        [Fact]
        public async Task WebhookNotificationService_PropertiesAndEnqueueAlert_DispatchesEvent()
        {
            WebhookNotificationService.DiscordWebhookUrl = "https://discord.com/api/webhooks/test";
            string? storedDiscordRaw = AppServices.GeneralSettings.Get(AppSettingsKeys.General.DiscordWebhookUrl);
            Assert.NotNull(storedDiscordRaw);
            Assert.StartsWith("dpapi:", storedDiscordRaw!, StringComparison.Ordinal);
            Assert.Equal("https://discord.com/api/webhooks/test", WebhookNotificationService.DiscordWebhookUrl);
            WebhookNotificationService.DiscordWebhookUrl = null;
            Assert.Null(WebhookNotificationService.DiscordWebhookUrl);

            WebhookNotificationService.TelegramBotToken = "12345:TOKEN";
            string? storedTelegramRaw = AppServices.GeneralSettings.Get(AppSettingsKeys.General.TelegramBotToken);
            Assert.NotNull(storedTelegramRaw);
            Assert.StartsWith("dpapi:", storedTelegramRaw!, StringComparison.Ordinal);
            Assert.Equal("12345:TOKEN", WebhookNotificationService.TelegramBotToken);
            WebhookNotificationService.TelegramBotToken = null;
            Assert.Null(WebhookNotificationService.TelegramBotToken);

            WebhookNotificationService.TelegramChatId = "-100123456";
            Assert.Equal("-100123456", WebhookNotificationService.TelegramChatId);
            WebhookNotificationService.TelegramChatId = null;
            Assert.Null(WebhookNotificationService.TelegramChatId);

            WebhookNotificationService.NotifyOnDisconnect = false;
            Assert.False(WebhookNotificationService.NotifyOnDisconnect);
            WebhookNotificationService.NotifyOnDisconnect = true;
            Assert.True(WebhookNotificationService.NotifyOnDisconnect);

            WebhookNotificationService.NotifyOnModeration = false;
            Assert.False(WebhookNotificationService.NotifyOnModeration);
            WebhookNotificationService.NotifyOnModeration = true;
            Assert.True(WebhookNotificationService.NotifyOnModeration);

            // Test EnqueueAlert event dispatch
            var receivedTcs = new TaskCompletionSource<WebhookAlert>(TaskCreationOptions.RunContinuationsAsynchronously);
            Action<WebhookAlert> handler = a => receivedTcs.TrySetResult(a);

            WebhookNotificationService.AlertDispatched += handler;
            try
            {
                var testAlert = new WebhookAlert
                {
                    AccountUsername = "QueueUser",
                    EventType = WebhookEventType.Info,
                    Description = "Dispatch verification"
                };

                WebhookNotificationService.EnqueueAlert(testAlert);
                var received = await Task.WhenAny(receivedTcs.Task, Task.Delay(1000));
                Assert.Same(receivedTcs.Task, received);
                var dispatched = await receivedTcs.Task;
                Assert.Equal("QueueUser", dispatched.AccountUsername);
                Assert.Equal(WebhookEventType.Info, dispatched.EventType);
            }
            finally
            {
                WebhookNotificationService.AlertDispatched -= handler;
            }
        }

        [Fact]
        public void DiscordRpcService_PresenceUpdates_AndLifecycle()
        {
            using var rpc = new DiscordRpcService();

            // Properties
            rpc.IsEnabled = false;
            Assert.False(rpc.IsEnabled);
            rpc.IsEnabled = true;
            Assert.True(rpc.IsEnabled);

            // 1. UpdatePresence with in-game and server join button
            rpc.UpdatePresence("Blox Fruits", 123456, "Job-777", totalAccounts: 10, activeInGameAccounts: 3);

            // 2. UpdatePresence with no game (managing accounts)
            rpc.UpdatePresence(null, 0, null, totalAccounts: 5, activeInGameAccounts: 0);

            // 3. UpdateCurrentPresence and ClearPresence
            rpc.UpdateCurrentPresence();
            rpc.ClearPresence();

            rpc.Stop();
            Assert.False(rpc.IsConnected);
        }

        [Fact]
        public void WebhookNotificationService_SecretProperties_FailClosedToNullOnCorruptDpapi_AndReadLegacyPlaintext()
        {
            try
            {
                AppServices.GeneralSettings.Set(AppSettingsKeys.General.DiscordWebhookUrl, "dpapi:corrupt-base64!!!");
                Assert.Null(WebhookNotificationService.DiscordWebhookUrl);

                AppServices.GeneralSettings.Set(AppSettingsKeys.General.TelegramBotToken, "dpapi:corrupt-base64!!!");
                Assert.Null(WebhookNotificationService.TelegramBotToken);

                AppServices.GeneralSettings.Set(AppSettingsKeys.General.DiscordWebhookUrl, "   ");
                Assert.Null(WebhookNotificationService.DiscordWebhookUrl);

                AppServices.GeneralSettings.Set(AppSettingsKeys.General.DiscordWebhookUrl, "https://discord.com/api/webhooks/legacy");
                Assert.Equal("https://discord.com/api/webhooks/legacy", WebhookNotificationService.DiscordWebhookUrl);
            }
            finally
            {
                WebhookNotificationService.DiscordWebhookUrl = null;
                WebhookNotificationService.TelegramBotToken = null;
            }
        }
    }
}

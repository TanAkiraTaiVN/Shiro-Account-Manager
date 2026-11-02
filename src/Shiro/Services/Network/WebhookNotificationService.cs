#nullable enable
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Shiro.Services
{
    public enum WebhookEventType
    {
        Disconnected,
        RateLimited,
        ModerationWarning,
        MaxRetriesExceeded,
        Info
    }

    public sealed class WebhookAlert
    {
        public string? AccountUsername { get; set; }
        public WebhookEventType EventType { get; set; }
        public string? Description { get; set; }
        public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    }

    public static class WebhookNotificationService
    {
        private static readonly SemaphoreSlim Gate = new(1, 1);
        private static DateTimeOffset _lastDispatchUtc = DateTimeOffset.MinValue;

        private static readonly Channel<WebhookAlert> AlertChannel = Channel.CreateBounded<WebhookAlert>(
            new BoundedChannelOptions(100)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            });

        static WebhookNotificationService()
        {
            _ = Task.Run(ProcessAlertsLoopAsync);
        }

        public static event Action<WebhookAlert>? AlertDispatched;

        public static string? DiscordWebhookUrl
        {
            get => AppSettings.General.Get(AppSettingsKeys.General.DiscordWebhookUrl);
            set
            {
                if (string.IsNullOrWhiteSpace(value))
                    AppSettings.General.RemoveProperty(AppSettingsKeys.General.DiscordWebhookUrl);
                else
                    AppSettings.General.Set(AppSettingsKeys.General.DiscordWebhookUrl, value);
            }
        }

        public static string? TelegramBotToken
        {
            get => AppSettings.General.Get(AppSettingsKeys.General.TelegramBotToken);
            set
            {
                if (string.IsNullOrWhiteSpace(value))
                    AppSettings.General.RemoveProperty(AppSettingsKeys.General.TelegramBotToken);
                else
                    AppSettings.General.Set(AppSettingsKeys.General.TelegramBotToken, value);
            }
        }

        public static string? TelegramChatId
        {
            get => AppSettings.General.Get(AppSettingsKeys.General.TelegramChatId);
            set
            {
                if (string.IsNullOrWhiteSpace(value))
                    AppSettings.General.RemoveProperty(AppSettingsKeys.General.TelegramChatId);
                else
                    AppSettings.General.Set(AppSettingsKeys.General.TelegramChatId, value);
            }
        }

        public static bool NotifyOnDisconnect
        {
            get => AppSettings.General.GetBool(AppSettingsKeys.General.NotifyOnDisconnect, true);
            set => AppSettings.General.Set(AppSettingsKeys.General.NotifyOnDisconnect, value ? "true" : "false");
        }

        public static bool NotifyOnModeration
        {
            get => AppSettings.General.GetBool(AppSettingsKeys.General.NotifyOnModeration, true);
            set => AppSettings.General.Set(AppSettingsKeys.General.NotifyOnModeration, value ? "true" : "false");
        }

        public static string EscapeTelegramMarkdown(string? text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;

            var sb = new StringBuilder(text.Length * 2);
            foreach (char c in text)
            {
                switch (c)
                {
                    case '_':
                    case '*':
                    case '[':
                    case ']':
                    case '(':
                    case ')':
                    case '~':
                    case '`':
                    case '>':
                    case '#':
                    case '+':
                    case '-':
                    case '=':
                    case '|':
                    case '{':
                    case '}':
                    case '.':
                    case '!':
                    case '\\':
                        sb.Append('\\').Append(c);
                        break;
                    default:
                        sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        public static string FormatDiscordPayload(WebhookAlert alert)
        {
            if (alert == null) return "{}";

            int color = alert.EventType switch
            {
                WebhookEventType.Disconnected => 15158332,      // #E74C3C Red
                WebhookEventType.RateLimited => 15105570,       // #E67E22 Orange
                WebhookEventType.ModerationWarning => 10038562, // #992D22 Dark Red
                WebhookEventType.MaxRetriesExceeded => 9841954, // #962D22 Crimson
                _ => 3447003                                    // #3498DB Blue
            };

            string title = alert.EventType switch
            {
                WebhookEventType.Disconnected => "⚠️ Roblox Client Disconnected",
                WebhookEventType.RateLimited => "⏳ Rate Limit Hit (HTTP 429)",
                WebhookEventType.ModerationWarning => "🚫 Moderation / Ban Detected",
                WebhookEventType.MaxRetriesExceeded => "🛑 Max Reconnect Retries Exceeded",
                _ => "ℹ️ Shiro Account Alert"
            };

            var fields = new List<DiscordWebhookField>();
            if (!string.IsNullOrWhiteSpace(alert.AccountUsername))
            {
                fields.Add(new DiscordWebhookField("Account", alert.AccountUsername, true));
            }

            fields.Add(new DiscordWebhookField("Event", alert.EventType.ToString(), true));

            if (!string.IsNullOrWhiteSpace(alert.Description))
            {
                fields.Add(new DiscordWebhookField("Details", alert.Description, false));
            }

            var embed = new DiscordWebhookEmbed(
                title,
                alert.Description,
                color,
                fields,
                alert.Timestamp.ToString("o"));

            var payload = new DiscordWebhookPayload(new List<DiscordWebhookEmbed> { embed });

            return JsonSerializer.Serialize(payload, ShiroAppJson.Default.DiscordWebhookPayload);
        }

        public static string FormatTelegramMessage(WebhookAlert alert)
        {
            if (alert == null) return string.Empty;

            string title = alert.EventType switch
            {
                WebhookEventType.Disconnected => "⚠️ Roblox Disconnected",
                WebhookEventType.RateLimited => "⏳ Rate Limited (HTTP 429)",
                WebhookEventType.ModerationWarning => "🚫 Moderation / Ban Alert",
                WebhookEventType.MaxRetriesExceeded => "🛑 Max Retries Exceeded",
                _ => "ℹ️ Shiro Alert"
            };

            var sb = new StringBuilder();
            sb.Append('*').Append(EscapeTelegramMarkdown(title)).Append("*\n\n");

            if (!string.IsNullOrWhiteSpace(alert.AccountUsername))
            {
                sb.Append("*Account:* ").Append(EscapeTelegramMarkdown(alert.AccountUsername)).Append('\n');
            }

            sb.Append("*Event:* ").Append(EscapeTelegramMarkdown(alert.EventType.ToString())).Append('\n');

            if (!string.IsNullOrWhiteSpace(alert.Description))
            {
                sb.Append("*Details:* ").Append(EscapeTelegramMarkdown(alert.Description)).Append('\n');
            }

            sb.Append("*Time:* ").Append(EscapeTelegramMarkdown(alert.Timestamp.ToString("yyyy-MM-dd HH:mm:ss UTC")));
            return sb.ToString();
        }

        private static bool ShouldSend(WebhookAlert alert)
        {
            if (alert.EventType == WebhookEventType.Disconnected && !NotifyOnDisconnect)
                return false;
            if (alert.EventType == WebhookEventType.ModerationWarning && !NotifyOnModeration)
                return false;
            return true;
        }

        private static void NotifyDispatched(WebhookAlert alert)
        {
            try
            {
                AlertDispatched?.Invoke(alert);
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"[Webhook] AlertDispatched subscriber error: {ex.Message}");
            }
        }

        public static void EnqueueAlert(WebhookAlert alert)
        {
            if (alert == null) return;
            if (!ShouldSend(alert)) return;

            NotifyDispatched(alert);
            AlertChannel.Writer.TryWrite(alert);
        }

        public static async Task SendAlertAsync(WebhookAlert alert, CancellationToken ct = default)
        {
            if (alert == null) return;
            if (!ShouldSend(alert)) return;

            NotifyDispatched(alert);
            await DispatchSingleAlertAsync(alert, ct).ConfigureAwait(false);
        }

        private static async Task ProcessAlertsLoopAsync()
        {
            var reader = AlertChannel.Reader;
            try
            {
                while (await reader.WaitToReadAsync().ConfigureAwait(false))
                {
                    while (reader.TryRead(out var alert))
                    {
                        if (alert != null)
                        {
                            await DispatchSingleAlertAsync(alert).ConfigureAwait(false);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"[Webhook] Alert processing loop ended: {ex.Message}");
            }
        }

        private static async Task DispatchSingleAlertAsync(WebhookAlert alert, CancellationToken ct = default)
        {
            string? discordUrl = DiscordWebhookUrl;
            string? tgToken = TelegramBotToken;
            string? tgChatId = TelegramChatId;

            bool hasDiscord = !string.IsNullOrWhiteSpace(discordUrl);
            bool hasTelegram = !string.IsNullOrWhiteSpace(tgToken) && !string.IsNullOrWhiteSpace(tgChatId);

            if (!hasDiscord && !hasTelegram)
                return;

            try
            {
                await Gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var elapsed = DateTimeOffset.UtcNow - _lastDispatchUtc;
                    if (elapsed < TimeSpan.FromSeconds(1))
                    {
                        var delay = TimeSpan.FromSeconds(1) - elapsed;
                        await Task.Delay(delay, ct).ConfigureAwait(false);
                    }

                    var httpClient = SharedHttpClient.Get();

                    if (hasDiscord)
                    {
                        await DispatchDiscordAsync(httpClient, discordUrl!, alert, ct).ConfigureAwait(false);
                    }

                    if (hasTelegram)
                    {
                        await DispatchTelegramAsync(httpClient, tgToken!, tgChatId!, alert, ct).ConfigureAwait(false);
                    }

                    _lastDispatchUtc = DateTimeOffset.UtcNow;
                }
                finally
                {
                    Gate.Release();
                }
            }
            catch (OperationCanceledException ex)
            {
                Program.Logger?.Debug($"[Webhook] Alert dispatch canceled: {ex.Message}");
            }
            catch (Exception ex)
            {
                Program.Logger?.Warn($"[Webhook] Alert dispatch failed: {ex.Message}");
            }
        }

        private static async Task DispatchDiscordAsync(HttpClient client, string webhookUrl, WebhookAlert alert, CancellationToken ct)
        {
            try
            {
                string json = FormatDiscordPayload(alert);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var response = await client.PostAsync(webhookUrl, content, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    Program.Logger?.Warn($"[Webhook] Discord returned {(int)response.StatusCode} {response.ReasonPhrase}");
                }
            }
            catch (Exception ex)
            {
                Program.Logger?.Warn($"[Webhook] Discord send failed: {ex.Message}");
            }
        }

        private static async Task DispatchTelegramAsync(HttpClient client, string token, string chatId, WebhookAlert alert, CancellationToken ct)
        {
            try
            {
                string text = FormatTelegramMessage(alert);
                string endpoint = $"https://api.telegram.org/bot{token}/sendMessage";
                var payload = new TelegramSendMessagePayload(chatId, text, "MarkdownV2");
                string json = JsonSerializer.Serialize(payload, ShiroAppJson.Default.TelegramSendMessagePayload);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var response = await client.PostAsync(endpoint, content, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    Program.Logger?.Warn($"[Webhook] Telegram returned {(int)response.StatusCode} {response.ReasonPhrase}");
                }
            }
            catch (Exception ex)
            {
                Program.Logger?.Warn($"[Webhook] Telegram send failed: {ex.Message}");
            }
        }
    }
}

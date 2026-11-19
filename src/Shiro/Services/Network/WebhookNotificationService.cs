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
        private static IAppLogger _logger = AppServices.Logger;
        public static IAppLogger Logger
        {
            get => _logger;
            set => _logger = value ?? throw new ArgumentNullException(nameof(value));
        }

        private static ISettingsService? _settingsOverride;
        private static ISettingsService _settings => _settingsOverride ?? AppServices.GeneralSettings;
        public static ISettingsService Settings
        {
            get => _settings;
            set => _settingsOverride = value ?? throw new ArgumentNullException(nameof(value));
        }

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
            get
            {
                string? raw = _settings.Get(AppSettingsKeys.General.DiscordWebhookUrl);
                if (string.IsNullOrWhiteSpace(raw)) return null;
                string plain = CryptoHelper.UnprotectSecretString(raw);
                return string.IsNullOrEmpty(plain) ? null : plain;
            }
            set
            {
                if (string.IsNullOrWhiteSpace(value))
                    _settings.RemoveProperty(AppSettingsKeys.General.DiscordWebhookUrl);
                else
                    _settings.Set(AppSettingsKeys.General.DiscordWebhookUrl, CryptoHelper.ProtectSecretString(value));
            }
        }

        public static string? TelegramBotToken
        {
            get
            {
                string? raw = _settings.Get(AppSettingsKeys.General.TelegramBotToken);
                if (string.IsNullOrWhiteSpace(raw)) return null;
                string plain = CryptoHelper.UnprotectSecretString(raw);
                return string.IsNullOrEmpty(plain) ? null : plain;
            }
            set
            {
                if (string.IsNullOrWhiteSpace(value))
                    _settings.RemoveProperty(AppSettingsKeys.General.TelegramBotToken);
                else
                    _settings.Set(AppSettingsKeys.General.TelegramBotToken, CryptoHelper.ProtectSecretString(value));
            }
        }

        public static string? TelegramChatId
        {
            get => _settings.Get(AppSettingsKeys.General.TelegramChatId);
            set
            {
                if (string.IsNullOrWhiteSpace(value))
                    _settings.RemoveProperty(AppSettingsKeys.General.TelegramChatId);
                else
                    _settings.Set(AppSettingsKeys.General.TelegramChatId, value);
            }
        }

        public static bool NotifyOnDisconnect
        {
            get => _settings.GetBool(AppSettingsKeys.General.NotifyOnDisconnect, true);
            set => _settings.Set(AppSettingsKeys.General.NotifyOnDisconnect, value ? "true" : "false");
        }

        public static bool NotifyOnModeration
        {
            get => _settings.GetBool(AppSettingsKeys.General.NotifyOnModeration, true);
            set => _settings.Set(AppSettingsKeys.General.NotifyOnModeration, value ? "true" : "false");
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
                fields.Add(new DiscordWebhookField("Account", TruncateText(alert.AccountUsername, 1024), true));
            }

            fields.Add(new DiscordWebhookField("Event", alert.EventType.ToString(), true));

            if (!string.IsNullOrWhiteSpace(alert.Description))
            {
                fields.Add(new DiscordWebhookField("Details", TruncateText(alert.Description, 1024), false));
            }

            var embed = new DiscordWebhookEmbed(
                title,
                string.IsNullOrWhiteSpace(alert.Description) ? null : TruncateText(alert.Description, 4096),
                color,
                fields,
                alert.Timestamp.ToString("o"));

            var payload = new DiscordWebhookPayload(new List<DiscordWebhookEmbed> { embed });

            return JsonSerializer.Serialize(payload, ShiroAppJson.Default.DiscordWebhookPayload);
        }

        private static string TruncateText(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
                return value;

            if (maxLength <= 3)
                return value[..maxLength];

            return value[..(maxLength - 3)] + "...";
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
                _logger.Debug($"[Webhook] AlertDispatched subscriber error: {ex.Message}");
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
                _logger.Debug($"[Webhook] Alert processing loop ended: {ex.Message}");
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

                    var httpClient = SharedHttpClient.GetNoRedirect();

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
                _logger.Debug($"[Webhook] Alert dispatch canceled: {ex.Message}");
            }
            catch (Exception ex)
            {
                _logger.Warn($"[Webhook] Alert dispatch failed: {ex.Message}");
            }
        }

        public static bool IsAllowedDiscordWebhookUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url) ||
                !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ||
                uri.Scheme != Uri.UriSchemeHttps ||
                !string.IsNullOrEmpty(uri.UserInfo) ||
                !uri.AbsolutePath.StartsWith("/api/webhooks/", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return string.Equals(uri.Host, "discord.com", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(uri.Host, "discordapp.com", StringComparison.OrdinalIgnoreCase) ||
                   uri.Host.EndsWith(".discord.com", StringComparison.OrdinalIgnoreCase) ||
                   uri.Host.EndsWith(".discordapp.com", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsValidTelegramBotToken(string? token)
        {
            if (string.IsNullOrWhiteSpace(token))
                return false;

            string trimmed = token.Trim();
            int colon = trimmed.IndexOf(':');
            if (colon <= 0 || colon == trimmed.Length - 1)
                return false;

            foreach (char c in trimmed.AsSpan(0, colon))
                if (!char.IsAsciiDigit(c)) return false;

            foreach (char c in trimmed.AsSpan(colon + 1))
                if (!char.IsAsciiLetterOrDigit(c) && c != '_' && c != '-') return false;

            return true;
        }

        private static async Task DispatchDiscordAsync(HttpClient client, string webhookUrl, WebhookAlert alert, CancellationToken ct)
        {
            if (!IsAllowedDiscordWebhookUrl(webhookUrl))
            {
                _logger.Warn("[Webhook] Blocked Discord webhook dispatch: URL must use HTTPS and target discord.com or discordapp.com.");
                return;
            }

            try
            {
                string json = FormatDiscordPayload(alert);
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    using var content = new StringContent(json, Encoding.UTF8, "application/json");
                    using var response = await client.PostAsync(webhookUrl, content, ct).ConfigureAwait(false);
                    if (response.IsSuccessStatusCode)
                        return;

                    if (attempt == 0 && (int)response.StatusCode == 429)
                    {
                        TimeSpan delay = ResolveRetryAfterDelay(response);
                        await Task.Delay(delay, ct).ConfigureAwait(false);
                        continue;
                    }

                    _logger.Warn($"[Webhook] Discord returned {(int)response.StatusCode} {response.ReasonPhrase}");
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.Warn($"[Webhook] Discord send failed: {LogRedactor.Redact(ex.Message)}");
            }
        }

        internal static TimeSpan ResolveRetryAfterDelay(HttpResponseMessage response)
        {
            TimeSpan? delta = response.Headers.RetryAfter?.Delta;
            if (!delta.HasValue &&
                response.Headers.TryGetValues("X-RateLimit-Reset-After", out var values))
            {
                foreach (string raw in values)
                {
                    if (double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double secs) && secs > 0)
                    {
                        delta = TimeSpan.FromSeconds(secs);
                        break;
                    }
                }
            }

            double clampedMs = Math.Clamp(delta?.TotalMilliseconds ?? 1000.0, 250.0, 5000.0);
            return TimeSpan.FromMilliseconds(clampedMs);
        }

        private static async Task DispatchTelegramAsync(HttpClient client, string token, string chatId, WebhookAlert alert, CancellationToken ct)
        {
            if (!IsValidTelegramBotToken(token))
            {
                _logger.Warn("[Webhook] Blocked Telegram dispatch: bot token format is invalid.");
                return;
            }

            try
            {
                string text = FormatTelegramMessage(alert);
                string endpoint = $"https://api.telegram.org/bot{token.Trim()}/sendMessage";
                var payload = new TelegramSendMessagePayload(chatId, text, "MarkdownV2");
                string json = JsonSerializer.Serialize(payload, ShiroAppJson.Default.TelegramSendMessagePayload);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var response = await client.PostAsync(endpoint, content, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.Warn($"[Webhook] Telegram returned {(int)response.StatusCode} {response.ReasonPhrase}");
                }
            }
            catch (Exception ex)
            {
                _logger.Warn($"[Webhook] Telegram send failed: {LogRedactor.Redact(ex.Message)}");
            }
        }
    }
}

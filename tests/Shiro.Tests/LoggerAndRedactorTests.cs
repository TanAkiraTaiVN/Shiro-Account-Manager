using System;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    public class LoggerAndRedactorTests
    {
        [Theory]
        [InlineData("Multi-Instance Ready")]
        [InlineData("[AntiAfk] Kept alive 4 Roblox client(s) via silent background pulse.")]
        [InlineData("Smart Mute Active (Background clients muted)")]
        [InlineData("Launching account Farmer01 into place 123456")]
        public void Redact_OrdinaryLogMessages_ShortCircuitsAndReturnsSameReference(string message)
        {
            Assert.False(LogRedactor.MayContainSensitiveData(message.AsSpan()));
            string redacted = LogRedactor.Redact(message);
            Assert.Same(message, redacted);
        }

        [Fact]
        public void Redact_AllSensitivePatterns_AreAccuratelyScrubbed()
        {
            string roblosec = "Token: _|WARNING:-DO-NOT-SHARE-THIS.--Sharing-this-will-allow-someone|_ABCDEF123456";
            Assert.Contains("[REDACTED_ROBLOSECURITY]", LogRedactor.Redact(roblosec));

            string cookieHeader = "Cookie: .ROBLOSECURITY=secret_cookie_value_123; path=/";
            Assert.Contains(".ROBLOSECURITY=[REDACTED_COOKIE]", LogRedactor.Redact(cookieHeader));

            string jsonSecret = "{\"password\":\"MySecretPass123\",\"username\":\"user1\"}";
            Assert.Contains("\"password\":\"[REDACTED]\"", LogRedactor.Redact(jsonSecret));

            string batHeader = "x-bound-auth-token: v1|abc123xyz";
            Assert.Contains("x-bound-auth-token:[REDACTED_BAT]", LogRedactor.Redact(batHeader));

            string querySecret = "https://example.com/api?token=secret999&id=1";
            Assert.Contains("?token=[REDACTED]", LogRedactor.Redact(querySecret));

            string bearer = "Authorization: Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9";
            Assert.Contains("Bearer [REDACTED_TOKEN]", LogRedactor.Redact(bearer));

            string proxyUrl = "Connected via socks5://admin:hunter2@127.0.0.1:1080";
            Assert.Contains("socks5://[REDACTED_PROXY_AUTH]@127.0.0.1:1080", LogRedactor.Redact(proxyUrl));

            string legacyBloxKey = "Using key BLOX-1234567890ABCDEF";
            Assert.Equal(legacyBloxKey, LogRedactor.Redact(legacyBloxKey));
        }

        [Theory]
        [InlineData("Webhook failed at https://discord.com/api/webhooks/1234567890/AbCdEfGh_1234-xyz", "AbCdEfGh_1234-xyz", "[REDACTED_WEBHOOK]")]
        [InlineData("Posted to https://discordapp.com/api/webhooks/9876543210/ZyXwVu_9876-abc", "ZyXwVu_9876-abc", "[REDACTED_WEBHOOK]")]
        public void Redact_DiscordWebhookUrls_RedactsTokenSegment(string input, string secretToken, string expectedMarker)
        {
            Assert.True(LogRedactor.MayContainSensitiveData(input.AsSpan()));
            string redacted = LogRedactor.Redact(input);
            Assert.Contains(expectedMarker, redacted, StringComparison.Ordinal);
            Assert.DoesNotContain(secretToken, redacted, StringComparison.Ordinal);
        }

        [Fact]
        public void Redact_QuotedRoblosecurityCookies_RedactsCleanly()
        {
            string quotedWarningCookie = "Cookie: .ROBLOSECURITY=\"_|WARNING:-DO-NOT-SHARE-THIS.--Abc123\"; path=/";
            Assert.True(LogRedactor.MayContainSensitiveData(quotedWarningCookie.AsSpan()));
            string redactedWarning = LogRedactor.Redact(quotedWarningCookie);
            Assert.Contains("[REDACTED_COOKIE]", redactedWarning, StringComparison.Ordinal);
            Assert.DoesNotContain("WARNING:-DO-NOT-SHARE-THIS", redactedWarning, StringComparison.Ordinal);
            Assert.DoesNotContain("Abc123", redactedWarning, StringComparison.Ordinal);

            string quotedRawCookie = ".ROBLOSECURITY=\"rawSecretValue\"";
            Assert.True(LogRedactor.MayContainSensitiveData(quotedRawCookie.AsSpan()));
            string redactedRaw = LogRedactor.Redact(quotedRawCookie);
            Assert.Contains("[REDACTED_COOKIE]", redactedRaw, StringComparison.Ordinal);
            Assert.DoesNotContain("rawSecretValue", redactedRaw, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("token=mySecret123&grant_type=client_credentials", "mySecret123", "token=[REDACTED]&grant_type=client_credentials")]
        [InlineData("password=Pass123!", "Pass123!", "password=[REDACTED]")]
        [InlineData("secret=TopSecret99&mode=test", "TopSecret99", "secret=[REDACTED]&mode=test")]
        public void Redact_IndexZeroFormSecrets_RedactsCleanly(string input, string secretValue, string expectedOutput)
        {
            Assert.True(LogRedactor.MayContainSensitiveData(input.AsSpan()));
            string redacted = LogRedactor.Redact(input);
            Assert.Equal(expectedOutput, redacted);
            Assert.DoesNotContain(secretValue, redacted, StringComparison.Ordinal);
        }

        [Fact]
        public void AppLogger_FormatLogLine_FormatsAndRedactsOnConsumerThread()
        {
            var ts = new DateTime(2026, 11, 4, 19, 30, 15, 250);
            var entry = new AppLogger.LogEntry(
                ts,
                42,
                "INFO",
                "Auth header Bearer super_secret_jwt_token_value",
                null);

            string formatted = AppLogger.FormatLogLine(in entry, "Account Manager");

            Assert.StartsWith("2026-11-04 19:30:15,250 [42] INFO  Account Manager - ", formatted);
            Assert.Contains("Bearer [REDACTED_TOKEN]", formatted);
            Assert.DoesNotContain("super_secret_jwt_token_value", formatted);
        }

        [Theory]
        [InlineData("Telegram API call failed for 123456789:AAHdqTcvCH1vGWJxfSeofSAs0K5PALDsawX!", "AAHdqTcvCH1vGWJxfSeofSAs0K5PALDsawX", "[REDACTED_TELEGRAM_TOKEN]")]
        [InlineData("1234567890:AbCdEfGhIjKlMnOpQrStUvWxYz012345678", "AbCdEfGhIjKlMnOpQrStUvWxYz012345678", "[REDACTED_TELEGRAM_TOKEN]")]
        [InlineData("bot_auth=123456789012:ZzYyXxWwVvUuTtSsRrQqPpOoNnMmLlKk_12", "ZzYyXxWwVvUuTtSsRrQqPpOoNnMmLlKk_12", "[REDACTED_TELEGRAM_TOKEN]")]
        [InlineData("token=123456789:AAHdqTcvCH1vGWJxfSeofSAs0K5PALDsaw!", "AAHdqTcvCH1vGWJxfSeofSAs0K5PALDsaw", "[REDACTED]")]
        [InlineData("https://api.telegram.org/bot123456789:AAHdqTcvCH1vGWJxfSeofSAs0K5PALDsawX/getMe", "AAHdqTcvCH1vGWJxfSeofSAs0K5PALDsawX", "[REDACTED_TELEGRAM_TOKEN]")]
        [InlineData("bot failed 123456789:AAHdqTcvCH1vGWJxfSeofSAs0K5PALDsaw- done", "AAHdqTcvCH1vGWJxfSeofSAs0K5PALDsaw-", "[REDACTED_TELEGRAM_TOKEN]")]
        [InlineData("123456789:AAHdqTcvCH1vGWJxfSeofSAs0K5PALDsaw-", "AAHdqTcvCH1vGWJxfSeofSAs0K5PALDsaw-", "[REDACTED_TELEGRAM_TOKEN]")]
        public void LogRedactor_StandaloneTelegramTokenWithoutBotPrefix_IsRedacted(string raw, string secretPart, string expectedMarker)
        {
            Assert.True(LogRedactor.MayContainSensitiveData(raw.AsSpan()));
            string redacted = LogRedactor.Redact(raw);
            Assert.DoesNotContain(secretPart, redacted, StringComparison.Ordinal);
            Assert.Contains(expectedMarker, redacted, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("2026-11-04 19:30:15,250 [42] INFO  Account Manager - Multi-Instance Ready")]
        [InlineData("Server endpoint: 127.0.0.1:7963")]
        [InlineData("Account 123456789: short status message with enough length")]
        [InlineData("Trailing colon 123456789:")]
        public void LogRedactor_BenignColonMessages_ShortCircuitWithoutRedaction(string message)
        {
            Assert.False(LogRedactor.MayContainSensitiveData(message.AsSpan()));
            string redacted = LogRedactor.Redact(message);
            Assert.Same(message, redacted);
        }
    }
}


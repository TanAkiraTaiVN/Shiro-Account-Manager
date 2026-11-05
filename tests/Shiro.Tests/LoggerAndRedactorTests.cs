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

            string bloxKey = "Using key BLOX-1234567890ABCDEF";
            Assert.Contains("BLOX-[REDACTED_KEY]", LogRedactor.Redact(bloxKey));
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
    }
}


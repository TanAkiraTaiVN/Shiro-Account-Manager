#nullable enable
using System;
using System.Buffers;
using System.Text.RegularExpressions;

namespace Shiro.Services
{
    public static partial class LogRedactor
    {
        private static readonly SearchValues<char> FastTriggerChars = SearchValues.Create(":=@\"Bb/");

        [GeneratedRegex(@"(?:_|%5F)?(?:\||%7C)?WARNING:-DO-NOT-SHARE-THIS[^;\r\n\s""']+", RegexOptions.IgnoreCase)]
        private static partial Regex RoblosecurityTokenRegex();

        [GeneratedRegex(@"\.ROBLOSECURITY=(?:([""'])([^;\r\n""']+)\1|[""']?([^;\r\n\s""']+))", RegexOptions.IgnoreCase)]
        private static partial Regex CookieHeaderRegex();

        [GeneratedRegex(@"(\\?""(?:password|token|SecurityToken|secret|HbaPrivateKey|privateKeyPkcs8|x-bound-auth-token|authenticationTicket|CSRFToken|DiscordWebhookUrl|TelegramBotToken)\\?"")\s*:\s*(\\?"")([^""\\]*(?:\\.[^""\\]*)*)\2", RegexOptions.IgnoreCase)]
        private static partial Regex JsonSecretRegex();

        [GeneratedRegex(@"x-bound-auth-token\s*[:=]\s*([^\r\n\s""',;]+)", RegexOptions.IgnoreCase)]
        private static partial Regex BoundAuthHeaderRegex();

        [GeneratedRegex(@"(^|[?&])(password|token|secret|cookie)=([^&\s""']+)", RegexOptions.IgnoreCase)]
        private static partial Regex QueryParamSecretRegex();

        [GeneratedRegex(@"Bearer\s+[a-zA-Z0-9_\-\.+/=]+", RegexOptions.IgnoreCase)]
        private static partial Regex BearerTokenRegex();

        [GeneratedRegex(@"((?:https?|socks4a?|socks5h?)://)([^/\s:@]+):([^/\s@]+)@", RegexOptions.IgnoreCase)]
        private static partial Regex ProxyAuthUrlRegex();

        [GeneratedRegex(@"(/bot)\d+:[A-Za-z0-9_\-]+(/|$|\s)", RegexOptions.IgnoreCase)]
        private static partial Regex TelegramBotUrlRegex();

        [GeneratedRegex(@"(https?://(?:(?:ptb|canary)\.)?(?:discord|discordapp)\.com/api/webhooks/\d+/)[A-Za-z0-9_\-]+", RegexOptions.IgnoreCase)]
        private static partial Regex DiscordWebhookUrlRegex();

        [GeneratedRegex(@"BLOX-[A-Za-z0-9_\-]{8,}", RegexOptions.IgnoreCase)]
        private static partial Regex BloxApiKeyRegex();

        private static bool StartsWithFormSecretKey(ReadOnlySpan<char> span) =>
            span.StartsWith("password=", StringComparison.OrdinalIgnoreCase) ||
            span.StartsWith("token=", StringComparison.OrdinalIgnoreCase) ||
            span.StartsWith("secret=", StringComparison.OrdinalIgnoreCase) ||
            span.StartsWith("cookie=", StringComparison.OrdinalIgnoreCase);

        internal static bool MayContainSensitiveData(ReadOnlySpan<char> span)
        {
            if (span.IsEmpty || span.IndexOfAny(FastTriggerChars) < 0)
                return false;

            if (span.IndexOf("WARNING:-DO-NOT-SHARE-THIS", StringComparison.OrdinalIgnoreCase) >= 0 ||
                span.IndexOf(".ROBLOSECURITY=", StringComparison.OrdinalIgnoreCase) >= 0 ||
                span.IndexOf("x-bound-auth-token", StringComparison.OrdinalIgnoreCase) >= 0 ||
                span.IndexOf("Bearer", StringComparison.OrdinalIgnoreCase) >= 0 ||
                span.IndexOf("/bot", StringComparison.OrdinalIgnoreCase) >= 0 ||
                span.IndexOf("webhooks/", StringComparison.OrdinalIgnoreCase) >= 0 ||
                span.IndexOf("BLOX-", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            if (span.IndexOf('"') >= 0 && span.IndexOf(':') >= 0)
                return true;

            if (span.IndexOf('=') >= 0 && (span.IndexOfAny('?', '&') >= 0 || StartsWithFormSecretKey(span)))
                return true;

            if (span.IndexOf('@') >= 0 && span.IndexOf("://", StringComparison.Ordinal) >= 0)
                return true;

            return false;
        }

        public static string Redact(string? message)
        {
            if (string.IsNullOrEmpty(message))
                return string.Empty;

            ReadOnlySpan<char> span = message.AsSpan();
            if (!MayContainSensitiveData(span))
                return message;

            string redacted = message;

            if (span.IndexOf("WARNING:-DO-NOT-SHARE-THIS", StringComparison.OrdinalIgnoreCase) >= 0)
                redacted = RoblosecurityTokenRegex().Replace(redacted, "[REDACTED_ROBLOSECURITY]");

            if (redacted.Contains(".ROBLOSECURITY=", StringComparison.OrdinalIgnoreCase))
                redacted = CookieHeaderRegex().Replace(redacted, ".ROBLOSECURITY=$1[REDACTED_COOKIE]$1");

            if (redacted.Contains('"') && redacted.Contains(':'))
                redacted = JsonSecretRegex().Replace(redacted, "$1:$2[REDACTED]$2");

            if (redacted.Contains("x-bound-auth-token", StringComparison.OrdinalIgnoreCase))
                redacted = BoundAuthHeaderRegex().Replace(redacted, "x-bound-auth-token:[REDACTED_BAT]");

            if (redacted.Contains('=') && (redacted.AsSpan().IndexOfAny('?', '&') >= 0 || StartsWithFormSecretKey(redacted.AsSpan())))
                redacted = QueryParamSecretRegex().Replace(redacted, "$1$2=[REDACTED]");

            if (redacted.Contains("Bearer", StringComparison.OrdinalIgnoreCase))
                redacted = BearerTokenRegex().Replace(redacted, "Bearer [REDACTED_TOKEN]");

            if (redacted.Contains('@') && redacted.Contains("://", StringComparison.Ordinal))
                redacted = ProxyAuthUrlRegex().Replace(redacted, "$1[REDACTED_PROXY_AUTH]@");

            if (redacted.Contains("/bot", StringComparison.OrdinalIgnoreCase))
                redacted = TelegramBotUrlRegex().Replace(redacted, "$1[REDACTED_TELEGRAM_TOKEN]$2");

            if (redacted.Contains("webhooks/", StringComparison.OrdinalIgnoreCase))
                redacted = DiscordWebhookUrlRegex().Replace(redacted, "$1[REDACTED_WEBHOOK]");

            if (redacted.Contains("BLOX-", StringComparison.OrdinalIgnoreCase))
                redacted = BloxApiKeyRegex().Replace(redacted, "BLOX-[REDACTED_KEY]");

            return redacted;
        }
    }
}


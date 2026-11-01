#nullable enable
using System;
using System.Text.RegularExpressions;

namespace Shiro.Services
{
    public static partial class LogRedactor
    {
        [GeneratedRegex(@"(?:_|%5F)?(?:\||%7C)?WARNING:-DO-NOT-SHARE-THIS[^;\r\n\s""']+", RegexOptions.IgnoreCase)]
        private static partial Regex RoblosecurityTokenRegex();

        [GeneratedRegex(@"\.ROBLOSECURITY=([^;\r\n\s""']+)", RegexOptions.IgnoreCase)]
        private static partial Regex CookieHeaderRegex();

        [GeneratedRegex(@"""(password|token|SecurityToken|secret|HbaPrivateKey|privateKeyPkcs8)""\s*:\s*""([^""]+)""", RegexOptions.IgnoreCase)]
        private static partial Regex JsonSecretRegex();

        [GeneratedRegex(@"x-bound-auth-token\s*[:=]\s*([^\r\n\s""',;]+)", RegexOptions.IgnoreCase)]
        private static partial Regex BoundAuthHeaderRegex();

        [GeneratedRegex(@"(?:[?&])(password|token|secret)=([^&\s""']+)", RegexOptions.IgnoreCase)]
        private static partial Regex QueryParamSecretRegex();

        [GeneratedRegex(@"Bearer\s+[a-zA-Z0-9_\-\.]+", RegexOptions.IgnoreCase)]
        private static partial Regex BearerTokenRegex();

        [GeneratedRegex(@"((?:https?|socks4|socks5)://)([^/\s:@]+):([^/\s@]+)@", RegexOptions.IgnoreCase)]
        private static partial Regex ProxyAuthUrlRegex();

        [GeneratedRegex(@"BLOX-[A-Za-z0-9_\-]{8,}", RegexOptions.IgnoreCase)]
        private static partial Regex BloxApiKeyRegex();

        public static string Redact(string? message)
        {
            if (string.IsNullOrEmpty(message))
                return string.Empty;

            string redacted = RoblosecurityTokenRegex().Replace(message, "[REDACTED_ROBLOSECURITY]");
            redacted = CookieHeaderRegex().Replace(redacted, ".ROBLOSECURITY=[REDACTED_COOKIE]");
            redacted = JsonSecretRegex().Replace(redacted, "\"$1\":\"[REDACTED]\"");
            redacted = BoundAuthHeaderRegex().Replace(redacted, "x-bound-auth-token:[REDACTED_BAT]");
            redacted = QueryParamSecretRegex().Replace(redacted, "?$1=[REDACTED]");
            redacted = BearerTokenRegex().Replace(redacted, "Bearer [REDACTED_TOKEN]");
            redacted = ProxyAuthUrlRegex().Replace(redacted, "$1[REDACTED_PROXY_AUTH]@");
            redacted = BloxApiKeyRegex().Replace(redacted, "BLOX-[REDACTED_KEY]");
            return redacted;
        }
    }
}


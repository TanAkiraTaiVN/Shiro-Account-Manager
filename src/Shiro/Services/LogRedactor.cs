#nullable enable
using System;
using System.Text.RegularExpressions;

namespace Shiro.Services
{
    public static partial class LogRedactor
    {
        [GeneratedRegex(@"(?:_|%5F)WARNING:-DO-NOT-SHARE-THIS\.[^;\r\n\s""']+", RegexOptions.IgnoreCase)]
        private static partial Regex RoblosecurityTokenRegex();

        [GeneratedRegex(@"\.ROBLOSECURITY=([^;\r\n\s""']+)", RegexOptions.IgnoreCase)]
        private static partial Regex CookieHeaderRegex();

        [GeneratedRegex(@"""(password|token|SecurityToken|secret)""\s*:\s*""([^""]+)""", RegexOptions.IgnoreCase)]
        private static partial Regex JsonSecretRegex();

        [GeneratedRegex(@"(?:[?&])(password|token|secret)=([^&\s""']+)", RegexOptions.IgnoreCase)]
        private static partial Regex QueryParamSecretRegex();

        [GeneratedRegex(@"Bearer\s+[a-zA-Z0-9_\-\.]+", RegexOptions.IgnoreCase)]
        private static partial Regex BearerTokenRegex();

        public static string Redact(string? message)
        {
            if (string.IsNullOrEmpty(message))
                return string.Empty;

            string redacted = RoblosecurityTokenRegex().Replace(message, "[REDACTED_ROBLOSECURITY]");
            redacted = CookieHeaderRegex().Replace(redacted, ".ROBLOSECURITY=[REDACTED_COOKIE]");
            redacted = JsonSecretRegex().Replace(redacted, "\"$1\":\"[REDACTED]\"");
            redacted = QueryParamSecretRegex().Replace(redacted, "?$1=[REDACTED]");
            redacted = BearerTokenRegex().Replace(redacted, "Bearer [REDACTED_TOKEN]");
            return redacted;
        }
    }
}


using System;
using System.Collections.Concurrent;
using System.Linq;

namespace Shiro.Services
{
    public enum AuthScope
    {
        PublicTelemetry,
        Api,
        WebSocket
    }

    public enum AuthDecision
    {
        Allow,
        ForbiddenExternal,
        ForbiddenHost,
        ForbiddenOrigin,
        PasswordNotConfigured,
        InvalidPassword
    }

    public readonly record struct AuthSettings(
        bool AllowExternal,
        bool EveryRequestRequiresPassword,
        string? ConfiguredPassword);

    public readonly record struct AuthRequest(
        AuthScope Scope,
        bool IsLoopbackRemote,
        string? Host,
        string? Origin,
        string? HeaderPassword,
        string? QueryPassword);

    public static class WebAuthPolicy
    {
        public const int MinPasswordLength = 6;
        public const int MinExternalPasswordLength = 12;
        public const int RecommendedExternalPasswordLength = MinExternalPasswordLength;
        public const int MaxFailedAttempts = 5;
        public static readonly TimeSpan LockoutDuration = TimeSpan.FromSeconds(30);
        public static readonly TimeSpan AttemptWindow = TimeSpan.FromMinutes(15);
        private const int MaxTrackedClientFailures = 512;

        private static readonly ConcurrentDictionary<string, (int Failures, DateTimeOffset LockedUntilUtc, DateTimeOffset LastFailureUtc)> ClientFailures =
            new(StringComparer.OrdinalIgnoreCase);

        public static void ResetLockoutsForTesting() => ClientFailures.Clear();

        public static bool IsClientLockedOut(string? clientKey, DateTimeOffset? nowUtc = null)
        {
            if (string.IsNullOrWhiteSpace(clientKey))
                return false;

            DateTimeOffset now = nowUtc ?? DateTimeOffset.UtcNow;
            if (ClientFailures.TryGetValue(clientKey, out var state))
            {
                if (state.LockedUntilUtc > now)
                    return true;

                if (state.LockedUntilUtc != DateTimeOffset.MinValue || (now - state.LastFailureUtc) > AttemptWindow)
                    ClientFailures.TryRemove(clientKey, out _);
            }

            return false;
        }

        private static void EvictClientFailuresIfNeeded(string incomingKey, DateTimeOffset now)
        {
            if (ClientFailures.Count < MaxTrackedClientFailures)
                return;

            foreach (var entry in ClientFailures)
            {
                bool expiredLockout = entry.Value.LockedUntilUtc != DateTimeOffset.MinValue && entry.Value.LockedUntilUtc <= now;
                bool staleNonLocked = entry.Value.LockedUntilUtc == DateTimeOffset.MinValue && (now - entry.Value.LastFailureUtc) > AttemptWindow;
                if (expiredLockout || staleNonLocked)
                {
                    ClientFailures.TryRemove(entry.Key, out _);
                }
            }

            int targetMax = ClientFailures.ContainsKey(incomingKey)
                ? MaxTrackedClientFailures
                : MaxTrackedClientFailures - 1;
            if (ClientFailures.Count <= targetMax)
                return;

            int excess = ClientFailures.Count - targetMax;
            var oldestNonLocked = ClientFailures
                .Where(kvp => kvp.Value.LockedUntilUtc == DateTimeOffset.MinValue &&
                              !string.Equals(kvp.Key, incomingKey, StringComparison.OrdinalIgnoreCase))
                .OrderBy(kvp => kvp.Value.LastFailureUtc)
                .Take(excess)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (string key in oldestNonLocked)
            {
                ClientFailures.TryRemove(key, out _);
            }

            if (ClientFailures.Count > targetMax)
            {
                int stillExcess = ClientFailures.Count - targetMax;
                var soonestExpiringLocked = ClientFailures
                    .Where(kvp => !string.Equals(kvp.Key, incomingKey, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(kvp => kvp.Value.LockedUntilUtc)
                    .ThenBy(kvp => kvp.Value.LastFailureUtc)
                    .Take(stillExcess)
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (string key in soonestExpiringLocked)
                {
                    ClientFailures.TryRemove(key, out _);
                }
            }
        }

        public static bool RecordAuthResult(string? clientKey, AuthDecision decision, DateTimeOffset? nowUtc = null)
        {
            if (string.IsNullOrWhiteSpace(clientKey))
                return false;

            if (decision == AuthDecision.Allow)
            {
                ClientFailures.TryRemove(clientKey, out _);
                return false;
            }

            if (decision != AuthDecision.InvalidPassword)
                return IsClientLockedOut(clientKey, nowUtc);

            DateTimeOffset now = nowUtc ?? DateTimeOffset.UtcNow;
            EvictClientFailuresIfNeeded(clientKey, now);

            var updated = ClientFailures.AddOrUpdate(
                clientKey,
                _ => (1, 1 >= MaxFailedAttempts ? now.Add(LockoutDuration) : DateTimeOffset.MinValue, now),
                (_, prev) =>
                {
                    bool expiredLockout = prev.LockedUntilUtc != DateTimeOffset.MinValue && prev.LockedUntilUtc <= now;
                    bool staleAttempts = prev.LockedUntilUtc == DateTimeOffset.MinValue && (now - prev.LastFailureUtc) > AttemptWindow;
                    int baseFailures = (expiredLockout || staleAttempts) ? 0 : prev.Failures;
                    int nextFailures = baseFailures + 1;
                    DateTimeOffset lockedUntil = nextFailures >= MaxFailedAttempts
                        ? now.Add(LockoutDuration)
                        : DateTimeOffset.MinValue;
                    return (nextFailures, lockedUntil, now);
                });

            return updated.LockedUntilUtc > now;
        }

        public static bool IsPasswordConfigured(string? configured) =>
            !string.IsNullOrEmpty(configured) && configured.Length >= MinPasswordLength;

        public static bool IsStrongExternalPassword(string? configured) =>
            !string.IsNullOrEmpty(configured) && configured.Length >= MinExternalPasswordLength;

        public static bool CanListenExternally(in AuthSettings settings) =>
            settings.AllowExternal && IsStrongExternalPassword(settings.ConfiguredPassword);

        /// <summary>
        /// Redacts sensitive query parameters (Password, password, token, Cookie, cookie)
        /// from a URL or query string before diagnostic logging.
        /// </summary>
        public static string RedactSensitiveQuery(string? urlOrQuery)
        {
            if (string.IsNullOrEmpty(urlOrQuery))
                return string.Empty;

            return System.Text.RegularExpressions.Regex.Replace(
                urlOrQuery,
                @"([?&](?:Password|token|Cookie|Proxy)=)[^&\s#]*",
                "$1[REDACTED]",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        /// <summary>
        /// Validates that a WebView2 document or message source URI uses HTTPS and belongs
        /// strictly to <c>roblox.com</c> or a subdomain of <c>.roblox.com</c>.
        /// </summary>
        public static bool IsTrustedRobloxOrigin(string? sourceUri)
        {
            if (string.IsNullOrWhiteSpace(sourceUri) ||
                !Uri.TryCreate(sourceUri, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps)
            {
                return false;
            }

            return string.Equals(uri.Host, "roblox.com", StringComparison.OrdinalIgnoreCase) ||
                   uri.Host.EndsWith(".roblox.com", StringComparison.OrdinalIgnoreCase);
        }

        internal static string StripHostPort(string? hostHeader)
        {
            if (string.IsNullOrWhiteSpace(hostHeader)) return string.Empty;
            string trimmed = hostHeader.Trim();
            if (trimmed.StartsWith('['))
            {
                int closing = trimmed.IndexOf(']');
                if (closing > 1)
                    return trimmed.Substring(1, closing - 1);
            }

            int colon = trimmed.LastIndexOf(':');
            if (colon > 0 && trimmed.IndexOf(':') == colon)
                trimmed = trimmed.Substring(0, colon);

            return trimmed.Trim('[', ']');
        }

        public static bool IsLoopbackHostName(string host)
        {
            string bare = StripHostPort(host);
            if (string.IsNullOrEmpty(bare)) return false;
            if (bare.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
            if (System.Net.IPAddress.TryParse(bare, out var ip))
            {
                if (ip.IsIPv4MappedToIPv6)
                    ip = ip.MapToIPv4();
                return System.Net.IPAddress.IsLoopback(ip);
            }
            return false;
        }

        public static bool IsTrustedLocalOrigin(string origin)
        {
            if (string.IsNullOrEmpty(origin)) return true;
            if (!Uri.TryCreate(origin, UriKind.Absolute, out Uri? uri) || uri == null) return false;
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
            return IsLoopbackHostName(uri.Host);
        }

        internal static bool IsOriginAllowed(string origin, string? requestHost, bool allowExternal)
        {
            if (string.IsNullOrEmpty(origin) || IsTrustedLocalOrigin(origin))
                return true;

            if (!allowExternal)
                return false;

            if (!Uri.TryCreate(origin, UriKind.Absolute, out Uri? originUri) || originUri == null)
                return false;
            if (originUri.Scheme != Uri.UriSchemeHttp && originUri.Scheme != Uri.UriSchemeHttps)
                return false;

            string bareRequestHost = StripHostPort(requestHost);
            return !string.IsNullOrEmpty(bareRequestHost) &&
                   string.Equals(originUri.Host, bareRequestHost, StringComparison.OrdinalIgnoreCase);
        }

        public static string? ExtractHeaderPassword(Func<string, string?> getHeader)
        {
            string? password = getHeader("X-Shiro-Auth");
            if (string.IsNullOrEmpty(password))
                password = getHeader("X-RAM-Auth");
            if (string.IsNullOrEmpty(password))
            {
                string? authHeader = getHeader("Authorization");
                if (!string.IsNullOrEmpty(authHeader) && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                    password = authHeader.Substring(7).Trim();
            }

            return string.IsNullOrEmpty(password) ? null : password;
        }

        public static string? ExtractQueryPassword(Func<string, string?> getQuery)
        {
            string? password = getQuery("Password") ?? getQuery("password") ?? getQuery("token");
            return string.IsNullOrEmpty(password) ? null : password;
        }

        public static string ExtractRequestPassword(Func<string, string?> getHeader, Func<string, string?> getQuery)
        {
            return ExtractHeaderPassword(getHeader) ?? ExtractQueryPassword(getQuery) ?? string.Empty;
        }

        public static int ToStatusCode(AuthDecision decision) =>
            decision switch
            {
                AuthDecision.Allow => 200,
                AuthDecision.ForbiddenExternal => 403,
                AuthDecision.ForbiddenHost => 403,
                AuthDecision.ForbiddenOrigin => 403,
                _ => 401
            };

        public static AuthDecision Evaluate(in AuthRequest request, in AuthSettings settings)
        {
            // 1. Remote request without external connections allowed -> ForbiddenExternal.
            if (!request.IsLoopbackRemote && !settings.AllowExternal)
            {
                return AuthDecision.ForbiddenExternal;
            }

            // 2. When AllowExternal is false: Host must be present and loopback; Origin must be allowed for this mode.
            if (!settings.AllowExternal)
            {
                if (string.IsNullOrWhiteSpace(request.Host) || !IsLoopbackHostName(request.Host))
                {
                    return AuthDecision.ForbiddenHost;
                }
            }

            if (!IsOriginAllowed(request.Origin ?? string.Empty, request.Host, settings.AllowExternal))
            {
                return AuthDecision.ForbiddenOrigin;
            }

            // 3. Effective password = HeaderPassword; QueryPassword is allowed on loopback,
            //    or remotely ONLY for AuthScope.WebSocket (e.g., RAMAccount.lua ?token=).
            string? effectivePassword = request.HeaderPassword;
            if (string.IsNullOrEmpty(effectivePassword) &&
                (request.IsLoopbackRemote || request.Scope == AuthScope.WebSocket))
            {
                effectivePassword = request.QueryPassword;
            }

            // 4. Password required when: Scope == Api, Scope == WebSocket, EveryRequestRequiresPassword, or !IsLoopbackRemote.
            bool passwordRequired =
                request.Scope == AuthScope.Api ||
                request.Scope == AuthScope.WebSocket ||
                settings.EveryRequestRequiresPassword ||
                !request.IsLoopbackRemote;

            // 5. If required: external requests require >= MinExternalPasswordLength (12);
            //    localhost requests require >= MinPasswordLength (6).
            if (passwordRequired)
            {
                bool passwordConfigured = request.IsLoopbackRemote
                    ? IsPasswordConfigured(settings.ConfiguredPassword)
                    : IsStrongExternalPassword(settings.ConfiguredPassword);
                if (!passwordConfigured)
                {
                    return AuthDecision.PasswordNotConfigured;
                }

                if (string.IsNullOrEmpty(effectivePassword) ||
                    !CryptoHelper.FixedTimeEqualsSecret(effectivePassword, settings.ConfiguredPassword!))
                {
                    return AuthDecision.InvalidPassword;
                }
            }

            // 6. Allow.
            return AuthDecision.Allow;
        }
    }
}

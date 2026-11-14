using System;
using System.Collections.Concurrent;

namespace Shiro.Services
{
    public enum AuthScope
    {
        PublicTelemetry,
        Api,
        NexusControl
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
        public const int RecommendedExternalPasswordLength = 12;
        public const int MaxFailedAttempts = 5;
        public static readonly TimeSpan LockoutDuration = TimeSpan.FromSeconds(30);

        private static readonly ConcurrentDictionary<string, (int Failures, DateTimeOffset LockedUntilUtc)> ClientFailures =
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

                if (state.LockedUntilUtc != DateTimeOffset.MinValue)
                    ClientFailures.TryRemove(clientKey, out _);
            }

            return false;
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
            var updated = ClientFailures.AddOrUpdate(
                clientKey,
                _ => (1, 1 >= MaxFailedAttempts ? now.Add(LockoutDuration) : DateTimeOffset.MinValue),
                (_, prev) =>
                {
                    int baseFailures = (prev.LockedUntilUtc != DateTimeOffset.MinValue && prev.LockedUntilUtc <= now) ? 0 : prev.Failures;
                    int nextFailures = baseFailures + 1;
                    DateTimeOffset lockedUntil = nextFailures >= MaxFailedAttempts
                        ? now.Add(LockoutDuration)
                        : DateTimeOffset.MinValue;
                    return (nextFailures, lockedUntil);
                });

            return updated.LockedUntilUtc > now;
        }

        public static bool IsPasswordConfigured(string? configured) =>
            !string.IsNullOrEmpty(configured) && configured.Length >= MinPasswordLength;

        public static bool IsStrongExternalPassword(string? configured) =>
            !string.IsNullOrEmpty(configured) && configured.Length >= RecommendedExternalPasswordLength;

        public static bool CanListenExternally(in AuthSettings settings) =>
            settings.AllowExternal && IsPasswordConfigured(settings.ConfiguredPassword);

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
                @"([?&](?:Password|password|token|Cookie|cookie)=)[^&\s#]*",
                "$1[REDACTED]",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant);
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

        public static bool IsLoopbackHostName(string host)
        {
            if (string.IsNullOrEmpty(host)) return false;
            host = host.Trim().TrimStart('[').TrimEnd(']');
            return host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                   host.Equals("127.0.0.1", StringComparison.Ordinal) ||
                   host.Equals("::1", StringComparison.Ordinal);
        }

        public static bool IsTrustedLocalOrigin(string origin)
        {
            if (string.IsNullOrEmpty(origin)) return true;
            if (!Uri.TryCreate(origin, UriKind.Absolute, out Uri? uri) || uri == null) return false;
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
            return IsLoopbackHostName(uri.Host);
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

            // 2. When AllowExternal is false: non-empty host must be loopback (IsLoopbackHostName) -> ForbiddenHost; Origin must be trusted local origin (IsTrustedLocalOrigin) -> ForbiddenOrigin (always checked even when AllowExternal is true).
            if (!settings.AllowExternal)
            {
                if (!string.IsNullOrEmpty(request.Host) && !IsLoopbackHostName(request.Host))
                {
                    return AuthDecision.ForbiddenHost;
                }
            }

            if (!IsTrustedLocalOrigin(request.Origin ?? string.Empty))
            {
                return AuthDecision.ForbiddenOrigin;
            }

            // 3. Effective password = HeaderPassword; if empty, QueryPassword is used only when Scope == NexusControl or IsLoopbackRemote is true.
            string? effectivePassword = request.HeaderPassword;
            if (string.IsNullOrEmpty(effectivePassword))
            {
                if (request.Scope == AuthScope.NexusControl || request.IsLoopbackRemote)
                {
                    effectivePassword = request.QueryPassword;
                }
            }

            // 4. Password required when: Scope == Api, or EveryRequestRequiresPassword, or !IsLoopbackRemote, or (Scope == NexusControl && IsPasswordConfigured).
            bool passwordRequired =
                request.Scope == AuthScope.Api ||
                settings.EveryRequestRequiresPassword ||
                !request.IsLoopbackRemote ||
                (request.Scope == AuthScope.NexusControl && IsPasswordConfigured(settings.ConfiguredPassword));

            // 5. If required: not configured -> PasswordNotConfigured; effective password empty or incorrect (CryptoHelper.FixedTimeEqualsSecret) -> InvalidPassword.
            if (passwordRequired)
            {
                if (!IsPasswordConfigured(settings.ConfiguredPassword))
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

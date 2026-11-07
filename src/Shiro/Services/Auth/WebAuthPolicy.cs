using System;

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
            // 1. !IsLoopbackRemote && !AllowExternal -> ForbiddenExternal
            if (!request.IsLoopbackRemote && !settings.AllowExternal)
            {
                return AuthDecision.ForbiddenExternal;
            }

            // 2. !AllowExternal: Host không rỗng và không phải loopback (AccountApiDispatcher.IsLoopbackHostName) -> ForbiddenHost;
            //    Origin không tin cậy (AccountApiDispatcher.IsTrustedLocalOrigin) -> ForbiddenOrigin (luôn kiểm tra cả khi AllowExternal = true).
            if (!settings.AllowExternal)
            {
                if (!string.IsNullOrEmpty(request.Host) && !AccountApiDispatcher.IsLoopbackHostName(request.Host))
                {
                    return AuthDecision.ForbiddenHost;
                }
            }

            if (!AccountApiDispatcher.IsTrustedLocalOrigin(request.Origin ?? string.Empty))
            {
                return AuthDecision.ForbiddenOrigin;
            }

            // 3. Mật khẩu hiệu lực = HeaderPassword; nếu rỗng thì dùng QueryPassword chỉ khi Scope == NexusControl || IsLoopbackRemote.
            string? effectivePassword = request.HeaderPassword;
            if (string.IsNullOrEmpty(effectivePassword))
            {
                if (request.Scope == AuthScope.NexusControl || request.IsLoopbackRemote)
                {
                    effectivePassword = request.QueryPassword;
                }
            }

            // 4. Bắt buộc mật khẩu khi: Scope == Api hoặc EveryRequestRequiresPassword hoặc !IsLoopbackRemote hoặc (Scope == NexusControl && IsPasswordConfigured).
            bool passwordRequired =
                request.Scope == AuthScope.Api ||
                settings.EveryRequestRequiresPassword ||
                !request.IsLoopbackRemote ||
                (request.Scope == AuthScope.NexusControl && IsPasswordConfigured(settings.ConfiguredPassword));

            // 5. Nếu bắt buộc: chưa cấu hình -> PasswordNotConfigured; mật khẩu hiệu lực rỗng hoặc sai (AccountApiDispatcher.FixedTimeEqualsPassword) -> InvalidPassword.
            if (passwordRequired)
            {
                if (!IsPasswordConfigured(settings.ConfiguredPassword))
                {
                    return AuthDecision.PasswordNotConfigured;
                }

                if (string.IsNullOrEmpty(effectivePassword) ||
                    !AccountApiDispatcher.FixedTimeEqualsPassword(effectivePassword, settings.ConfiguredPassword!))
                {
                    return AuthDecision.InvalidPassword;
                }
            }

            // 6. Allow.
            return AuthDecision.Allow;
        }
    }
}

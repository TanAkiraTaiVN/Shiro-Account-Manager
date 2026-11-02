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

        public static bool IsPasswordConfigured(string? configured) =>
            !string.IsNullOrEmpty(configured) && configured.Length >= MinPasswordLength;

        public static bool CanListenExternally(in AuthSettings settings) =>
            settings.AllowExternal && IsPasswordConfigured(settings.ConfiguredPassword);

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

            // 2. !AllowExternal: Host không rỗng và không phải loopback (AccountManager.IsLoopbackHostName) -> ForbiddenHost;
            //    Origin không tin cậy (AccountManager.IsTrustedLocalOrigin) -> ForbiddenOrigin.
            if (!settings.AllowExternal)
            {
                if (!string.IsNullOrEmpty(request.Host) && !AccountManager.IsLoopbackHostName(request.Host))
                {
                    return AuthDecision.ForbiddenHost;
                }

                if (!AccountManager.IsTrustedLocalOrigin(request.Origin ?? string.Empty))
                {
                    return AuthDecision.ForbiddenOrigin;
                }
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

            // 5. Nếu bắt buộc: chưa cấu hình -> PasswordNotConfigured; mật khẩu hiệu lực rỗng hoặc sai (AccountManager.FixedTimeEqualsPassword) -> InvalidPassword.
            if (passwordRequired)
            {
                if (!IsPasswordConfigured(settings.ConfiguredPassword))
                {
                    return AuthDecision.PasswordNotConfigured;
                }

                if (string.IsNullOrEmpty(effectivePassword) ||
                    !AccountManager.FixedTimeEqualsPassword(effectivePassword, settings.ConfiguredPassword!))
                {
                    return AuthDecision.InvalidPassword;
                }
            }

            // 6. Allow.
            return AuthDecision.Allow;
        }
    }
}

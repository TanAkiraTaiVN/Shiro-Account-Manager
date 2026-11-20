#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Services;

namespace Shiro
{
    public partial class Account
    {
        internal const string DefaultBrowserUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

        private object? _cookieLock;
        private object CookieSyncRoot => _cookieLock ?? Interlocked.CompareExchange(ref _cookieLock, new object(), null) ?? _cookieLock!;
        private int _cookieRotationVersion;

        [System.Text.Json.Serialization.JsonIgnore]
        internal int CookieRotationVersion => Volatile.Read(ref _cookieRotationVersion);

        internal void SetCsrfToken(string? token)
        {
            lock (CookieSyncRoot)
            {
                CSRFToken = token;
                TokenSet = string.IsNullOrWhiteSpace(token) ? DateTime.MinValue : DateTime.Now;
            }
        }

        internal string GetFreshCsrfToken(TimeSpan maxAge)
        {
            lock (CookieSyncRoot)
            {
                return !string.IsNullOrEmpty(CSRFToken) && (DateTime.Now - TokenSet) < maxAge
                    ? CSRFToken
                    : string.Empty;
            }
        }

        internal void RecordCsrfTokenAndLastUse(string token, DateTime timestamp)
        {
            lock (CookieSyncRoot)
            {
                CSRFToken = token;
                TokenSet = timestamp;
                LastUse = timestamp;
            }
            RecordVolatileLastUse(timestamp);
        }

        internal string GetCurrentSecurityToken()
        {
            lock (CookieSyncRoot)
            {
                return InMemorySecretProtector.Unprotect(_protectedSecurityToken);
            }
        }

        public Task<bool> ValidateSessionAsync(CancellationToken cancellationToken = default) =>
            AppServices.AccountSessionClient.ValidateSessionAsync(this, cancellationToken);
    }
}

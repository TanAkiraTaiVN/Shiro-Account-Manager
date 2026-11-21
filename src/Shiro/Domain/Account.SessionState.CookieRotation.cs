#nullable enable
using System;
using System.Linq;
using System.Threading;
using Shiro.Services;

namespace Shiro
{
    public partial class Account
    {
        public static string? ExtractRoblosecurityFromSetCookie(string? header) =>
            RobloxCookieHeaderParser.ExtractRoblosecurityFromSetCookie(header);

        internal bool ApplyRotatedSecurityToken(string newCookie, string source) =>
            AppServices.AccountSessionClient.ApplyRotatedSecurityToken(this, newCookie, source);

        internal bool TryCommitRotatedSecurityToken(string sanitized)
        {
            lock (CookieSyncRoot)
            {
                if (!TryReplaceProtectedSecurityTokenLocked(sanitized))
                    return false;

                IsDirty = true;
                TokenSet = DateTime.MinValue;
                CSRFToken = null;
                LastUse = DateTime.Now;
                Interlocked.Increment(ref _cookieRotationVersion);
                return true;
            }
        }

        internal string GetCurrentPassword()
        {
            lock (CookieSyncRoot)
            {
                return InMemorySecretProtector.Unprotect(_protectedPassword);
            }
        }

        private bool TryReplaceProtectedPassword(string value)
        {
            lock (CookieSyncRoot)
            {
                string current = InMemorySecretProtector.Unprotect(_protectedPassword);
                if (string.Equals(current, value, StringComparison.Ordinal))
                    return false;

                InMemorySecretProtector.ZeroAndClear(ref _protectedPassword);
                _protectedPassword = InMemorySecretProtector.Protect(value);
                return true;
            }
        }

        private bool TryReplaceProtectedSecurityTokenLocked(string sanitized)
        {
            string current = InMemorySecretProtector.Unprotect(_protectedSecurityToken);
            if (string.Equals(current, sanitized, StringComparison.Ordinal))
                return false;

            InMemorySecretProtector.ZeroAndClear(ref _protectedSecurityToken);
            bool hasToken = !string.IsNullOrWhiteSpace(sanitized);
            _protectedSecurityToken = hasToken ? InMemorySecretProtector.Protect(sanitized) : null;
            _hasSecurityToken = hasToken;
            _securityTokenPayloadLength = hasToken ? GetSessionTokenPayloadLength(sanitized) : 0;
            return true;
        }

        private (bool HasToken, int PayloadLength) GetCachedTokenMetadata()
        {
            lock (CookieSyncRoot)
            {
                return (_hasSecurityToken, _securityTokenPayloadLength);
            }
        }

        internal bool HasSecurityToken => GetCachedTokenMetadata().HasToken;
        internal int SecurityTokenPayloadLength => GetCachedTokenMetadata().PayloadLength;

        private sealed class SuppressDeferredSaveScope : IDisposable
        {
            private bool _disposed;
            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                if (_suppressDeferredSaveDepth > 0) _suppressDeferredSaveDepth--;
            }
        }

        public void ClearSensitiveMemory()
        {
            string hbaKey = HbaPrivateKey;
            lock (CookieSyncRoot)
            {
                InMemorySecretProtector.ZeroAndClear(ref _protectedSecurityToken);
                _hasSecurityToken = false;
                _securityTokenPayloadLength = 0;
                InMemorySecretProtector.ZeroAndClear(ref _protectedPassword);
                InMemorySecretProtector.ZeroAndClear(ref _protectedHbaPrivateKey);
                _hasHbaPrivateKey = false;
                CSRFToken = null;
                TokenSet = DateTime.MinValue;
                Interlocked.Increment(ref _cookieRotationVersion);
            }
            lock (FieldsSyncRoot)
            {
                if (Fields != null)
                {
                    foreach (string k in Fields.Keys.Where(IsProtectedInternalField).ToList())
                        Fields.Remove(k);
                }
            }
            RobloxHbaService.ClearCachedKey(hbaKey);
        }
    }
}

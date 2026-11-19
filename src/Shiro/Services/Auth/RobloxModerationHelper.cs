#nullable enable
using System;

namespace Shiro.Services
{
    /// <summary>
    /// Shared URL classification helpers for Roblox moderation, lockouts, and security challenge redirects.
    /// </summary>
    internal static class RobloxModerationHelper
    {
        internal static bool IsModerationOrLockedUrl(string? url)
        {
            if (string.IsNullOrEmpty(url))
                return false;

            return url.Contains("/not-approved", StringComparison.OrdinalIgnoreCase) ||
                   url.Contains("/account-status", StringComparison.OrdinalIgnoreCase) ||
                   url.Contains("/banned", StringComparison.OrdinalIgnoreCase) ||
                   url.Contains("/moderation", StringComparison.OrdinalIgnoreCase) ||
                   url.Contains("/account-locked", StringComparison.OrdinalIgnoreCase) ||
                   url.Contains("/security-notification", StringComparison.OrdinalIgnoreCase);
        }
    }
}

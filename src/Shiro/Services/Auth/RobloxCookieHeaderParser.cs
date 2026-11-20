#nullable enable
using System;
using System.Globalization;

namespace Shiro.Services
{
    /// <summary>
    /// Strict RFC 6265-aware parser for Roblox Set-Cookie headers.
    /// Enforces exact cookie-name boundaries and rejects expired or deleted directives.
    /// </summary>
    internal static class RobloxCookieHeaderParser
    {
        private const string CookiePrefix = ".ROBLOSECURITY=";
        private const string WarningMarker = "_|WARNING:-DO-NOT-SHARE-THIS";

        private static readonly string[] LegacyCookieDateFormats =
        {
            "ddd, dd-MMM-yyyy HH:mm:ss 'GMT'",
            "ddd, dd MMM yyyy HH:mm:ss 'GMT'",
            "dd-MMM-yyyy HH:mm:ss 'GMT'",
            "ddd, dd-MMM-yy HH:mm:ss 'GMT'"
        };

        /// <summary>
        /// Returns true if <paramref name="header"/> contains a <c>.ROBLOSECURITY=</c> assignment
        /// at a valid cookie-name boundary.
        /// </summary>
        internal static bool HasRoblosecurityCookiePair(string? header)
        {
            if (string.IsNullOrWhiteSpace(header))
                return false;

            return FindRoblosecurityAssignmentIndex(header.AsSpan()) >= 0;
        }

        /// <summary>
        /// Extracts a valid <c>.ROBLOSECURITY</c> token from a <c>Set-Cookie</c> header,
        /// returning <c>null</c> if the header targets a different cookie name, is expired,
        /// or carries a deletion directive such as <c>max-age=0</c>.
        /// </summary>
        internal static string? ExtractRoblosecurityFromSetCookie(string? header)
        {
            if (string.IsNullOrWhiteSpace(header))
                return null;

            ReadOnlySpan<char> span = header.AsSpan();
            int idx = FindRoblosecurityAssignmentIndex(span);
            if (idx < 0)
                return null;

            ReadOnlySpan<char> afterAssign = span.Slice(idx + CookiePrefix.Length);
            int firstSemi = afterAssign.IndexOf(';');
            if (firstSemi >= 0)
            {
                ReadOnlySpan<char> attributes = afterAssign.Slice(firstSemi + 1);
                if (IsDeletedOrExpiredAttributes(attributes))
                    return null;
                afterAssign = afterAssign.Slice(0, firstSemi);
            }

            ReadOnlySpan<char> tokenSpan = afterAssign.Trim().Trim('"').Trim();
            if (tokenSpan.Length < 100 || !tokenSpan.Contains(WarningMarker.AsSpan(), StringComparison.Ordinal))
                return null;

            return tokenSpan.ToString();
        }

        internal static string SanitizeCookie(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
            ReadOnlySpan<char> span = raw.AsSpan().Trim().Trim("'\"").Trim();

            while (span.StartsWith(".ROBLOSECURITY=", StringComparison.OrdinalIgnoreCase) ||
                   span.StartsWith("ROBLOSECURITY=", StringComparison.OrdinalIgnoreCase))
            {
                int eq = span.IndexOf('=');
                span = span.Slice(eq + 1).Trim().Trim("'\"").Trim();
            }

            int semiIndex = span.IndexOf(';');
            if (semiIndex >= 0)
            {
                span = span.Slice(0, semiIndex).Trim();
            }

            span = span.Trim("'\"").Trim();
            if (span.IsEmpty) return string.Empty;
            return span.Length == raw.Length ? raw : span.ToString();
        }

        internal static int GetSessionTokenPayloadLength(string? cookie)
        {
            if (string.IsNullOrWhiteSpace(cookie))
                return 0;

            ReadOnlySpan<char> span = cookie.AsSpan().Trim();
            int prefixIdx = span.LastIndexOf("|_");
            if (prefixIdx >= 0 && prefixIdx + 2 < span.Length)
                span = span.Slice(prefixIdx + 2);

            int firstDot = span.IndexOf('.');
            if (firstDot <= 0 || firstDot + 1 >= span.Length)
                return 0;

            ReadOnlySpan<char> rest = span.Slice(firstDot + 1);
            int secondDot = rest.IndexOf('.');
            if (secondDot <= 0)
                return 0;

            return secondDot;
        }

        private static int FindRoblosecurityAssignmentIndex(ReadOnlySpan<char> span)
        {
            int searchStart = 0;
            while (searchStart < span.Length)
            {
                int relIdx = span.Slice(searchStart).IndexOf(CookiePrefix.AsSpan(), StringComparison.OrdinalIgnoreCase);
                if (relIdx < 0)
                    return -1;

                int idx = searchStart + relIdx;
                if (idx == 0 || span[idx - 1] == ';' || char.IsWhiteSpace(span[idx - 1]))
                {
                    int walk = idx - 1;
                    while (walk >= 0 && char.IsWhiteSpace(span[walk]))
                        walk--;

                    if (walk < 0 || span[walk] == ';')
                        return idx;
                }

                searchStart = idx + CookiePrefix.Length;
            }

            return -1;
        }

        private static bool IsDeletedOrExpiredAttributes(ReadOnlySpan<char> attributes)
        {
            while (!attributes.IsEmpty)
            {
                int semi = attributes.IndexOf(';');
                ReadOnlySpan<char> segment = semi >= 0 ? attributes.Slice(0, semi) : attributes;
                attributes = semi >= 0 ? attributes.Slice(semi + 1) : ReadOnlySpan<char>.Empty;

                segment = segment.Trim();
                if (segment.IsEmpty)
                    continue;

                int eq = segment.IndexOf('=');
                if (eq <= 0)
                    continue;

                ReadOnlySpan<char> attrName = segment.Slice(0, eq).Trim();
                ReadOnlySpan<char> attrValue = segment.Slice(eq + 1).Trim().Trim('"').Trim();

                if (attrName.Equals("max-age".AsSpan(), StringComparison.OrdinalIgnoreCase))
                {
                    if (long.TryParse(attrValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out long maxAge) && maxAge <= 0)
                        return true;
                }
                else if (attrName.Equals("expires".AsSpan(), StringComparison.OrdinalIgnoreCase))
                {
                    if (TryParseExpiryDate(attrValue, out DateTimeOffset expiresAt) && expiresAt.ToUniversalTime() <= DateTimeOffset.UtcNow)
                        return true;
                }
            }

            return false;
        }

        private static bool TryParseExpiryDate(ReadOnlySpan<char> value, out DateTimeOffset expiresAt)
        {
            if (DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal,
                out expiresAt))
            {
                return true;
            }

            return DateTimeOffset.TryParseExact(
                value,
                LegacyCookieDateFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal,
                out expiresAt);
        }
    }
}

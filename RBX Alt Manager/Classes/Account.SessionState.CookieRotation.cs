#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using Shiro.Http;

namespace Shiro
{
    public partial class Account
    {
        public bool TryUpdateRotatedCookie(HttpResponseMessage response)
        {
            if (response == null) return false;
            try
            {
                IEnumerable<string>? setCookieHeaders = null;
                if (!response.Headers.TryGetValues("Set-Cookie", out setCookieHeaders))
                {
                    try
                    {
                        if (response.Headers.NonValidated.TryGetValues("Set-Cookie", out var nonValidated))
                            setCookieHeaders = nonValidated;
                    }
                    catch (Exception ex)
                    {
                        Program.Logger?.Debug($"NonValidated Set-Cookie header extraction error: {ex.Message}");
                    }
                }

                if (setCookieHeaders == null && response.Content != null)
                {
                    try
                    {
                        if (response.Content.Headers.TryGetValues("Set-Cookie", out var contentCookies))
                            setCookieHeaders = contentCookies;
                    }
                    catch (Exception ex)
                    {
                        Program.Logger?.Debug($"Content Set-Cookie header extraction error: {ex.Message}");
                    }
                }

                if (setCookieHeaders != null)
                {
                    foreach (string header in setCookieHeaders)
                    {
                        string? newCookie = ExtractRoblosecurityFromSetCookie(header);
                        if (!string.IsNullOrWhiteSpace(newCookie) && ApplyRotatedSecurityToken(newCookie, "HttpResponseMessage Set-Cookie"))
                            return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Program.Logger?.Warn($"[CookieRotation] Error inspecting Set-Cookie header for {Username ?? "account"}: {ex.Message}");
            }
            return false;
        }

        public bool TryUpdateRotatedCookie(RestResponse response)
        {
            if (response == null) return false;
            try
            {
                if (response.Cookies != null)
                {
                    var cookie = response.Cookies.FirstOrDefault(c => string.Equals(c.Name, ".ROBLOSECURITY", StringComparison.OrdinalIgnoreCase));
                    if (cookie != null && !string.IsNullOrWhiteSpace(cookie.Value) &&
                        cookie.Value.Length >= 100 && cookie.Value.Contains("_|WARNING:-DO-NOT-SHARE-THIS"))
                    {
                        if (ApplyRotatedSecurityToken(cookie.Value, "RestResponse cookies"))
                            return true;
                    }
                }

                if (response.Headers != null)
                {
                    var setCookieHeaders = response.Headers.Where(h => string.Equals(h.Name, "Set-Cookie", StringComparison.OrdinalIgnoreCase));
                    foreach (var h in setCookieHeaders)
                    {
                        string? str = Convert.ToString(h.Value);
                        string? newCookie = ExtractRoblosecurityFromSetCookie(str);
                        if (!string.IsNullOrEmpty(newCookie) && ApplyRotatedSecurityToken(newCookie, "RestResponse headers"))
                            return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Program.Logger?.Warn($"[CookieRotation] Error inspecting RestResponse cookies for {Username ?? "account"}: {ex.Message}");
            }
            return false;
        }

        public static string? ExtractRoblosecurityFromSetCookie(string? header)
        {
            if (string.IsNullOrWhiteSpace(header)) return null;
            ReadOnlySpan<char> span = header.AsSpan();
            int idx = span.IndexOf(".ROBLOSECURITY=", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;
            span = span.Slice(idx + ".ROBLOSECURITY=".Length);
            int end = span.IndexOf(';');
            if (end >= 0)
                span = span.Slice(0, end);

            span = span.Trim().Trim('"').Trim();
            if (span.Length < 100 || !span.Contains("_|WARNING:-DO-NOT-SHARE-THIS".AsSpan(), StringComparison.Ordinal))
                return null;

            return span.ToString();
        }
    }
}

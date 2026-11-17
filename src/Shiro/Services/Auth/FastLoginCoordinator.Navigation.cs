#nullable enable
using System;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace Shiro.Services
{
    public enum WebViewNavigationFailureAction
    {
        ShowNetworkError,
        ShowHttpError,
        FallbackModerationToLogin
    }

    public static partial class FastLoginCoordinator
    {
        /// <summary>
        /// Only API fetch and XMLHttpRequest calls should receive an injected HBA bound-auth token.
        /// Signing Document navigations or static subresources does not match real browser behavior
        /// and causes stale URL signatures to be carried across 302 redirects (e.g. /home -> /not-approved).
        /// </summary>
        public static bool ShouldSignWebViewResourceContext(CoreWebView2WebResourceContext context) =>
            context == CoreWebView2WebResourceContext.Fetch ||
            context == CoreWebView2WebResourceContext.XmlHttpRequest;

        /// <summary>
        /// Returns true when the given text (or Chromium JSON viewer dump) is a Roblox JSON error envelope
        /// such as <c>{"errors":[{"code":0,"message":""}]}</c> or <c>9002</c>.
        /// </summary>
        public static bool IsRobloxJsonErrorPayload(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            string candidate = text.Trim();
            if (candidate.StartsWith("Pretty-print", StringComparison.OrdinalIgnoreCase))
            {
                candidate = candidate.Substring("Pretty-print".Length).TrimStart();
            }

            if (candidate.Length < 2 || candidate[0] != '{' || candidate[^1] != '}')
                return false;

            try
            {
                using var doc = JsonDocument.Parse(candidate);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    return false;

                if (!doc.RootElement.TryGetProperty("errors", out var errors) ||
                    errors.ValueKind != JsonValueKind.Array ||
                    errors.GetArrayLength() == 0)
                {
                    return false;
                }

                foreach (var item in errors.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object &&
                        (item.TryGetProperty("code", out _) || item.TryGetProperty("message", out _)))
                    {
                        return true;
                    }
                }

                return false;
            }
            catch (JsonException ex)
            {
                _logger.Debug($"[FastLoginCoordinator] IsRobloxJsonErrorPayload parse skipped: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Classifies a failed WebView2 navigation into a network outage, an HTTP error banner,
        /// or a rejected restored moderation session that should fall back to interactive login.
        /// </summary>
        public static WebViewNavigationFailureAction ClassifyNavigationFailure(
            int httpStatusCode,
            bool hasRestoredModerationSession,
            bool alreadyRedirectedModeration,
            string? documentBodyText)
        {
            if (httpStatusCode <= 0)
                return WebViewNavigationFailureAction.ShowNetworkError;

            if (hasRestoredModerationSession &&
                !alreadyRedirectedModeration &&
                (httpStatusCode == 401 || httpStatusCode == 403))
            {
                if (httpStatusCode == 401 || IsRobloxJsonErrorPayload(documentBodyText))
                    return WebViewNavigationFailureAction.FallbackModerationToLogin;

                string structured = ClassifyAuthOrModerationApiPayload(documentBodyText, httpStatusCode);
                if (structured == "UNAUTHENTICATED_MODERATION")
                    return WebViewNavigationFailureAction.FallbackModerationToLogin;
            }

            return WebViewNavigationFailureAction.ShowHttpError;
        }

        /// <summary>
        /// Extracts text from a WebView2 document (including Chromium's JSON viewer &lt;pre&gt; element)
        /// so failed HTTP navigations can be classified cleanly without crashing on closed webviews.
        /// </summary>
        public static async Task<string> ReadDocumentErrorTextAsync(CoreWebView2? core)
        {
            if (core == null)
                return string.Empty;

            try
            {
                string raw = await core.ExecuteScriptAsync(
                    "(() => { const pre = document.querySelector('pre'); return (pre && pre.innerText) || (document.body && document.body.innerText) || ''; })()");
                if (string.IsNullOrWhiteSpace(raw) || raw == "null")
                    return string.Empty;

                using var doc = JsonDocument.Parse(raw);
                return doc.RootElement.ValueKind == JsonValueKind.String
                    ? (doc.RootElement.GetString() ?? string.Empty)
                    : string.Empty;
            }
            catch (Exception ex) when (ex is JsonException || ex is InvalidOperationException || ex is COMException)
            {
                _logger.Debug($"[FastLoginCoordinator] ReadDocumentErrorTextAsync skipped: {ex.Message}");
                return string.Empty;
            }
        }

        /// <summary>
        /// Reads the failed WebView2 document text when an HTTP error status is present and classifies
        /// the appropriate UI or recovery action.
        /// </summary>
        public static async Task<WebViewNavigationFailureAction> ClassifyNavigationFailureAsync(
            CoreWebView2? core,
            int httpStatusCode,
            bool hasRestoredModerationSession,
            bool alreadyRedirectedModeration)
        {
            string errBody = httpStatusCode >= 400
                ? await ReadDocumentErrorTextAsync(core)
                : string.Empty;

            return ClassifyNavigationFailure(
                httpStatusCode,
                hasRestoredModerationSession,
                alreadyRedirectedModeration,
                errBody);
        }
    }
}

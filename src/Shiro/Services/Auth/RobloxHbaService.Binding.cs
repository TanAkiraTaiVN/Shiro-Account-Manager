#nullable enable
using System;
using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Shiro.Services
{
    public static partial class RobloxHbaService
    {
        /// <summary>
        /// Generates a signed <c>x-bound-auth-token</c> for the given account and request parameters.
        /// Returns null if the account does not possess a valid HBA private key or if signing fails.
        /// </summary>
        public static string? GenerateBoundAuthToken(
            Account account,
            string httpMethod,
            string requestUrl,
            string? timestamp = null)
        {
            if (account == null || !account.HasHbaBinding || string.IsNullOrWhiteSpace(requestUrl))
                return null;

            long? tsOverride = null;
            if (!string.IsNullOrWhiteSpace(timestamp) &&
                long.TryParse(timestamp, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsedTs))
            {
                tsOverride = parsedTs;
            }

            if (TryBuildBoundAuthToken(
                account.HbaPrivateKeyPkcs8,
                requestUrl,
                httpMethod,
                bodyContent: null,
                account.HbaTemplate,
                tsOverride,
                out string token))
            {
                return token;
            }

            return null;
        }

        /// <summary>
        /// Cryptographically signs the outgoing HTTP request using the account's HBA key
        /// and attaches the <c>x-bound-auth-token</c> header synchronously.
        /// When request body is present, callers should supply <paramref name="bodyContent"/> directly
        /// or use <see cref="TryAttachHbaHeaderAsync"/> to prevent sync-over-async blocking.
        /// </summary>
        public static bool TryAttachHbaHeader(HttpRequestMessage request, Account account, string? bodyContent = null)
        {
            if (request == null || account == null || !account.HasHbaBinding || request.RequestUri == null)
                return false;

            if (bodyContent == null && request.Content != null)
            {
                try
                {
                    var contentStream = request.Content.ReadAsStream(CancellationToken.None);
                    if (!contentStream.CanRead || !contentStream.CanSeek)
                        return false;

                    contentStream.Position = 0;

                    using var ms = new System.IO.MemoryStream();
                    contentStream.CopyTo(ms);

                    contentStream.Position = 0;

                    bodyContent = System.Text.Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
                }
                catch (Exception ex)
                {
                    _logger.Debug($"[RobloxHbaService] Sync body read failed: {ex.Message}");
                    return false;
                }
            }

            if (TryBuildBoundAuthToken(
                account.HbaPrivateKeyPkcs8,
                request.RequestUri.AbsoluteUri,
                request.Method.Method,
                bodyContent,
                account.HbaTemplate,
                unixTimestampOverride: null,
                out string token))
            {
                request.Headers.Remove(BoundAuthHeaderName);
                request.Headers.TryAddWithoutValidation(BoundAuthHeaderName, token);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Asynchronously reads request body content if needed, cryptographically signs the request
        /// using the account's HBA key, and attaches the <c>x-bound-auth-token</c> header without thread-pool starvation.
        /// </summary>
        public static async Task<bool> TryAttachHbaHeaderAsync(
            HttpRequestMessage request,
            Account account,
            CancellationToken cancellationToken = default)
        {
            if (request == null || account == null || !account.HasHbaBinding || request.RequestUri == null)
                return false;

            string? bodyContent = null;
            if (request.Content != null)
            {
                try
                {
                    bodyContent = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    AppServices.Logger.Debug("[RobloxHbaService] Read body content failed", ex);
                    return false;
                }
            }

            if (TryBuildBoundAuthToken(
                account.HbaPrivateKeyPkcs8,
                request.RequestUri.AbsoluteUri,
                request.Method.Method,
                bodyContent,
                account.HbaTemplate,
                unixTimestampOverride: null,
                out string token))
            {
                request.Headers.Remove(BoundAuthHeaderName);
                request.Headers.TryAddWithoutValidation(BoundAuthHeaderName, token);
                return true;
            }

            return false;
        }

        public static async Task<string?> RestoreWebViewModerationSessionAsync(
            Microsoft.Web.WebView2.Core.CoreWebView2 core,
            Account account,
            CaptureSnapshot targetSnapshot)
        {
            if (core == null || account == null || string.IsNullOrWhiteSpace(account.SecurityToken))
                return null;

            if (!string.IsNullOrWhiteSpace(account.SessionUserAgent))
            {
                try { core.Settings.UserAgent = account.SessionUserAgent; }
                catch (Exception ex) { _logger.Debug($"RestoreWebViewModerationSessionAsync set UserAgent threw: {ex.Message}"); }
            }

            core.CookieManager.DeleteCookiesWithDomainAndPath(".ROBLOSECURITY", "www.roblox.com", "/");
            var banCookie = core.CookieManager.CreateCookie(".ROBLOSECURITY", account.SecurityToken, ".roblox.com", "/");
            banCookie.IsHttpOnly = true;
            banCookie.IsSecure = true;
            core.CookieManager.AddOrUpdateCookie(banCookie);

            if (!string.IsNullOrWhiteSpace(account.BrowserTrackerID))
            {
                string uidPart = account.UserID > 0 ? account.UserID.ToString(CultureInfo.InvariantCulture) : string.Empty;
                var trackerCookie = core.CookieManager.CreateCookie(
                    "RBXEventTrackerV2",
                    $"CreateDate={DateTime.UtcNow.ToString("MM/dd/yyyy HH:mm:ss", CultureInfo.InvariantCulture)}&rbxid={uidPart}&browserid={account.BrowserTrackerID}",
                    ".roblox.com",
                    "/");
                core.CookieManager.AddOrUpdateCookie(trackerCookie);
            }

            if (!string.IsNullOrWhiteSpace(account.HbaPrivateKey))
            {
                if (targetSnapshot != null)
                {
                    targetSnapshot.PrivateKeyPkcs8Base64 = account.HbaPrivateKey;
                    if (!string.IsNullOrWhiteSpace(account.HbaPublicKey))
                        targetSnapshot.PublicKeySpkiBase64 = account.HbaPublicKey;
                    if (!string.IsNullOrWhiteSpace(account.HbaTemplate))
                        targetSnapshot.TokenTemplate = account.HbaTemplate;
                }

                string hbaScript = GetPersistedKeyInjectionScript(
                    account.HbaPrivateKey,
                    account.HbaPublicKey,
                    account.HbaTemplate);
                if (!string.IsNullOrEmpty(hbaScript))
                {
                    return await core.AddScriptToExecuteOnDocumentCreatedAsync(hbaScript);
                }
            }

            return null;
        }

        internal static bool TryDeriveSpkiPublicKeyBase64(string? pkcs8PrivateKeyBase64, out string spkiPublicKeyBase64)
        {
            spkiPublicKeyBase64 = string.Empty;
            if (string.IsNullOrWhiteSpace(pkcs8PrivateKeyBase64))
                return false;

            try
            {
                byte[] pkcs8 = Convert.FromBase64String(pkcs8PrivateKeyBase64.Trim());
                try
                {
                    using var ecdsa = ECDsa.Create();
                    ecdsa.ImportPkcs8PrivateKey(pkcs8, out _);
                    spkiPublicKeyBase64 = Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo());
                    return !string.IsNullOrWhiteSpace(spkiPublicKeyBase64);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(pkcs8);
                }
            }
            catch (Exception ex)
            {
                AppServices.Logger.Debug("[RobloxHbaService] TryDeriveSpkiPublicKeyBase64 failed", ex);
                return false;
            }
        }

        internal static void ApplyCaptureToAccount(Account account, CaptureSnapshot? snapshot)
        {
            if (account == null || snapshot == null)
                return;

            if (!string.IsNullOrWhiteSpace(snapshot.PrivateKeyPkcs8Base64) &&
                IsValidPkcs8EcdsaKey(snapshot.PrivateKeyPkcs8Base64))
            {
                account.HbaPrivateKey = snapshot.PrivateKeyPkcs8Base64;
                if (!string.IsNullOrWhiteSpace(snapshot.PublicKeySpkiBase64) &&
                    DoPublicAndPrivateKeysMatch(snapshot.PrivateKeyPkcs8Base64, snapshot.PublicKeySpkiBase64))
                {
                    account.HbaPublicKey = snapshot.PublicKeySpkiBase64;
                }
                else if (!string.IsNullOrWhiteSpace(account.HbaPublicKey) &&
                         !DoPublicAndPrivateKeysMatch(snapshot.PrivateKeyPkcs8Base64, account.HbaPublicKey))
                {
                    account.HbaPublicKey = string.Empty;
                }
            }

            if (!string.IsNullOrWhiteSpace(snapshot.TokenTemplate))
                account.HbaTemplate = NormalizeTemplate(snapshot.TokenTemplate);
            else if (account.HasHbaBinding)
                account.HbaTemplate = NormalizeTemplate(account.HbaTemplate);

            if (!string.IsNullOrWhiteSpace(snapshot.UserAgent))
                account.SessionUserAgent = snapshot.UserAgent;

            if (!string.IsNullOrWhiteSpace(snapshot.BrowserTrackerId))
            {
                account.BrowserTrackerID = snapshot.BrowserTrackerId.Trim();
                account.IsDirty = true;
            }

            string netSig = GetNetworkSignatureForAccount(account);
            if (netSig is not ("offline" or "unknown"))
                account.LastBoundIpSignature = netSig;
        }

        internal static void CopyHbaAndFingerprint(Account source, Account target)
        {
            if (source == null || target == null || ReferenceEquals(source, target))
                return;

            if (!string.IsNullOrWhiteSpace(source.HbaPrivateKey))
            {
                target.HbaPrivateKey = source.HbaPrivateKey;
                target.HbaPublicKey = source.HbaPublicKey;
                target.HbaTemplate = !string.IsNullOrWhiteSpace(source.HbaTemplate) ? source.HbaTemplate : DefaultTemplate;
            }

            if (!string.IsNullOrWhiteSpace(source.SessionUserAgent))
                target.SessionUserAgent = source.SessionUserAgent;
            if (!string.IsNullOrWhiteSpace(source.LastBoundIpSignature))
                target.LastBoundIpSignature = source.LastBoundIpSignature;
            if (!string.IsNullOrWhiteSpace(source.BrowserTrackerID))
            {
                target.BrowserTrackerID = source.BrowserTrackerID;
                target.IsDirty = true;
            }
        }
    }
}

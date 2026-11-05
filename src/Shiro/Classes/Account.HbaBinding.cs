#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Http;
using Shiro.Services;

namespace Shiro
{
    public partial class Account
    {
        private const string FieldHbaPrivateKey = "HbaPrivateKey";
        private const string FieldHbaPublicKey = "HbaPublicKey";
        private const string FieldHbaTemplate = "HbaTemplate";
        private const string FieldSessionUserAgent = "SessionUserAgent";
        private const string FieldLastBoundIpSig = "LastBoundIpSig";

        public Account(string username, string password, string alias)
        {
            Username = username;
            Password = password;
            Alias = alias;
        }

        [JsonIgnore]
        public string HbaPrivateKey
        {
            get => GetProtectedField(FieldHbaPrivateKey);
            set => SetProtectedField(FieldHbaPrivateKey, value);
        }

        [JsonIgnore]
        public string HbaPrivateKeyPkcs8
        {
            get => HbaPrivateKey;
            set => HbaPrivateKey = value;
        }

        [JsonIgnore]
        public string HbaPublicKey
        {
            get => GetProtectedField(FieldHbaPublicKey);
            set => SetProtectedField(FieldHbaPublicKey, value);
        }

        [JsonIgnore]
        public string HbaPublicKeySpki
        {
            get => HbaPublicKey;
            set => HbaPublicKey = value;
        }

        [JsonIgnore]
        public string HbaTemplate
        {
            get => GetProtectedField(FieldHbaTemplate);
            set => SetProtectedField(FieldHbaTemplate, value);
        }

        [JsonIgnore]
        public string SessionUserAgent
        {
            get => GetProtectedField(FieldSessionUserAgent);
            set => SetProtectedField(FieldSessionUserAgent, value);
        }

        [JsonIgnore]
        public string LastBoundIpSignature
        {
            get => GetProtectedField(FieldLastBoundIpSig);
            set => SetProtectedField(FieldLastBoundIpSig, value);
        }

        [JsonIgnore]
        public bool HasHbaBinding => !string.IsNullOrWhiteSpace(HbaPrivateKey);

        private string GetProtectedField(string key)
        {
            lock (FieldsSyncRoot)
            {
                return Fields != null && Fields.TryGetValue(key, out string? val) && val != null
                    ? val
                    : string.Empty;
            }
        }

        private void SetProtectedField(string key, string? value)
        {
            string normalized = value?.Trim() ?? string.Empty;
            lock (FieldsSyncRoot)
            {
                Fields ??= new Dictionary<string, string>();
                string current = Fields.TryGetValue(key, out string? existing) && existing != null ? existing : string.Empty;
                if (!string.Equals(current, normalized, StringComparison.Ordinal))
                {
                    if (string.IsNullOrEmpty(normalized))
                        Fields.Remove(key);
                    else
                        Fields[key] = normalized;
                    if (!IsSuppressingDeferredSave)
                        IsDirty = true;
                }
            }
        }

        internal string GetEffectiveUserAgent()
        {
            string ua = SessionUserAgent;
            return !string.IsNullOrWhiteSpace(ua) ? ua : DefaultBrowserUserAgent;
        }

        internal string BuildCookieHeaderValue(string? tokenOverride = null)
        {
            string token = tokenOverride ?? GetCurrentSecurityToken();
            if (string.IsNullOrWhiteSpace(token))
                return string.Empty;

            string btid = BrowserTrackerID;
            if (string.IsNullOrWhiteSpace(btid))
                return $".ROBLOSECURITY={token}";

            string uidPart = UserID > 0 ? UserID.ToString(CultureInfo.InvariantCulture) : string.Empty;
            return $".ROBLOSECURITY={token}; RBXEventTrackerV2=CreateDate=01/01/2026 00:00:00&rbxid={uidPart}&browserid={btid}";
        }

        public void ApplyHbaCapture(RobloxHbaService.CaptureSnapshot? snapshot)
        {
            if (snapshot == null)
                return;

            if (!string.IsNullOrWhiteSpace(snapshot.PrivateKeyPkcs8Base64) &&
                RobloxHbaService.IsValidPkcs8EcdsaKey(snapshot.PrivateKeyPkcs8Base64))
            {
                HbaPrivateKey = snapshot.PrivateKeyPkcs8Base64;
                if (!string.IsNullOrWhiteSpace(snapshot.PublicKeySpkiBase64))
                    HbaPublicKey = snapshot.PublicKeySpkiBase64;
            }

            if (!string.IsNullOrWhiteSpace(snapshot.TokenTemplate))
                HbaTemplate = snapshot.TokenTemplate;

            if (!string.IsNullOrWhiteSpace(snapshot.UserAgent))
                SessionUserAgent = snapshot.UserAgent;

            if (!string.IsNullOrWhiteSpace(snapshot.BrowserTrackerId))
            {
                BrowserTrackerID = snapshot.BrowserTrackerId.Trim();
                IsDirty = true;
            }

            string netSig = RobloxHbaService.GetLocalNetworkSignature();
            if (netSig is not ("offline" or "unknown"))
                LastBoundIpSignature = netSig;
        }

        internal void CopyHbaAndFingerprintTo(Account target)
        {
            if (target == null || ReferenceEquals(this, target))
                return;

            if (!string.IsNullOrWhiteSpace(HbaPrivateKey))
                target.HbaPrivateKey = HbaPrivateKey;
            if (!string.IsNullOrWhiteSpace(HbaPublicKey))
                target.HbaPublicKey = HbaPublicKey;
            if (!string.IsNullOrWhiteSpace(HbaTemplate))
                target.HbaTemplate = HbaTemplate;
            if (!string.IsNullOrWhiteSpace(SessionUserAgent))
                target.SessionUserAgent = SessionUserAgent;
            if (!string.IsNullOrWhiteSpace(LastBoundIpSignature))
                target.LastBoundIpSignature = LastBoundIpSignature;
            if (!string.IsNullOrWhiteSpace(BrowserTrackerID))
            {
                target.BrowserTrackerID = BrowserTrackerID;
                target.IsDirty = true;
            }
        }

        internal void ApplySessionFingerprintAndHba(
            HttpRequestMessage request,
            string? bodyContent = null,
            string? tokenOverride = null)
        {
            if (request == null)
                return;

            string cookieHeader = BuildCookieHeaderValue(tokenOverride);
            request.Headers.Remove("Cookie");
            if (!string.IsNullOrEmpty(cookieHeader))
                request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);

            request.Headers.Remove("User-Agent");
            request.Headers.TryAddWithoutValidation("User-Agent", GetEffectiveUserAgent());

            RobloxHbaService.TryAttachHbaHeader(request, this, bodyContent);
        }

        internal void ApplySessionFingerprintAndHbaToRestRequest(RestRequest request, string baseUrl)
        {
            if (request == null)
                return;

            if (!request.Headers.TryGetValue("User-Agent", out var reqUa) || string.IsNullOrEmpty(reqUa))
            {
                request.Headers["User-Agent"] = GetEffectiveUserAgent();
            }

            if (!string.IsNullOrWhiteSpace(BrowserTrackerID) &&
                request.Headers.TryGetValue("Cookie", out string? existingCookie) &&
                existingCookie != null &&
                !existingCookie.Contains("RBXEventTrackerV2=", StringComparison.OrdinalIgnoreCase))
            {
                string uidPart = UserID > 0 ? UserID.ToString(CultureInfo.InvariantCulture) : string.Empty;
                request.Headers["Cookie"] = $"{existingCookie}; RBXEventTrackerV2=CreateDate=01/01/2026 00:00:00&rbxid={uidPart}&browserid={BrowserTrackerID}";
            }

            string privKey = HbaPrivateKey;
            if (string.IsNullOrWhiteSpace(privKey))
                return;

            string resource = request.Resource ?? string.Empty;
            Uri finalUri;
            if (!Uri.TryCreate(resource, UriKind.Absolute, out Uri? abs) || abs == null)
            {
                string normalizedBase = string.IsNullOrWhiteSpace(baseUrl)
                    ? "https://www.roblox.com/"
                    : (baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/");
                finalUri = new Uri(new Uri(normalizedBase), resource.TrimStart('/'));
            }
            else
            {
                finalUri = abs;
            }

            bool isBodyMethod = request.Method is Method.Post or Method.Put or Method.Patch;

            // Include query parameters for non-body methods so the signed URL matches what RestClient actually sends
            if (!isBodyMethod && request.Parameters.Count > 0)
            {
                var ub = new UriBuilder(finalUri);
                var sb = new StringBuilder();
                if (!string.IsNullOrEmpty(ub.Query))
                    sb.Append(ub.Query.AsSpan().TrimStart('?'));

                foreach (var param in request.Parameters)
                {
                    if (sb.Length > 0)
                        sb.Append('&');
                    sb.Append(Uri.EscapeDataString(param.Key))
                      .Append('=')
                      .Append(Uri.EscapeDataString(param.Value));
                }
                ub.Query = sb.ToString();
                finalUri = ub.Uri;
            }

            string? bodyStr = null;
            if (isBodyMethod)
            {
                if (request.JsonBody != null)
                {
                    if (request.JsonBody is string rawJson)
                        bodyStr = rawJson;
                    else if (request.JsonBody is System.Text.Json.JsonElement je)
                        bodyStr = je.GetRawText();
                    else
                        bodyStr = Utilities.FastJson.Serialize(request.JsonBody);
                }
                else if (request.StringBody != null)
                {
                    bodyStr = request.StringBody;
                }
                else if (request.Parameters.Count > 0)
                {
                    bodyStr = string.Join("&", request.Parameters.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));
                }
            }

            if (RobloxHbaService.TryBuildBoundAuthToken(
                privKey,
                finalUri.AbsoluteUri,
                request.Method.ToString().ToUpperInvariant(),
                bodyStr,
                HbaTemplate,
                unixTimestampOverride: null,
                out string batToken))
            {
                request.Headers[RobloxHbaService.BoundAuthHeaderName] = batToken;
            }
        }

        /// <summary>
        /// Proactively exchanges an HBA-signed authentication ticket via <c>/v1/authentication-ticket/redeem</c>
        /// when the local network interface changes (e.g., turning off VPN back to Vietnam IP),
        /// re-binding the rotated <c>.ROBLOSECURITY</c> session cookie to the current IP.
        /// </summary>
        public async Task<bool> TryRebindSessionToCurrentNetworkAsync(bool force = false, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(GetCurrentSecurityToken()))
                return false;

            string currentNetSig = RobloxHbaService.GetLocalNetworkSignature();
            string previousSig = LastBoundIpSignature;

            if (!force)
            {
                if (currentNetSig is "offline" or "unknown")
                    return false;

                if (string.IsNullOrEmpty(previousSig))
                {
                    LastBoundIpSignature = currentNetSig;
                    AppServices.AccountRepository.QueueDeferredSave();
                    return true;
                }

                if (string.Equals(previousSig, currentNetSig, StringComparison.Ordinal))
                    return true;
            }

            try
            {
                var (ticketOk, ticket) = await GetAuthTicketAsync(cancellationToken).ConfigureAwait(false);
                if (!ticketOk || string.IsNullOrWhiteSpace(ticket))
                    return false;

                HttpClient httpClient = GetSessionHttpClient();
                string payloadJson = JsonSerializer.Serialize(new AuthenticationTicketPayload(ticket), ShiroAppJson.Default.AuthenticationTicketPayload);

                string currentCsrf;
                lock (CookieSyncRoot)
                {
                    currentCsrf = CSRFToken ?? string.Empty;
                }

                for (int attempt = 0; attempt < 2; attempt++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using var request = new HttpRequestMessage(HttpMethod.Post, "https://auth.roblox.com/v1/authentication-ticket/redeem");
                    ApplySessionFingerprintAndHba(request, payloadJson);
                    if (!string.IsNullOrEmpty(currentCsrf))
                        request.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", currentCsrf);
                    request.Headers.TryAddWithoutValidation("Origin", "https://www.roblox.com");
                    request.Headers.TryAddWithoutValidation("Referer", "https://www.roblox.com/");
                    request.Headers.TryAddWithoutValidation("rbxauthenticationnegotiation", "1");
                    request.Content = new StringContent(payloadJson, Encoding.UTF8, "application/json");

                    using HttpResponseMessage response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                    bool rotated = TryUpdateRotatedCookie(response);

                    if (response.Headers.TryGetValues("x-csrf-token", out var csrfVals))
                    {
                        string? freshCsrf = csrfVals.FirstOrDefault();
                        if (!string.IsNullOrWhiteSpace(freshCsrf))
                        {
                            currentCsrf = freshCsrf;
                            lock (CookieSyncRoot)
                            {
                                CSRFToken = freshCsrf;
                                TokenSet = DateTime.Now;
                            }
                        }
                    }

                    if (response.StatusCode == HttpStatusCode.Forbidden && attempt == 0 && !string.IsNullOrEmpty(currentCsrf))
                        continue;

                    if (rotated || response.IsSuccessStatusCode)
                    {
                        if (currentNetSig is not ("offline" or "unknown"))
                            LastBoundIpSignature = currentNetSig;
                        AppServices.AccountRepository.QueueDeferredSave();
                        Program.Logger?.Info($"[HbaRebind] Session synchronized to current network for {Username} (rotated={rotated}).");
                        return true;
                    }

                    break;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"[HbaRebind] Fail-soft during network session rebind for {Username}: {ex.Message}");
            }

            return false;
        }
    }
}

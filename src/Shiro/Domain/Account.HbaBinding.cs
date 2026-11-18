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
        private const string FieldAccountStatusIssue = "AccountStatusIssue";

        internal static bool IsProtectedInternalField(string? fieldName)
        {
            if (string.IsNullOrWhiteSpace(fieldName))
                return false;

            ReadOnlySpan<char> name = fieldName.AsSpan().Trim();
            return name.Equals(FieldHbaPrivateKey, StringComparison.OrdinalIgnoreCase) ||
                   name.Equals(FieldHbaPublicKey, StringComparison.OrdinalIgnoreCase) ||
                   name.Equals(FieldHbaTemplate, StringComparison.OrdinalIgnoreCase) ||
                   name.Equals(FieldSessionUserAgent, StringComparison.OrdinalIgnoreCase) ||
                   name.Equals(FieldLastBoundIpSig, StringComparison.OrdinalIgnoreCase) ||
                   name.Equals(FieldAccountStatusIssue, StringComparison.OrdinalIgnoreCase) ||
                   name.StartsWith("Hba", StringComparison.OrdinalIgnoreCase);
        }

        internal Dictionary<string, string> GetPublicFieldsSnapshot()
        {
            lock (FieldsSyncRoot)
            {
                var snapshot = new Dictionary<string, string>();
                if (Fields != null)
                {
                    foreach (var kvp in Fields)
                    {
                        if (!IsProtectedInternalField(kvp.Key))
                            snapshot[kvp.Key] = kvp.Value;
                    }
                }
                return snapshot;
            }
        }

        public Account(string username, string password, string alias)
        {
            Username = username;
            Password = password;
            Alias = alias;
        }

        [JsonIgnore]
        public string HbaPrivateKey
        {
            get
            {
                MigrateProtectedFieldsFromDictionary();
                lock (CookieSyncRoot)
                    return InMemorySecretProtector.Unprotect(_protectedHbaPrivateKey);
            }
            set
            {
                string normalized = value?.Trim() ?? string.Empty;
                bool changed;
                lock (CookieSyncRoot)
                {
                    string current = InMemorySecretProtector.Unprotect(_protectedHbaPrivateKey);
                    changed = !string.Equals(current, normalized, StringComparison.Ordinal);
                    if (changed)
                    {
                        InMemorySecretProtector.ZeroAndClear(ref _protectedHbaPrivateKey);
                        _protectedHbaPrivateKey = string.IsNullOrEmpty(normalized) ? null : InMemorySecretProtector.Protect(normalized);
                    }
                }
                lock (FieldsSyncRoot)
                    Fields?.Remove(FieldHbaPrivateKey);
                if (changed && !IsSuppressingDeferredSave)
                    IsDirty = true;
            }
        }

        internal void MigrateProtectedFieldsFromDictionary()
        {
            string? legacyPriv = null;
            lock (FieldsSyncRoot)
            {
                if (Fields != null && Fields.TryGetValue(FieldHbaPrivateKey, out string? raw))
                {
                    legacyPriv = raw?.Trim();
                    Fields.Remove(FieldHbaPrivateKey);
                }
            }
            if (!string.IsNullOrEmpty(legacyPriv))
            {
                lock (CookieSyncRoot)
                {
                    if (_protectedHbaPrivateKey == null)
                        _protectedHbaPrivateKey = InMemorySecretProtector.Protect(legacyPriv);
                }
            }
        }

        [JsonIgnore] public string HbaPrivateKeyPkcs8 { get => HbaPrivateKey; set => HbaPrivateKey = value; }
        [JsonIgnore] public string HbaPublicKey { get => GetProtectedField(FieldHbaPublicKey); set => SetProtectedField(FieldHbaPublicKey, value); }
        [JsonIgnore] public string HbaPublicKeySpki { get => HbaPublicKey; set => HbaPublicKey = value; }
        [JsonIgnore] public string HbaTemplate { get => GetProtectedField(FieldHbaTemplate); set => SetProtectedField(FieldHbaTemplate, value); }
        [JsonIgnore] public string SessionUserAgent { get => GetProtectedField(FieldSessionUserAgent); set => SetProtectedField(FieldSessionUserAgent, value); }
        [JsonIgnore] public string LastBoundIpSignature { get => GetProtectedField(FieldLastBoundIpSig); set => SetProtectedField(FieldLastBoundIpSig, value); }
        [JsonIgnore] public bool HasHbaBinding => !string.IsNullOrWhiteSpace(HbaPrivateKey);

        public void ClearHbaBinding()
        {
            HbaPrivateKey = string.Empty;
            HbaPublicKey = string.Empty;
            HbaTemplate = string.Empty;
            LastBoundIpSignature = string.Empty;
        }

        private string GetProtectedField(string key)
        {
            lock (FieldsSyncRoot)
                return Fields != null && Fields.TryGetValue(key, out string? val) && val != null ? val : string.Empty;
        }

        private void SetProtectedField(string key, string? value, bool ignoreSuppress = false)
        {
            string normalized = value?.Trim() ?? string.Empty;
            lock (FieldsSyncRoot)
            {
                Fields ??= new Dictionary<string, string>();
                string current = Fields.TryGetValue(key, out string? existing) && existing != null ? existing : string.Empty;
                if (string.Equals(current, normalized, StringComparison.Ordinal))
                    return;

                if (string.IsNullOrEmpty(normalized)) Fields.Remove(key);
                else Fields[key] = normalized;
                if (ignoreSuppress || !IsSuppressingDeferredSave) IsDirty = true;
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
            string nowUtc = DateTime.UtcNow.ToString("MM/dd/yyyy HH:mm:ss", CultureInfo.InvariantCulture);
            return $".ROBLOSECURITY={token}; RBXEventTrackerV2=CreateDate={nowUtc}&rbxid={uidPart}&browserid={btid}";
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
                HbaTemplate = RobloxHbaService.NormalizeTemplate(snapshot.TokenTemplate);
            else if (HasHbaBinding)
                HbaTemplate = RobloxHbaService.NormalizeTemplate(HbaTemplate);

            if (!string.IsNullOrWhiteSpace(snapshot.UserAgent))
                SessionUserAgent = snapshot.UserAgent;

            if (!string.IsNullOrWhiteSpace(snapshot.BrowserTrackerId))
            {
                BrowserTrackerID = snapshot.BrowserTrackerId.Trim();
                IsDirty = true;
            }

            string netSig = RobloxHbaService.GetNetworkSignatureForAccount(this);
            if (netSig is not ("offline" or "unknown"))
                LastBoundIpSignature = netSig;
        }

        internal void CopyHbaAndFingerprintTo(Account target)
        {
            if (target == null || ReferenceEquals(this, target))
                return;

            if (!string.IsNullOrWhiteSpace(HbaPrivateKey))
            {
                target.HbaPrivateKey = HbaPrivateKey;
                target.HbaPublicKey = HbaPublicKey;
                target.HbaTemplate = !string.IsNullOrWhiteSpace(HbaTemplate) ? HbaTemplate : RobloxHbaService.DefaultTemplate;
            }

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
            string? tokenOverride = null,
            bool includeHba = true)
        {
            if (request == null)
                return;

            string cookieHeader = BuildCookieHeaderValue(tokenOverride);
            request.Headers.Remove("Cookie");
            if (!string.IsNullOrEmpty(cookieHeader))
                request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);

            request.Headers.Remove("User-Agent");
            request.Headers.TryAddWithoutValidation("User-Agent", GetEffectiveUserAgent());

            if (includeHba)
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
                string nowUtc = DateTime.UtcNow.ToString("MM/dd/yyyy HH:mm:ss", CultureInfo.InvariantCulture);
                request.Headers["Cookie"] = $"{existingCookie}; RBXEventTrackerV2=CreateDate={nowUtc}&rbxid={uidPart}&browserid={BrowserTrackerID}";
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

            string? bodyStr = isBodyMethod ? RestClient.SerializeRequestBodyForSigning(request) : null;

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
    }
}

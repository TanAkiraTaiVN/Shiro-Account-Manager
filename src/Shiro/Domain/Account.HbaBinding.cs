#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
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

        internal static void StripTransientWindowFields(Dictionary<string, string> snapshot)
        {
            snapshot.Remove("Window_Position_X");
            snapshot.Remove("Window_Position_Y");
            snapshot.Remove("Window_Width");
            snapshot.Remove("Window_Height");
        }

        internal static bool ExceedsMaxLength(string fieldName, string? value, int maxLength)
        {
            if (value == null)
                return true;
            if (value.Length <= maxLength)
                return false;

            AppServices.Logger?.Debug($"[Account] Ignored oversized {fieldName} value ({value.Length} > {maxLength} chars).");
            return true;
        }

        internal static bool CanSetPublicField(string name, string? value)
        {
            if (string.IsNullOrWhiteSpace(name))
                return false;
            if (IsProtectedInternalField(name))
            {
                AppServices.Logger?.Debug($"[Account] Ignored write to protected field '{name}'.");
                return false;
            }
            return value == null || !ExceedsMaxLength(name, value, 5000);
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
                        bool hasKey = !string.IsNullOrEmpty(normalized);
                        _protectedHbaPrivateKey = hasKey ? InMemorySecretProtector.Protect(normalized) : null;
                        _hasHbaPrivateKey = hasKey;
                    }
                }
                lock (FieldsSyncRoot)
                {
                    Fields?.Remove(FieldHbaPrivateKey);
                    if (changed &&
                        Fields != null &&
                        Fields.TryGetValue(FieldHbaPublicKey, out string? existingPub) &&
                        !string.IsNullOrEmpty(existingPub) &&
                        (string.IsNullOrEmpty(normalized) || !RobloxHbaService.DoPublicAndPrivateKeysMatch(normalized, existingPub)))
                    {
                        Fields.Remove(FieldHbaPublicKey);
                    }
                }
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
                    _hasHbaPrivateKey = _protectedHbaPrivateKey != null;
                }
            }
        }

        [JsonIgnore] public string HbaPrivateKeyPkcs8 { get => HbaPrivateKey; set => HbaPrivateKey = value; }
        [JsonIgnore] public string HbaPublicKey { get => GetProtectedField(FieldHbaPublicKey); set => SetProtectedField(FieldHbaPublicKey, value); }
        [JsonIgnore] public string HbaPublicKeySpki { get => HbaPublicKey; set => HbaPublicKey = value; }
        [JsonIgnore] public string HbaTemplate { get => GetProtectedField(FieldHbaTemplate); set => SetProtectedField(FieldHbaTemplate, value); }
        [JsonIgnore] public string SessionUserAgent { get => GetProtectedField(FieldSessionUserAgent); set => SetProtectedField(FieldSessionUserAgent, value); }
        [JsonIgnore] public string LastBoundIpSignature { get => GetProtectedField(FieldLastBoundIpSig); set => SetProtectedField(FieldLastBoundIpSig, value); }
        [JsonIgnore]
        public bool HasHbaBinding
        {
            get
            {
                bool hasLegacy;
                lock (FieldsSyncRoot)
                    hasLegacy = Fields != null && Fields.ContainsKey(FieldHbaPrivateKey);
                if (hasLegacy)
                    MigrateProtectedFieldsFromDictionary();
                lock (CookieSyncRoot)
                    return _hasHbaPrivateKey;
            }
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

        public void ApplyHbaCapture(RobloxHbaService.CaptureSnapshot? snapshot) =>
            RobloxHbaService.ApplyCaptureToAccount(this, snapshot);

        internal void CopyHbaAndFingerprintTo(Account target) =>
            RobloxHbaService.CopyHbaAndFingerprint(this, target);

        internal Account CloneSnapshotForUndo()
        {
            using (BeginSuppressDeferredSaveScope())
            {
                return new Account
                {
                    Username = Username, UserID = UserID, Valid = Valid, Pinned = Pinned,
                    Alias = Alias, Group = Group, Description = Description, Proxy = Proxy,
                    RobuxBalance = RobuxBalance, PendingRobux = PendingRobux, BrowserTrackerID = BrowserTrackerID,
                    LastUse = LastUse, LastAttemptedRefresh = LastAttemptedRefresh, LastEconomyCheck = LastEconomyCheck,
                    SecurityToken = SecurityToken, Password = Password, HbaPrivateKey = HbaPrivateKey,
                    HbaPublicKey = HbaPublicKey, HbaTemplate = HbaTemplate, SessionUserAgent = SessionUserAgent,
                    LastBoundIpSignature = LastBoundIpSignature, AccountStatusIssue = AccountStatusIssue,
                    Fields = GetPublicFieldsSnapshot(),
                    IsDirty = true
                };
            }
        }

        internal async Task<T> RunWithSessionGateAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken = default)
        {
            await _sessionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await action().ConfigureAwait(false);
            }
            finally
            {
                _sessionGate.Release();
            }
        }
    }
}

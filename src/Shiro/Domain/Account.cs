using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json.Serialization;
using Shiro.Services;

namespace Shiro
{
    public enum SessionHealth
    {
        Healthy,
        ShortLived,
        Expired,
        Locked
    }

    public partial class Account : IComparable<Account>
    {
        public bool Valid = true;
        // Stored with the account record so the modern dashboard can keep
        // frequently used accounts at the top across restarts.
        public bool Pinned;
        [JsonIgnore] public bool IsDirty { get; set; } = true;
        private System.Threading.Lock? _fieldsLock = new();
        internal System.Threading.Lock FieldsSyncRoot => _fieldsLock ?? Interlocked.CompareExchange(ref _fieldsLock, new System.Threading.Lock(), null) ?? _fieldsLock!;

        private static readonly AsyncLocal<int> _suppressDeferredSaveDepth = new();

        internal static bool IsSuppressingDeferredSave => _suppressDeferredSaveDepth.Value > 0;

        internal static IDisposable BeginSuppressDeferredSaveScope()
        {
            _suppressDeferredSaveDepth.Value++;
            return new SuppressDeferredSaveScope();
        }

        internal Dictionary<string, string> GetFieldsSnapshot()
        {
            MigrateProtectedFieldsFromDictionary();
            lock (FieldsSyncRoot)
                return Fields != null ? new Dictionary<string, string>(Fields) : new Dictionary<string, string>();
        }

        internal Dictionary<string, string> GetSerializedFieldsSnapshot()
        {
            var snapshot = GetFieldsSnapshot();
            StripTransientWindowFields(snapshot);
            string priv = HbaPrivateKey;
            if (!string.IsNullOrEmpty(priv))
                snapshot["HbaPrivateKey"] = priv;
            return snapshot;
        }

        [JsonIgnore]
        public string AccountStatusIssue
        {
            get => GetProtectedField("AccountStatusIssue");
            set => SetProtectedField("AccountStatusIssue", value, ignoreSuppress: true);
        }

        [JsonIgnore]
        public bool IsBannedOrLocked => !Valid && !string.IsNullOrEmpty(AccountStatusIssue);
        private byte[]? _protectedSecurityToken;
        private byte[]? _protectedHbaPrivateKey;
        private readonly SemaphoreSlim _sessionGate = new(1, 1);
        private bool _hasSecurityToken;
        private bool _hasHbaPrivateKey;
        private int _securityTokenPayloadLength;
        public string SecurityToken
        {
            get => GetCurrentSecurityToken();
            set
            {
                string sanitized = SanitizeCookie(value);
                lock (CookieSyncRoot)
                {
                    if (TryReplaceProtectedSecurityTokenLocked(sanitized))
                    {
                        IsDirty = true;
                        Interlocked.Increment(ref _cookieRotationVersion);
                    }
                }
            }
        }
        public string Username = string.Empty;
        public DateTime LastUse;
        private string _Alias = "";
        private string _Description = "";
        private byte[]? _protectedPassword;
        private string _Group = "Default";
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string Group
        {
            get => _Group;
            set
            {
                string nextGroup = value ?? "Default";
                if (!string.Equals(_Group, nextGroup, StringComparison.Ordinal))
                {
                    _Group = nextGroup;
                    if (!IsSuppressingDeferredSave)
                    {
                        IsDirty = true;
                        AppServices.AccountRepository.QueueDeferredSave();
                    }
                }
            }
        }
        private string? _Proxy;
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Proxy
        {
            get => _Proxy;
            set
            {
                if (!string.Equals(_Proxy, value, StringComparison.Ordinal))
                {
                    _Proxy = value;
                    if (!IsSuppressingDeferredSave)
                    {
                        IsDirty = true;
                        AppServices.AccountRepository.QueueDeferredSave();
                    }
                }
            }
        }
        public long UserID;
        public Dictionary<string, string> Fields = new Dictionary<string, string>();
        public DateTime LastAttemptedRefresh;
        [JsonIgnore] public DateTime TokenSet;
        [JsonIgnore] public DateTime LastAppLaunch;
        [JsonIgnore] public string? CSRFToken;
        [JsonIgnore] public UserPresence? Presence;
        [JsonIgnore] public bool LastAuthTicketRateLimited;
        [JsonIgnore] public bool LastNativeLaunchRateLimited;
        [JsonIgnore] public int LastLaunchedProcessId;
        public long RobuxBalance = -1;
        public long PendingRobux = 0;
        [JsonIgnore] public DateTime LastEconomyCheck;

        public int CompareTo(Account? compareTo)
        {
            if (compareTo == null) return 1;
            int grpCmp = string.Compare(Group, compareTo.Group, StringComparison.OrdinalIgnoreCase);
            return grpCmp != 0 ? grpCmp : string.Compare(Username, compareTo.Username, StringComparison.OrdinalIgnoreCase);
        }

        public const string BrowserHomeResult = "BROWSER_HOME_STARTED";
        public string BrowserTrackerID = string.Empty;

        internal void EnsureBrowserTrackerId()
        {
            if (string.IsNullOrEmpty(BrowserTrackerID))
                BrowserTrackerID = $"{Random.Shared.Next(100000, 175000)}{Random.Shared.Next(100000, 900000)}";
        }

        internal const int MaxAliasLength = 50;
        internal const int MaxDescriptionLength = 5000;

        public string Alias
        {
            get => _Alias;
            set
            {
                if (ExceedsMaxLength(nameof(Alias), value, MaxAliasLength))
                    return;

                if (_Alias != value)
                {
                    _Alias = value;
                    if (!IsSuppressingDeferredSave)
                    {
                        IsDirty = true;
                        AppServices.AccountRepository.QueueDeferredSave();
                    }
                }
            }
        }
        public string Description
        {
            get => _Description;
            set
            {
                if (ExceedsMaxLength(nameof(Description), value, MaxDescriptionLength))
                    return;

                if (_Description != value)
                {
                    _Description = value;
                    if (!IsSuppressingDeferredSave)
                    {
                        IsDirty = true;
                        AppServices.AccountRepository.QueueDeferredSave();
                    }
                }
            }
        }
        public string Password
        {
            get => GetCurrentPassword();
            set
            {
                if (ExceedsMaxLength(nameof(Password), value, 5000))
                    return;

                if (TryReplaceProtectedPassword(value) && !IsSuppressingDeferredSave)
                {
                    IsDirty = true;
                    AppServices.AccountRepository.QueueDeferredSave();
                }
            }
        }

        /// <summary>
        /// Sanitizes and extracts the pure .ROBLOSECURITY token from raw strings,
        /// handling leading prefixes, wrapping quotes, trailing cookie attributes, and whitespace.
        /// </summary>
        public static string SanitizeCookie(string raw) =>
            RobloxCookieHeaderParser.SanitizeCookie(raw);

        /// <summary>
        /// Returns the base64 character length of the middle payload segment (Dot[1]) of a .ROBLOSECURITY token.
        /// </summary>
        public static int GetSessionTokenPayloadLength(string cookie) =>
            RobloxCookieHeaderParser.GetSessionTokenPayloadLength(cookie);

        /// <summary>
        /// Returns true if the given .ROBLOSECURITY token is a downgraded 592-byte (790-char)
        /// short-lived fallback session rather than a full 704-byte (939-char) long-lived session.
        /// </summary>
        public static bool IsShortLivedCookie(string cookie)
        {
            int middleLen = GetSessionTokenPayloadLength(cookie);
            return middleLen >= 500 && middleLen < 850;
        }

        /// <summary>
        /// Detects whether the stored .ROBLOSECURITY token is a downgraded 592-byte (790-char)
        /// fallback session (issued when secureAuthenticationIntent was stripped) that has exceeded
        /// Roblox's ~30-minute server-side TTL on auth.roblox.com / accountsettings.roblox.com.
        /// Full sessions issued with secureAuthenticationIntent have a 704-byte (939-char) middle payload.
        /// </summary>
        public bool HasExpiredShortLivedCookie()
        {
            var (hasToken, _) = GetCachedTokenMetadata();
            if (!hasToken)
                return true;

            // NOTE: Do NOT invalidate short-lived cookies based on a local clock. LastUse is not refreshed
            // on every request, so this used to flag freshly re-logged accounts (or any account in a long
            // relogin queue) as expired. The server-side checks in ValidateSessionAsync are authoritative.
            return false;
        }

        [JsonIgnore]
        public bool IsShortLivedSession
        {
            get
            {
                if (!Valid) return false;
                var (hasToken, payloadLen) = GetCachedTokenMetadata();
                return hasToken && payloadLen is >= 500 and < 850 && !HasExpiredShortLivedCookie();
            }
        }

        [JsonIgnore]
        public SessionHealth SessionHealthStatus
        {
            get
            {
                if (IsBannedOrLocked) return SessionHealth.Locked;
                var (hasToken, payloadLen) = GetCachedTokenMetadata();
                if (!Valid || !hasToken || HasExpiredShortLivedCookie())
                    return SessionHealth.Expired;
                if (payloadLen is >= 500 and < 850)
                    return SessionHealth.ShortLived;
                return SessionHealth.Healthy;
            }
        }

        public Account() { }

        public static Task<Account> CreateAsync(
            string cookie,
            string? accountJson = null,
            CancellationToken cancellationToken = default,
            RobloxHbaService.CaptureSnapshot? hbaCapture = null) =>
            CreateAsync(cookie, accountJson, cancellationToken, hbaCapture, expectedUsername: null);

        public static Task<Account> CreateAsync(
            string cookie,
            string? accountJson,
            CancellationToken cancellationToken,
            RobloxHbaService.CaptureSnapshot? hbaCapture,
            string? expectedUsername) =>
            AppServices.AccountSessionClient.CreateValidatedAccountAsync(
                cookie,
                accountJson,
                cancellationToken,
                hbaCapture,
                expectedUsername);

        /// <summary>
        /// Synchronous account constructor. Prefer <see cref="CreateAsync(string, string?, CancellationToken, RobloxHbaService.CaptureSnapshot?)"/> to avoid blocking threads.
        /// </summary>
        public Account(string Cookie, string? AccountJSON = null)
        {
            SecurityToken = Cookie;

            if (!string.IsNullOrEmpty(AccountJSON) && Utilities.TryParseJson(AccountJSON, out AccountJson? data) && data != null && data.UserId > 0)
            {
                Username = data.Name;
                UserID = data.UserId;
                Valid = true;
                LastUse = DateTime.Now;
                AppServices.SelectionState.LastValidAccount = this;
                return;
            }

            Valid = false;
        }

        public string GetField(string Name)
        {
            if (IsProtectedInternalField(Name))
                return string.Empty;
            lock (FieldsSyncRoot)
                return Fields != null && Fields.TryGetValue(Name, out string? value) ? value : string.Empty;
        }

        public void SetField(string Name, string Value)
        {
            if (!CanSetPublicField(Name, Value))
                return;
            lock (FieldsSyncRoot)
            {
                Fields ??= new Dictionary<string, string>();
                Fields[Name] = Value;
            }
            IsDirty = true;
            AppServices.AccountRepository.QueueDeferredSave();
        }

        public void SetWindowBounds(int x, int y, int width, int height)
        {
            ApplyWindowBoundsInMemory(x, y, width, height);
            AccountRuntimeStateStore.RecordWindowBounds(this, x, y, width, height);
        }

        internal void ApplyWindowBoundsInMemory(int x, int y, int width, int height)
        {
            lock (FieldsSyncRoot)
            {
                Fields ??= new Dictionary<string, string>();
                Fields["Window_Position_X"] = x.ToString(CultureInfo.InvariantCulture);
                Fields["Window_Position_Y"] = y.ToString(CultureInfo.InvariantCulture);
                Fields["Window_Width"] = width.ToString(CultureInfo.InvariantCulture);
                Fields["Window_Height"] = height.ToString(CultureInfo.InvariantCulture);
            }
        }

        internal void RecordVolatileLastUse(DateTime timestamp)
        {
            lock (CookieSyncRoot)
            {
                LastUse = timestamp;
            }
            AccountRuntimeStateStore.RecordLastUse(this, timestamp);
        }

        public void RemoveField(string Name)
        {
            if (IsProtectedInternalField(Name))
                return;
            bool removed;
            lock (FieldsSyncRoot)
                removed = Fields != null && Fields.Remove(Name);
            if (removed)
            {
                IsDirty = true;
                AppServices.AccountRepository.QueueDeferredSave();
            }
        }
    }
}

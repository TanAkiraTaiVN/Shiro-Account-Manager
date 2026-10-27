using RBX_Alt_Manager.Classes;
using RBX_Alt_Manager.Http;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json.Serialization;
using RBX_Alt_Manager.Services;

namespace RBX_Alt_Manager
{
    public enum SessionHealth
    {
        Healthy,
        ShortLived,
        Expired,
        Locked,
        Unknown
    }

    public partial class Account : IComparable<Account>
    {
        public bool Valid = true;
        // Stored with the account record so the modern dashboard can keep
        // frequently used accounts at the top across restarts.
        public bool Pinned;
        [JsonIgnore] public bool IsDirty { get; set; } = true;

        [JsonIgnore]
        public string AccountStatusIssue
        {
            get => Fields != null && Fields.TryGetValue("AccountStatusIssue", out var v) ? v : string.Empty;
            set
            {
                Fields ??= new Dictionary<string, string>();
                string current = AccountStatusIssue;
                string normalized = value?.Trim() ?? string.Empty;
                if (!string.Equals(current, normalized, StringComparison.Ordinal))
                {
                    if (string.IsNullOrEmpty(normalized))
                        Fields.Remove("AccountStatusIssue");
                    else
                        Fields["AccountStatusIssue"] = normalized;
                    IsDirty = true;
                }
            }
        }

        [JsonIgnore]
        public bool IsBannedOrLocked => !Valid && !string.IsNullOrEmpty(AccountStatusIssue);
        private string _SecurityToken = "";
        public string SecurityToken
        {
            get => _SecurityToken;
            set
            {
                string sanitized = SanitizeCookie(value);
                if (!string.Equals(_SecurityToken, sanitized, StringComparison.Ordinal))
                {
                    _SecurityToken = sanitized;
                    IsDirty = true;
                }
            }
        }
        public string Username = string.Empty;
        public DateTime LastUse;
        private string _Alias = "";
        private string _Description = "";
        private string _Password = "";
        private string _Group = "Default";
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string Group
        {
            get => _Group;
            set
            {
                if (!string.Equals(_Group, value, StringComparison.Ordinal))
                {
                    _Group = value ?? "Default";
                    IsDirty = true;
                    AccountManager.QueueDeferredSave();
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
                    IsDirty = true;
                    AccountManager.QueueDeferredSave();
                }
            }
        }
        public long UserID;
        public Dictionary<string, string> Fields = new Dictionary<string, string>();
        public DateTime LastAttemptedRefresh;
        [JsonIgnore] public DateTime PinUnlocked;
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

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, [MarshalAs(UnmanagedType.Bool)] bool bRepaint);

        public int CompareTo(Account? compareTo)
        {
            if (compareTo == null)
                return 1;
            else
                return Group.CompareTo(compareTo.Group);
        }

        // Retry up to 4 attempts on transient network glitches, stale CSRF (403), or short burst 429s
        // with backoff to guarantee high-concurrency multi-launch reliability.
        private const int AuthTicketMaxAttempts = 4;
        // The native client redeems the ticket after RAM has started it. That
        // second request can be rate-limited even when ticket creation worked,
        // so keep a separate adaptive recovery window for confirmed 429s.
        private const int NativeAuthRecoverySeconds = 90;
        // Roblox accepts the roblox-player URI asynchronously.  On a busy
        // machine the protocol handler can take longer than five seconds to
        // hand the ticket to RobloxPlayerBeta.exe.  Moving the next account
        // before that handoff is complete is what caused intermittent
        // account-2/account-3 failures in multi-launch.
        private const int NativeHandoffTimeoutMilliseconds = 15000;
        private static readonly SemaphoreSlim NativeLaunchGate = new SemaphoreSlim(1, 1);
        public const string BrowserHomeResult = "BROWSER_HOME_STARTED";
        public string BrowserTrackerID = string.Empty;

        public string Alias
        {
            get => _Alias;
            set
            {
                if (value == null || value.Length > 50)
                    return;

                if (_Alias != value)
                {
                    _Alias = value;
                    IsDirty = true;
                    AccountManager.QueueDeferredSave();
                }
            }
        }
        public string Description
        {
            get => _Description;
            set
            {
                if (value == null || value.Length > 5000)
                    return;

                if (_Description != value)
                {
                    _Description = value;
                    IsDirty = true;
                    AccountManager.QueueDeferredSave();
                }
            }
        }
        public string Password
        {
            get => _Password;
            set
            {
                if (value == null || value.Length > 5000)
                    return;

                if (_Password != value)
                {
                    _Password = value;
                    IsDirty = true;
                    AccountManager.QueueDeferredSave();
                }
            }
        }

        /// <summary>
        /// Sanitizes and extracts the pure .ROBLOSECURITY token from raw strings,
        /// handling leading prefixes (".ROBLOSECURITY=", "ROBLOSECURITY="),
        /// wrapping quotes, trailing cookie attributes (path, domain, semicolons), and whitespace.
        /// </summary>
        public static string SanitizeCookie(string raw)
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

        /// <summary>
        /// Returns the base64 character length of the middle payload segment (Dot[1]) of a .ROBLOSECURITY token.
        /// Full long-lived sessions issued with secureAuthenticationIntent have a 939-char (704-byte) payload,
        /// whereas downgraded fallback sessions have a 790-char (592-byte) payload.
        /// </summary>
        public static int GetSessionTokenPayloadLength(string cookie)
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
            if (string.IsNullOrWhiteSpace(_SecurityToken))
                return true;

            // NOTE: Do NOT invalidate short-lived cookies based on a local clock. LastUse is not refreshed
            // on every request, so this used to flag freshly re-logged accounts (or any account in a long
            // relogin queue) as expired. The server-side checks in ValidateSessionAsync are authoritative.
            return false;
        }

        [JsonIgnore]
        public bool IsShortLivedSession => Valid && IsShortLivedCookie(_SecurityToken) && !HasExpiredShortLivedCookie();

        [JsonIgnore]
        public SessionHealth SessionHealthStatus
        {
            get
            {
                if (IsBannedOrLocked) return SessionHealth.Locked;
                if (!Valid || string.IsNullOrWhiteSpace(_SecurityToken) || HasExpiredShortLivedCookie())
                    return SessionHealth.Expired;
                if (IsShortLivedCookie(_SecurityToken))
                    return SessionHealth.ShortLived;
                return SessionHealth.Healthy;
            }
        }

        public Account() { }

        public static async Task<Account> CreateAsync(string cookie, string? accountJson = null, CancellationToken cancellationToken = default)
        {
            var account = new Account { SecurityToken = cookie, Valid = false, LastUse = DateTime.Now };

            if (!string.IsNullOrEmpty(accountJson) && Utilities.TryParseJson(accountJson, out AccountJson? data) && data != null && data.UserId > 0)
            {
                account.Username = data.Name;
                account.UserID = data.UserId;
                account.Valid = true;
                account.LastUse = DateTime.Now;
                AccountManager.LastValidAccount = account;
                return account;
            }

            if (await account.ValidateSessionAsync(cancellationToken).ConfigureAwait(false) && account.UserID > 0)
            {
                account.LastUse = DateTime.Now;
                AccountManager.LastValidAccount = account;
            }
            return account;
        }

        public Account(string Cookie, string? AccountJSON = null)
        {
            SecurityToken = Cookie;

            // 1. Try provided AccountJSON if valid
            if (!string.IsNullOrEmpty(AccountJSON) && Utilities.TryParseJson(AccountJSON, out AccountJson? data) && data != null && data.UserId > 0)
            {
                Username = data.Name;
                UserID = data.UserId;
                Valid = true;
                LastUse = DateTime.Now;
                AccountManager.LastValidAccount = this;
                return;
            }

            // 2. Query official users.roblox.com/v1/users/authenticated (fast, reliable, isolated headers)
            try
            {
                string? token = SecurityToken?.Trim().Trim('"').Trim();
                if (!string.IsNullOrEmpty(token))
                {
                    HttpClient httpClient = ProxyBindingService.GetClient(this);
                    using var req = new HttpRequestMessage(HttpMethod.Get, "https://users.roblox.com/v1/users/authenticated");
                    req.Headers.TryAddWithoutValidation("Cookie", $".ROBLOSECURITY={token}");
                    req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
                    req.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");

                    using var resp = httpClient.Send(req);
                    if (resp != null && resp.StatusCode == HttpStatusCode.OK)
                    {
                        TryUpdateRotatedCookie(resp);
                        using var streamReader = new System.IO.StreamReader(resp.Content.ReadAsStream());
                        string body = streamReader.ReadToEnd();
                        if (!string.IsNullOrWhiteSpace(body) && !body.StartsWith("<"))
                        {
                            if (Utilities.FastJson.TryDeserialize<RobloxAuthenticatedUser>(body, out var authUser) && authUser?.Id > 0)
                            {
                                UserID = authUser.Id;
                                Username = authUser.Name ?? string.Empty;
                                Valid = true;
                                LastUse = DateTime.Now;
                                AccountManager.LastValidAccount = this;
                                return;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Program.Logger.Warn($"[Account constructor] Failed to authenticate user: {ex.Message}");
            }

            Valid = false;
        }

        // Session state, cookie rotation, and request execution extracted to Account.SessionState.cs
        // GetCSRFTokenAsync and GetAuthTicketAsync extracted to Account.Api.cs
        // Account settings, PIN, blocklist, friend/avatar APIs extracted to Account.SettingsApi.cs
        // Process launching, window positioning, and native/browser home methods extracted to Account.Launcher.cs & Account.Launcher.Diagnostics.cs

        public string GetField(string Name) => Fields.TryGetValue(Name, out string? value) ? value : string.Empty;
        public void SetField(string Name, string Value) { Fields[Name] = Value; IsDirty = true; AccountManager.QueueDeferredSave(); }
        public void SetWindowBounds(int x, int y, int width, int height)
        {
            Fields["Window_Position_X"] = x.ToString(CultureInfo.InvariantCulture);
            Fields["Window_Position_Y"] = y.ToString(CultureInfo.InvariantCulture);
            Fields["Window_Width"] = width.ToString(CultureInfo.InvariantCulture);
            Fields["Window_Height"] = height.ToString(CultureInfo.InvariantCulture);
            IsDirty = true;
            AccountManager.QueueDeferredSave();
        }
        public void RemoveField(string Name) { if (Fields.Remove(Name)) { IsDirty = true; AccountManager.QueueDeferredSave(); } }
    }

    public class AccountJson
    {
        public long UserId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string UserEmail { get; set; } = string.Empty;
        public bool IsEmailVerified { get; set; }
        public int AgeBracket { get; set; }
        public bool UserAbove13 { get; set; }
    }
}

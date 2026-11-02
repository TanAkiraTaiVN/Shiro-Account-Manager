#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Classes;

namespace Shiro.Services
{
    public sealed class BloxGenAccountItem
    {
        public long UserId { get; init; }
        public string Username { get; init; } = string.Empty;
        public string Password { get; init; } = string.Empty;
        public string Cookie { get; init; } = string.Empty;
        public string Region { get; init; } = "RANDOM";
        public string GenType { get; init; } = "unverified";
        public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;
    }

    public sealed class BloxGenAccountResult
    {
        public bool Success { get; init; }
        public BloxGenAccountItem? Account { get; init; }
        public string? ErrorMessage { get; init; }
        public double? RemainingBalance { get; init; }
    }

    /// <summary>
    /// Service for automated Roblox alternate account generation via BloxGen API (bloxgen.net).
    /// Supports continent/region selection, auto-importing to Shiro Vault, and CSV history tracking.
    /// </summary>
    public sealed class BloxGenService
    {
        private static readonly Lazy<BloxGenService> _instance = new(() => new BloxGenService());
        public static BloxGenService Default => _instance.Value;

        private readonly HttpClient _httpClient;
        private const string DefaultBaseUrl = "https://bloxgen.net/api/";

        public BloxGenService(HttpClient? httpClient = null)
        {
            _httpClient = httpClient ?? new HttpClient
            {
                BaseAddress = new Uri(DefaultBaseUrl),
                Timeout = TimeSpan.FromSeconds(30)
            };
        }

        public string SavedApiKey
        {
            get
            {
                string raw = AccountManager.General?.Get<string>(AppSettingsKeys.General.BloxGenApiKey) ?? string.Empty;
                return CryptoHelper.UnprotectSecretString(raw);
            }
            set
            {
                string protectedVal = CryptoHelper.ProtectSecretString(value ?? string.Empty);
                AccountManager.General?.Set(AppSettingsKeys.General.BloxGenApiKey, protectedVal);
            }
        }

        public string DefaultContinent
        {
            get => AccountManager.General?.Get<string>(AppSettingsKeys.General.BloxGenContinent) ?? "RANDOM";
            set
            {
                AccountManager.General?.Set(AppSettingsKeys.General.BloxGenContinent, value ?? "RANDOM");
            }
        }

        public string DefaultGenType
        {
            get => AccountManager.General?.Get<string>(AppSettingsKeys.General.BloxGenGenType) ?? "unverified";
            set
            {
                AccountManager.General?.Set(AppSettingsKeys.General.BloxGenGenType, value ?? "unverified");
            }
        }

        /// <summary>
        /// Generates a new Roblox alt account using the BloxGen API.
        /// </summary>
        public async Task<BloxGenAccountResult> GenerateAccountAsync(
            string? apiKey = null,
            string? genType = null,
            string? continent = null,
            CancellationToken cancellationToken = default)
        {
            string key = !string.IsNullOrWhiteSpace(apiKey) ? apiKey.Trim() : SavedApiKey;
            if (string.IsNullOrWhiteSpace(key))
            {
                return new BloxGenAccountResult
                {
                    Success = false,
                    ErrorMessage = "Vui lòng nhập BloxGen API Key (BLOX-...)"
                };
            }

            string type = !string.IsNullOrWhiteSpace(genType) ? genType : DefaultGenType;
            string region = !string.IsNullOrWhiteSpace(continent) ? continent : DefaultContinent;

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "generate");
                request.Headers.Add("Authorization", $"Bearer {key}");
                request.Headers.Add("X-API-Key", key);

                var payload = new
                {
                    gen_type = type,
                    continent = region.Equals("RANDOM", StringComparison.OrdinalIgnoreCase) ? null : region
                };

                string jsonPayload = JsonSerializer.Serialize(payload);
                request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                string responseText = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    return new BloxGenAccountResult
                    {
                        Success = false,
                        ErrorMessage = $"BloxGen API Error ({(int)response.StatusCode}): {ExtractErrorMessage(responseText)}"
                    };
                }

                using var doc = JsonDocument.Parse(responseText);
                var root = doc.RootElement;

                bool success = root.TryGetProperty("success", out var succProp) ? succProp.GetBoolean() : true;
                if (!success)
                {
                    string err = root.TryGetProperty("message", out var msgProp) ? (msgProp.GetString() ?? "Lỗi không xác định") : "Yêu cầu không thành công";
                    return new BloxGenAccountResult { Success = false, ErrorMessage = err };
                }

                JsonElement dataElem = root.TryGetProperty("data", out var dProp) ? dProp : root;

                string? cookie = null;
                if (dataElem.TryGetProperty("cookie", out var cProp)) cookie = cProp.GetString();
                else if (dataElem.TryGetProperty(".ROBLOSECURITY", out var cProp2)) cookie = cProp2.GetString();

                if (string.IsNullOrWhiteSpace(cookie))
                {
                    return new BloxGenAccountResult
                    {
                        Success = false,
                        ErrorMessage = "Phản hồi từ BloxGen không chứa cookie hợp lệ."
                    };
                }

                string username = dataElem.TryGetProperty("username", out var uProp) ? (uProp.GetString() ?? string.Empty) : string.Empty;
                string password = dataElem.TryGetProperty("password", out var pProp) ? (pProp.GetString() ?? string.Empty) : string.Empty;
                long userId = 0;
                if (dataElem.TryGetProperty("user_id", out var uidProp) && uidProp.TryGetInt64(out long uidVal))
                    userId = uidVal;
                else if (dataElem.TryGetProperty("userId", out var uidProp2) && uidProp2.TryGetInt64(out long uidVal2))
                    userId = uidVal2;

                double? balance = null;
                if (root.TryGetProperty("remaining_balance", out var balProp) && balProp.TryGetDouble(out double balVal))
                    balance = balVal;

                var alt = new BloxGenAccountItem
                {
                    UserId = userId,
                    Username = username,
                    Password = password,
                    Cookie = cookie,
                    Region = region,
                    GenType = type,
                    CreatedAtUtc = DateTime.UtcNow
                };

                return new BloxGenAccountResult
                {
                    Success = true,
                    Account = alt,
                    RemainingBalance = balance
                };
            }
            catch (OperationCanceledException)
            {
                return new BloxGenAccountResult { Success = false, ErrorMessage = "Yêu cầu đã bị hủy." };
            }
            catch (Exception ex)
            {
                Program.Logger?.Error($"[BloxGenService] Generation failed: {ex.Message}");
                return new BloxGenAccountResult { Success = false, ErrorMessage = ex.Message };
            }
        }

        /// <summary>
        /// Automatically imports a generated BloxGen account into Shiro's secure vault and tracks in history.
        /// </summary>
        public bool ImportAccountToShiro(BloxGenAccountItem alt, string groupName = "AltGen")
        {
            if (alt == null || string.IsNullOrWhiteSpace(alt.Cookie))
                return false;

            try
            {
                string cleanCookie = alt.Cookie.Trim();
                if (!cleanCookie.StartsWith("_|WARNING:-DO-NOT-SHARE-THIS", StringComparison.OrdinalIgnoreCase))
                {
                    if (cleanCookie.Contains("_|WARNING"))
                    {
                        cleanCookie = cleanCookie.Substring(cleanCookie.IndexOf("_|WARNING", StringComparison.OrdinalIgnoreCase));
                    }
                }

                string uname = !string.IsNullOrWhiteSpace(alt.Username) ? alt.Username : $"Alt_{alt.UserId}";
                var account = new Account(cleanCookie)
                {
                    Username = uname,
                    UserID = alt.UserId,
                    Group = !string.IsNullOrWhiteSpace(groupName) ? groupName : "AltGen",
                    Description = $"BloxGen ({alt.Region} / {alt.GenType})",
                    Password = alt.Password
                };

                AccountManager.AddAccountThreadSafe(account);
                AccountManager.SaveAccounts(true, true);
                Shiro.AvaloniaUI.Views.MainWindow.Instance?.RefreshAccounts(AccountManager.GetAccountsSnapshot());

                AppendToHistoryCsv(alt);
                Program.Logger?.Info($"[BloxGenService] Successfully imported alt account {uname} (ID: {alt.UserId}) to group '{groupName}'.");
                return true;
            }
            catch (Exception ex)
            {
                Program.Logger?.Error($"[BloxGenService] Failed to import generated account: {ex.Message}");
                return false;
            }
        }

        private static string SanitizeCsvField(string? value)
        {
            if (string.IsNullOrEmpty(value)) return "\"\"";
            string sanitized = value.Replace("\"", "\"\"");
            if (sanitized.StartsWith('=') || sanitized.StartsWith('+') || sanitized.StartsWith('-') || sanitized.StartsWith('@') || sanitized.StartsWith('\t') || sanitized.StartsWith('\r'))
            {
                sanitized = "'" + sanitized;
            }
            return $"\"{sanitized}\"";
        }

        private static void AppendToHistoryCsv(BloxGenAccountItem alt)
        {
            try
            {
                string csvPath = Path.Combine(StoragePaths.DataDirectory, "altgen_history.csv");
                bool fileExists = File.Exists(csvPath);

                using var sw = new StreamWriter(csvPath, append: true, Encoding.UTF8);
                if (!fileExists)
                {
                    sw.WriteLine("TimestampUtc,UserId,Username,Region,GenType,Status");
                }
                sw.WriteLine($"{alt.CreatedAtUtc:yyyy-MM-dd HH:mm:ss},{alt.UserId},{SanitizeCsvField(alt.Username)},{SanitizeCsvField(alt.Region)},{SanitizeCsvField(alt.GenType)},\"[VAULT_SECURED]\"");
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"[BloxGenService] Append CSV history failed: {ex.Message}");
            }
        }

        private static string ExtractErrorMessage(string responseText)
        {
            if (string.IsNullOrWhiteSpace(responseText)) return "Empty response";
            try
            {
                using var doc = JsonDocument.Parse(responseText);
                if (doc.RootElement.TryGetProperty("message", out var mProp))
                    return mProp.GetString() ?? responseText;
                if (doc.RootElement.TryGetProperty("error", out var eProp))
                    return eProp.GetString() ?? responseText;
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"[BloxGenService.ExtractErrorMessage] {ex.Message}");
            }
            return responseText.Length > 100 ? responseText.Substring(0, 100) + "..." : responseText;
        }
    }
}

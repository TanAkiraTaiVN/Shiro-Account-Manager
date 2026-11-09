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
    public sealed class BloxGenService : IBloxGenService
    {
        private static readonly Lazy<BloxGenService> _instance = new(() => new BloxGenService(null, AppServices.Logger, AppServices.GeneralSettings));
        public static BloxGenService Default => _instance.Value;

        private static IAppLogger _staticLogger = AppServices.Logger;
        public static IAppLogger Logger
        {
            get => _staticLogger;
            set => _staticLogger = value ?? throw new ArgumentNullException(nameof(value));
        }

        private readonly HttpClient _httpClient;
        private readonly IAppLogger _logger;
        private readonly ISettingsService _settings;
        private const string DefaultBaseUrl = "https://core.bloxgen.net/api/";

        public BloxGenService(HttpClient? httpClient = null, IAppLogger? logger = null, ISettingsService? settings = null)
        {
            _httpClient = httpClient ?? new HttpClient
            {
                BaseAddress = new Uri(DefaultBaseUrl),
                Timeout = TimeSpan.FromSeconds(30)
            };
            _logger = logger ?? AppServices.Logger;
            _settings = settings ?? AppServices.GeneralSettings;
        }

        public string SavedApiKey
        {
            get
            {
                string raw = _settings.Get<string>(AppSettingsKeys.General.BloxGenApiKey) ?? string.Empty;
                return CryptoHelper.UnprotectSecretString(raw);
            }
            set
            {
                string protectedVal = CryptoHelper.ProtectSecretString(value ?? string.Empty);
                _settings.Set(AppSettingsKeys.General.BloxGenApiKey, protectedVal);
            }
        }

        public string DefaultContinent
        {
            get => _settings.Get<string>(AppSettingsKeys.General.BloxGenContinent) ?? "RANDOM";
            set
            {
                _settings.Set(AppSettingsKeys.General.BloxGenContinent, value ?? "RANDOM");
            }
        }

        public string DefaultGenType
        {
            get => _settings.Get<string>(AppSettingsKeys.General.BloxGenGenType) ?? "unverified";
            set
            {
                _settings.Set(AppSettingsKeys.General.BloxGenGenType, value ?? "unverified");
            }
        }

        internal static string NormalizeGenType(string? genType)
        {
            if (string.IsNullOrWhiteSpace(genType))
                return "alt";

            return genType.Trim().ToLowerInvariant() switch
            {
                "unverified" or "alt" => "alt",
                "verified" or "18+ age verified" => "18+ age verified",
                "+30 days old" or "30+ days old" => "+30 days old",
                "+1 year old" or "1+ year old" => "+1 year old",
                "5+ years old" or "+5 years old" => "5+ years old",
                "dump" => "dump",
                _ => genType.Trim()
            };
        }

        internal static string? NormalizeContinentSlug(string? continent)
        {
            if (string.IsNullOrWhiteSpace(continent))
                return null;

            string trimmed = continent.Trim();
            if (trimmed.Equals("RANDOM", StringComparison.OrdinalIgnoreCase))
                return null;

            return trimmed.ToUpperInvariant() switch
            {
                "AS" or "ASIA" => "asia",
                "NA" or "NORTH-AMERICA" or "NORTH AMERICA" => "north-america",
                "EU" or "EUROPE" => "europe",
                "OC" or "OCEANIA" => "oceania",
                "SA" or "SOUTH-AMERICA" or "SOUTH AMERICA" => "south-america",
                "AF" or "AFRICA" => "africa",
                "AN" or "ANTARCTICA" => "antarctica",
                _ => trimmed.ToLowerInvariant()
            };
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
                    ErrorMessage = Shiro.Services.LocalizationService.Get("BloxGen.EnterApiKey")
                };
            }

            string rawType = !string.IsNullOrWhiteSpace(genType) ? genType : DefaultGenType;
            string apiType = NormalizeGenType(rawType);
            string region = !string.IsNullOrWhiteSpace(continent) ? continent : DefaultContinent;
            string? continentSlug = NormalizeContinentSlug(region);

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "generate");
                request.Headers.Add("Authorization", $"Bearer {key}");
                request.Headers.Add("X-API-Key", key);

                string jsonPayload = JsonSerializer.Serialize(
                    new BloxGenGenerateRequest(key, apiType, continentSlug),
                    ShiroAppJson.Default.BloxGenGenerateRequest);
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
                    string err = root.TryGetProperty("message", out var msgProp) ? (msgProp.GetString() ?? Shiro.Services.LocalizationService.Get("AltGen.UnknownError")) : Shiro.Services.LocalizationService.Get("BloxGen.RequestFailed");
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
                        ErrorMessage = Shiro.Services.LocalizationService.Get("BloxGen.MissingCookieResponse")
                    };
                }

                string username = dataElem.TryGetProperty("username", out var uProp) ? (uProp.GetString() ?? string.Empty) : string.Empty;
                string password = dataElem.TryGetProperty("password", out var pProp) ? (pProp.GetString() ?? string.Empty) : string.Empty;
                long userId = 0;
                if (dataElem.TryGetProperty("id", out var idProp) && idProp.TryGetInt64(out long idVal))
                    userId = idVal;
                else if (dataElem.TryGetProperty("user_id", out var uidProp) && uidProp.TryGetInt64(out long uidVal))
                    userId = uidVal;
                else if (dataElem.TryGetProperty("userId", out var uidProp2) && uidProp2.TryGetInt64(out long uidVal2))
                    userId = uidVal2;

                string resolvedRegion = region;
                if (dataElem.TryGetProperty("region", out var regProp) && regProp.ValueKind == JsonValueKind.String)
                {
                    string? apiRegion = regProp.GetString();
                    if (!string.IsNullOrWhiteSpace(apiRegion))
                        resolvedRegion = apiRegion;
                }

                double? balance = null;
                if (root.TryGetProperty("remaining_balance", out var balProp) && balProp.TryGetDouble(out double balVal))
                    balance = balVal;

                var alt = new BloxGenAccountItem
                {
                    UserId = userId,
                    Username = username,
                    Password = password,
                    Cookie = cookie,
                    Region = resolvedRegion,
                    GenType = rawType,
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
                return new BloxGenAccountResult { Success = false, ErrorMessage = Shiro.Services.LocalizationService.Get("BloxGen.RequestCancelled") };
            }
            catch (Exception ex)
            {
                _logger.Error($"[BloxGenService] Generation failed: {ex.Message}");
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
                string? accountJson = alt.UserId > 0
                    ? $"{{\"id\":{alt.UserId},\"name\":{JsonSerializer.Serialize(uname, ShiroAppJson.Default.String)},\"displayName\":{JsonSerializer.Serialize(uname, ShiroAppJson.Default.String)}}}"
                    : null;

                var account = new Account(cleanCookie, accountJson)
                {
                    Username = uname,
                    Group = !string.IsNullOrWhiteSpace(groupName) ? groupName : "AltGen",
                    Description = $"BloxGen ({alt.Region} / {alt.GenType})",
                    Password = alt.Password,
                    Valid = true
                };
                if (alt.UserId > 0)
                {
                    account.UserID = alt.UserId;
                }

                AppServices.AccountRepository.AddThreadSafe(account);
                AppServices.AccountRepository.Save(true);
                AppServices.HostUiBridge.RefreshAccountView();

                AppendToHistoryCsv(alt);
                _logger.Info($"[BloxGenService] Successfully imported alt account {uname} (ID: {alt.UserId}) to group '{groupName}'.");
                return true;
            }
            catch (Exception ex)
            {
                _logger.Error($"[BloxGenService] Failed to import generated account: {ex.Message}");
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
                _staticLogger.Debug($"[BloxGenService] Append CSV history failed: {ex.Message}");
            }
        }

        internal static string ExtractErrorMessage(string responseText)
        {
            if (string.IsNullOrWhiteSpace(responseText)) return "Empty response";
            string trimmed = responseText.Trim();
            if (trimmed.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("<html", StringComparison.OrdinalIgnoreCase))
            {
                return "Endpoint not found or service temporarily unavailable (HTML response).";
            }
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                if (doc.RootElement.TryGetProperty("message", out var mProp) && mProp.ValueKind == JsonValueKind.String)
                    return mProp.GetString() ?? trimmed;
                if (doc.RootElement.TryGetProperty("error", out var eProp) && eProp.ValueKind == JsonValueKind.String)
                    return eProp.GetString() ?? trimmed;
            }
            catch (Exception ex)
            {
                _staticLogger.Debug($"[BloxGenService.ExtractErrorMessage] {ex.Message}");
            }
            return trimmed.Length > 100 ? trimmed.Substring(0, 100) + "..." : trimmed;
        }
    }
}

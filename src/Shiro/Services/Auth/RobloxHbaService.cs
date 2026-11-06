#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Shiro.Services
{
    /// <summary>
    /// Cryptographic Hardware-Backed Authentication (HBA / BAT) engine for Roblox sessions.
    /// Captures ECDSA P-256 session-binding keys during browser sign-in and signs outgoing C#
    /// API/ticket requests with <c>x-bound-auth-token</c> so sessions survive VPN/IP switches.
    /// </summary>
    public static partial class RobloxHbaService
    {
        private static IAppLogger _logger = AppServices.Logger;
        public static IAppLogger Logger
        {
            get => _logger;
            set => _logger = value ?? throw new ArgumentNullException(nameof(value));
        }

        public const string BoundAuthHeaderName = "x-bound-auth-token";
        public const string DefaultTemplate = "v1|b64|HB,TS,URL,M|,TS,PATH,M";

        private static readonly System.Threading.Lock KeyCacheLock = new();
        private static readonly ConcurrentDictionary<string, ECParameters> CachedEcParameters = new(StringComparer.Ordinal);
        private static int _activeInteractiveLoginCount;

        public static void ClearKeyCache()
        {
            lock (KeyCacheLock)
            {
                foreach (var kvp in CachedEcParameters)
                {
                    if (kvp.Value.D != null)
                        CryptographicOperations.ZeroMemory(kvp.Value.D);
                }
                CachedEcParameters.Clear();
            }
        }

        public static bool IsInteractiveLoginActive => Volatile.Read(ref _activeInteractiveLoginCount) > 0;

        public static IDisposable BeginInteractiveLoginScope()
        {
            Interlocked.Increment(ref _activeInteractiveLoginCount);
            return new InteractiveLoginScope();
        }

        private sealed class InteractiveLoginScope : IDisposable
        {
            private int _disposed;
            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    int val = Interlocked.Decrement(ref _activeInteractiveLoginCount);
                    if (val < 0)
                        Interlocked.Exchange(ref _activeInteractiveLoginCount, 0);
                }
            }
        }

        public sealed class CaptureSnapshot
        {
            public string? PrivateKeyPkcs8Base64 { get; set; }
            public string? PublicKeySpkiBase64 { get; set; }
            public string? TokenTemplate { get; set; }
            public string? UserAgent { get; set; }
            public string? BrowserTrackerId { get; set; }

            public bool HasPrivateKey => !string.IsNullOrWhiteSpace(PrivateKeyPkcs8Base64);
        }

        [GeneratedRegex(@"(?:^|[&\s;])browserid=(\d+)", RegexOptions.IgnoreCase)]
        private static partial Regex BrowserIdRegex();

        public static string? ExtractBrowserTrackerIdFromCookie(string? cookieValue)
        {
            if (string.IsNullOrWhiteSpace(cookieValue))
                return null;

            Match match = BrowserIdRegex().Match(cookieValue);
            return match.Success && !string.IsNullOrWhiteSpace(match.Groups[1].Value)
                ? match.Groups[1].Value
                : null;
        }

        public static bool IsValidPkcs8EcdsaKey(string? base64Pkcs8)
        {
            if (string.IsNullOrWhiteSpace(base64Pkcs8))
                return false;

            try
            {
                byte[] keyBytes = Convert.FromBase64String(base64Pkcs8.Trim());
                try
                {
                    using ECDsa ecdsa = ECDsa.Create();
                    ecdsa.ImportPkcs8PrivateKey(keyBytes, out _);
                    return true;
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(keyBytes);
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Signs an HTTP request using the account's captured ECDSA P-256 HBA private key,
        /// producing a valid <c>x-bound-auth-token</c> header matching Roblox's Account Session Protection spec.
        /// </summary>
        public static bool TryBuildBoundAuthToken(
            string? privateKeyPkcs8Base64,
            string? requestUrl,
            string? httpMethod,
            string? bodyContent,
            string? tokenTemplate,
            long? unixTimestampOverride,
            out string boundAuthToken)
        {
            boundAuthToken = string.Empty;
            if (string.IsNullOrWhiteSpace(privateKeyPkcs8Base64) || string.IsNullOrWhiteSpace(requestUrl))
                return false;

            try
            {
                string trimmedKey = privateKeyPkcs8Base64.Trim();
                using ECDsa ecdsa = AcquireCachedEcdsa(trimmedKey);

                string template = string.IsNullOrWhiteSpace(tokenTemplate) ? DefaultTemplate : tokenTemplate.Trim();
                string[] tplParts = template.Split('|');
                if (tplParts.Length < 3)
                {
                    tplParts = DefaultTemplate.Split('|');
                }

                string version = string.IsNullOrWhiteSpace(tplParts[0]) ? "v1" : tplParts[0];
                bool useHexHash = string.Equals(tplParts[1], "hex", StringComparison.OrdinalIgnoreCase);

                byte[] bodyBytes = string.IsNullOrEmpty(bodyContent)
                    ? Array.Empty<byte>()
                    : Encoding.UTF8.GetBytes(bodyContent);
                byte[] sha256Bytes = SHA256.HashData(bodyBytes);
                string hashedBody = useHexHash
                    ? Convert.ToHexStringLower(sha256Bytes)
                    : Convert.ToBase64String(sha256Bytes);

                long ts = unixTimestampOverride ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                string timestampStr = ts.ToString(CultureInfo.InvariantCulture);
                string methodUpper = string.IsNullOrWhiteSpace(httpMethod) ? "GET" : httpMethod.Trim().ToUpperInvariant();
                string fullUrl = requestUrl.Trim();
                string pathAndQuery = Uri.TryCreate(fullUrl, UriKind.Absolute, out Uri? parsedUri) && parsedUri != null
                    ? parsedUri.PathAndQuery
                    : fullUrl;

                var outputSegments = new List<string>(tplParts.Length + 1)
                {
                    version,
                    hashedBody,
                    timestampStr
                };

                for (int i = 2; i < tplParts.Length; i++)
                {
                    string sigSpec = tplParts[i];
                    string payloadToSign = BuildPayloadFromSpec(sigSpec, hashedBody, timestampStr, fullUrl, pathAndQuery, methodUpper);
                    byte[] sigBytes = ecdsa.SignData(
                        Encoding.UTF8.GetBytes(payloadToSign),
                        HashAlgorithmName.SHA256,
                        DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
                    outputSegments.Add(Convert.ToBase64String(sigBytes));
                }

                boundAuthToken = string.Join("|", outputSegments);
                return true;
            }
            catch (Exception ex)
            {
                _logger.Debug($"[RobloxHbaService] Failed to sign x-bound-auth-token: {ex.Message}");
                return false;
            }
        }

        private static ECDsa AcquireCachedEcdsa(string trimmedKey)
        {
            lock (KeyCacheLock)
            {
                if (!CachedEcParameters.TryGetValue(trimmedKey, out ECParameters ecParams))
                {
                    byte[] keyBytes = Convert.FromBase64String(trimmedKey);
                    try
                    {
                        using ECDsa parserEcdsa = ECDsa.Create();
                        parserEcdsa.ImportPkcs8PrivateKey(keyBytes, out _);
                        ecParams = parserEcdsa.ExportParameters(includePrivateParameters: true);
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(keyBytes);
                    }

                    if (CachedEcParameters.Count > 512)
                        ClearKeyCache();

                    CachedEcParameters[trimmedKey] = ecParams;
                }

                return ECDsa.Create(ecParams);
            }
        }

        private static string BuildPayloadFromSpec(
            string sigSpec,
            string hashedBody,
            string timestampStr,
            string fullUrl,
            string pathAndQuery,
            string methodUpper)
        {
            string[] tokens = sigSpec.Split(',');
            var values = new string[tokens.Length];
            for (int i = 0; i < tokens.Length; i++)
            {
                values[i] = tokens[i].Trim() switch
                {
                    "HB" => hashedBody,
                    "TS" => timestampStr,
                    "URL" => fullUrl,
                    "PATH" => pathAndQuery,
                    "M" => methodUpper,
                    _ => string.Empty
                };
            }
            return string.Join("|", values);
        }
    }
}

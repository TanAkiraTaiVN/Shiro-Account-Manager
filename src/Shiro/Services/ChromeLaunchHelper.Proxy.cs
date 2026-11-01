#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PuppeteerSharp;
using Shiro.Classes;
using Shiro.Http;

namespace Shiro.Services
{
    public sealed class VerifiedBrowserProxy
    {
        public WebProxy? WebProxy { get; init; }
        public string BrowserServerArgument { get; init; } = string.Empty;
        public string RawAuthority { get; init; } = string.Empty;
        public bool IsSocks { get; init; }
        public bool AuthenticateInBrowser { get; init; }
        public string? Username { get; init; }
        public string? Password { get; init; }
    }

    public static partial class ChromeLaunchHelper
    {
        public static async Task HandleCdpProxyAuthChallengeAsync(
            ICDPSession session,
            MessageEventArgs e,
            string proxyUsername,
            string proxyPassword,
            bool authenticateProxy = true)
        {
            if (session == null || e == null)
                return;

            try
            {
                JsonElement message = e.MessageData;
                if (message.ValueKind != JsonValueKind.Object)
                    return;

                if (!message.TryGetProperty("method", out var methodProp) || methodProp.GetString() != "Fetch.authRequired")
                    return;

                if (!message.TryGetProperty("params", out var parameters))
                    return;

                string? requestId = parameters.TryGetProperty("requestId", out var reqProp) ? reqProp.GetString() : null;
                string? challengeSource = parameters.TryGetProperty("authChallenge", out var authChal) && authChal.TryGetProperty("source", out var srcProp)
                    ? srcProp.GetString()
                    : null;

                if (string.IsNullOrEmpty(requestId))
                    return;

                if (authenticateProxy && challengeSource == "Proxy" && !string.IsNullOrEmpty(proxyUsername))
                {
                    await session.SendAsync("Fetch.continueWithAuth", new
                    {
                        requestId,
                        authChallengeResponse = new
                        {
                            response = "ProvideCredentials",
                            username = proxyUsername,
                            password = proxyPassword ?? string.Empty
                        }
                    }).ConfigureAwait(false);
                }
                else
                {
                    await session.SendAsync("Fetch.continueWithAuth", new
                    {
                        requestId,
                        authChallengeResponse = new { response = "Default" }
                    }).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Program.Logger?.Warn($"Unable to answer a browser authentication challenge: {ex.Message}");
            }
        }

        public static async Task<VerifiedBrowserProxy?> SelectVerifiedBrowserProxyAsync(
            string[]? additionalProbeUrls = null,
            CancellationToken cancellationToken = default)
        {
            if (AccountManager.General == null || !AccountManager.General.Get<bool>("UseProxies"))
                return null;

            string proxiesPath = StoragePaths.GetUserFile("proxies.txt");
            if (!File.Exists(proxiesPath))
                return null;

            Random rng = new Random(unchecked(Environment.TickCount ^ Guid.NewGuid().GetHashCode()));
            int timeout = AccountManager.General.Exists("ProxyTimeout") ? AccountManager.General.Get<int>("ProxyTimeout") : 3000;
            int limit = AccountManager.General.Exists("ProxyTestLimit") ? AccountManager.General.Get<int>("ProxyTestLimit") : 10;
            string[] proxies = File.ReadAllLines(proxiesPath)
                .Select(line => line.Trim())
                .Where(line => !string.IsNullOrWhiteSpace(line) && !line.StartsWith("#"))
                .OrderBy(_ => rng.Next())
                .Take(Math.Max(limit, 0))
                .ToArray();

            foreach (string entry in proxies)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ProxyItem? parsed = ProxyManagerService.ParseProxy(entry);
                if (parsed == null)
                {
                    Program.Logger?.Warn($"Skipping invalid proxy entry: {LogRedactor.Redact(entry)}");
                    continue;
                }

                string browserScheme;
                if (parsed.Protocol.Equals("socks5", StringComparison.OrdinalIgnoreCase))
                    browserScheme = "socks5";
                else if (parsed.Protocol.Equals("socks4", StringComparison.OrdinalIgnoreCase))
                    browserScheme = "socks4";
                else if (parsed.Protocol.Equals("http", StringComparison.OrdinalIgnoreCase) || parsed.Protocol.Equals("https", StringComparison.OrdinalIgnoreCase))
                    browserScheme = "http";
                else
                {
                    Program.Logger?.Warn($"Skipping proxy entry with unsupported protocol: {parsed.Protocol}");
                    continue;
                }

                string username = parsed.Username ?? string.Empty;
                string password = parsed.Password ?? string.Empty;

                if (!Uri.TryCreate($"{parsed.Protocol}://{parsed.Host}:{parsed.Port}", UriKind.Absolute, out Uri? proxyUri) ||
                    string.IsNullOrWhiteSpace(proxyUri.Host) || proxyUri.Port < 1)
                {
                    Program.Logger?.Warn("Skipping a proxy entry with an invalid address.");
                    continue;
                }

                try
                {
                    var webProxy = new WebProxy(proxyUri);
                    if (!string.IsNullOrEmpty(username))
                        webProxy.Credentials = new NetworkCredential(username, password);

                    bool probeReachable = true;
                    using (HttpClient client = SharedHttpClient.CreateProxyClient(
                        webProxy,
                        TimeSpan.FromMilliseconds(Math.Max(250, timeout))))
                    {
                        using (HttpResponseMessage response = await client.GetAsync("https://www.roblox.com/", cancellationToken).ConfigureAwait(false))
                        {
                            if (response.StatusCode == HttpStatusCode.ProxyAuthenticationRequired)
                                continue;
                        }

                        if (additionalProbeUrls != null)
                        {
                            foreach (string url in additionalProbeUrls)
                            {
                                using (HttpResponseMessage response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
                                {
                                    if (response.StatusCode == HttpStatusCode.ProxyAuthenticationRequired)
                                    {
                                        probeReachable = false;
                                        break;
                                    }
                                }
                            }
                        }
                    }

                    if (!probeReachable)
                    {
                        Program.Logger?.Warn("A configured proxy could reach Roblox but not its verification services; trying another proxy.");
                        continue;
                    }

                    bool isSocks = browserScheme.StartsWith("socks", StringComparison.OrdinalIgnoreCase);
                    bool canAuthenticate = !isSocks && !string.IsNullOrEmpty(username);
                    Program.Logger?.Info("A configured proxy passed the Roblox connectivity check.");
                    return new VerifiedBrowserProxy
                    {
                        WebProxy = webProxy,
                        BrowserServerArgument = browserScheme + "://" + proxyUri.Authority,
                        RawAuthority = (isSocks ? $"{browserScheme}://" : "") + proxyUri.Authority,
                        IsSocks = isSocks,
                        AuthenticateInBrowser = canAuthenticate,
                        Username = canAuthenticate ? username : null,
                        Password = canAuthenticate ? password : null
                    };
                }
                catch (Exception ex)
                {
                    Program.Logger?.Debug($"Proxy candidate test threw: {ex.Message}");
                }
            }

            if (proxies.Length > 0)
            {
                Program.Logger?.Error("No configured proxy passed the Roblox connectivity check. Fail-Closed enforced to prevent IP leak.");
                throw new InvalidOperationException("None of the configured proxies passed the connectivity check. Browser launch blocked to prevent IP leak.");
            }

            return null;
        }
    }
}

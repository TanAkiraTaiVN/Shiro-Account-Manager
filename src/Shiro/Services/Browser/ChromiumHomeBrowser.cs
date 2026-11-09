#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Services;

namespace Shiro.Services
{
    /// <summary>
    /// Opens an authenticated Roblox Home page through the browser's Chrome DevTools
    /// Protocol. Each account gets a private temporary profile and a session-only
    /// .ROBLOSECURITY cookie, so the user's normal Chrome profile is never touched.
    ///
    /// This path is deliberately separate from AccountBrowser: the latter is still
    /// used for the interactive login and custom browser tools, while Home can start
    /// quickly without creating a Puppeteer connection or requesting a Roblox auth
    /// ticket.
    /// </summary>
    internal static partial class ChromiumHomeBrowser
    {
        private const string HomeUrl = "https://www.roblox.com/home";
        private const int StartupTimeoutSeconds = 15;
        private const int NavigationTimeoutSeconds = 30;
        private const int ProbeIntervalMilliseconds = 1500;
        // Reuse the process-wide short-lived CDP client.  Creating a new
        // HttpClient for every account made multi-Home launch pay repeated
        // handler/socket setup costs and could leave loopback connections in
        // TIME_WAIT during a fast batch.
        private static HttpClient DevToolsHttp => SharedHttpClient.Get("cdp-home-monitor");

        private static string SessionRoot => StoragePaths.BrowserSessionsDirectory;

        private sealed class BrowserTarget
        {
            public string WebSocketUrl = string.Empty;
            public string BrowserName = string.Empty;
        }

        /// <summary>Starts one isolated Chromium Home window and returns when navigation was accepted.</summary>
        public static async Task<bool> OpenAsync(Account? account, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (account == null || string.IsNullOrWhiteSpace(account.SecurityToken))
                return false;

            string? executable = FindExecutable();
            if (string.IsNullOrWhiteSpace(executable))
            {
                AppServices.Logger?.Warn("No Chrome or Edge executable was found for the Roblox Home browser.");
                return false;
            }

            string profileDir = Path.Combine(SessionRoot,
                (account.UserID > 0 ? account.UserID.ToString() : "account") + "-" + Guid.NewGuid().ToString("N"));
            int port = GetFreePort();
            Process? process = null;
            bool cleanupStarted = false;

            try
            {
                Directory.CreateDirectory(profileDir);
                Services.BrowserService.SuppressPasswordManager(profileDir);
                List<string> arguments = BuildArguments(profileDir, port);

                process = StartBrowser(executable, arguments);
                if (process == null)
                    throw new InvalidOperationException("Chrome did not return a process handle.");

                BrowserTarget? target = await WaitForPageTargetAsync(port,
                    TimeSpan.FromSeconds(StartupTimeoutSeconds), cancellationToken).ConfigureAwait(false);
                if (target == null)
                    throw new TimeoutException("The browser DevTools endpoint did not become ready.");

                // The browser process returned by Process.Start can be only a short-lived
                // launcher. Liveness is therefore monitored through the DevTools port.
                cleanupStarted = true;
                AccountBrowser.Observe(CleanupWhenClosedAsync(port, process, profileDir));

                await InjectCookieAndNavigateAsync(target.WebSocketUrl, account.SecurityToken,
                    cancellationToken).ConfigureAwait(false);

                AppServices.Logger?.Info($"Opened Roblox Home for {account.Username} in {Path.GetFileName(executable)} using an isolated Chromium profile.");
                return true;
            }
            catch (OperationCanceledException)
            {
                AppServices.Logger?.Warn($"Opening Roblox Home for {account.Username} was cancelled.");
                if (cleanupStarted && process != null) await CloseBrowserAsync(port, process).ConfigureAwait(false);
                return false;
            }
            catch (Exception exception)
            {
                AppServices.Logger?.Error($"Failed to open Roblox Home in Chromium for {account.Username}.", exception);
                if (cleanupStarted && process != null) await CloseBrowserAsync(port, process).ConfigureAwait(false);
                return false;
            }
            finally
            {
                if (!cleanupStarted)
                {
                    if (process != null)
                    {
                        await CloseBrowserAsync(port, process).ConfigureAwait(false);
                        SafeExecuteHelper.Try(process.Dispose);
                    }
                    await WipeProfileWithRetryAsync(profileDir).ConfigureAwait(false);
                }
            }
        }

        private static string? FindExecutable()
        {
            string? installed = BrowserService.FindInstalledBrowserExecutable();
            if (!string.IsNullOrWhiteSpace(installed) && File.Exists(installed))
                return installed;

            try
            {
                return AccountBrowser.FindSystemBrowserExecutables().FirstOrDefault(File.Exists);
            }
            catch (Exception ex)
            {
                AppServices.Logger?.Debug($"FindSystemBrowserExecutables failed: {ex.Message}");
                return null;
            }
        }

        private static List<string> BuildArguments(string profileDir, int port)
        {
            var workArea = RobloxWindowService.GetTargetWorkArea();
            int width = Math.Min(1280, Math.Max(960, workArea.Width - 80));
            int height = Math.Min(860, Math.Max(700, workArea.Height - 80));
            int left = workArea.Left + Math.Max(0, (workArea.Width - width) / 2);
            int top = workArea.Top + Math.Max(0, (workArea.Height - height) / 2);

            return new List<string>
            {
                "--new-window",
                $"--user-data-dir={profileDir}",
                $"--remote-debugging-port={port}",
                "--remote-debugging-address=127.0.0.1",
                "--no-first-run",
                "--no-default-browser-check",
                "--disable-save-password-bubble",
                "--disable-single-click-autofill",
                "--disable-autofill-keyboard-accessory-view",
                "--disable-search-engine-choice-screen",
                "--disable-sync",
                "--password-store=basic",
                "--disable-features=AutofillServerCommunication,PasswordManager,SavePasswordBubble,PasswordLeakDetection,msEdgeSidebarV2,msEdgeShopping,msImplicitSignin",
                $"--window-size={width},{height}",
                $"--window-position={left},{top}",
                "about:blank"
            };
        }

        private static Process? StartBrowser(string executable, List<string> arguments)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(executable) ?? Environment.CurrentDirectory
            };

            foreach (string argument in arguments)
                startInfo.ArgumentList.Add(argument);

            return Process.Start(startInfo);
        }

        private static int GetFreePort() => ChromeLaunchHelper.GetFreeLoopbackPort();

        private static async Task<BrowserTarget?> WaitForPageTargetAsync(int port, TimeSpan timeout, CancellationToken cancellationToken)
        {
            HttpClient http = DevToolsHttp;
            // Reuse one short-timeout localhost client for all CDP probes;
            // creating a new handler per account caused avoidable socket
            // and TLS setup work when opening several Home tabs.
            long startTimestamp = Stopwatch.GetTimestamp();
            while (Stopwatch.GetElapsedTime(startTimestamp) < timeout)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    string json = await http.GetStringAsync($"http://127.0.0.1:{port}/json", cancellationToken).ConfigureAwait(false);
                    using (JsonDocument document = JsonDocument.Parse(json))
                    {
                        foreach (JsonElement target in document.RootElement.EnumerateArray())
                        {
                            string? type = target.TryGetProperty("type", out JsonElement typeElement)
                                ? typeElement.GetString() : null;
                            string? socket = target.TryGetProperty("webSocketDebuggerUrl", out JsonElement socketElement)
                                ? socketElement.GetString() : null;

                            if (type == "page" && !string.IsNullOrEmpty(socket) && IsOwnDebuggerUrl(socket, port, "page"))
                            {
                                return new BrowserTarget
                                {
                                    WebSocketUrl = socket,
                                    BrowserName = target.TryGetProperty("title", out JsonElement title)
                                        ? (title.GetString() ?? string.Empty) : string.Empty
                                };
                            }
                        }
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { /* Probe timeout */ }
                catch (HttpRequestException) { /* Endpoint not ready */ }
                catch (IOException) { /* Socket transient error */ }
                catch (JsonException) { /* Partial JSON payload */ }

                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }
            return null;
        }

        private static async Task<string?> WaitForBrowserSocketAsync(int port, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            HttpClient http = DevToolsHttp;
            long startTimestamp = Stopwatch.GetTimestamp();
            while (Stopwatch.GetElapsedTime(startTimestamp) < timeout && !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    string json = await http.GetStringAsync($"http://127.0.0.1:{port}/json/version", cancellationToken).ConfigureAwait(false);
                    using (JsonDocument document = JsonDocument.Parse(json))
                    {
                        if (document.RootElement.TryGetProperty("webSocketDebuggerUrl", out JsonElement socket))
                        {
                            string? value = socket.GetString();
                            if (!string.IsNullOrEmpty(value) && IsOwnDebuggerUrl(value, port, "browser")) return value;
                        }
                    }
                }
                catch (Exception ex)
                {
                    AppServices.Logger?.Debug($"Parse webSocketDebuggerUrl JSON failed: {ex.Message}");
                }

                try
                {
                    await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            return null;
        }

        private static bool IsOwnDebuggerUrl(string? url, int port, string kind)
        {
            if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri == null) return false;
            return uri.Scheme == "ws" && uri.Host == "127.0.0.1" && uri.Port == port &&
                string.IsNullOrEmpty(uri.UserInfo) && uri.AbsolutePath.StartsWith("/devtools/" + kind + "/", StringComparison.Ordinal);
        }

        private static async Task InjectCookieAndNavigateAsync(string webSocketUrl, string cookie, CancellationToken cancellationToken)
        {
            using (ClientWebSocket socket = new ClientWebSocket())
            {
                await socket.ConnectAsync(new Uri(webSocketUrl), cancellationToken).ConfigureAwait(false);

                using (CancellationTokenSource commandTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    commandTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                    await CallAsync(socket, 1, "Network.enable", new { }, commandTimeout.Token, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                }

                using (CancellationTokenSource commandTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    commandTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                    await CallAsync(socket, 2, "Network.setCookie", new
                    {
                        name = ".ROBLOSECURITY",
                        value = cookie,
                        domain = ".roblox.com",
                        path = "/",
                        secure = true,
                        httpOnly = true
                    }, commandTimeout.Token, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                }

                using (CancellationTokenSource commandTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    commandTimeout.CancelAfter(TimeSpan.FromSeconds(NavigationTimeoutSeconds));
                    await CallAsync(socket, 3, "Page.navigate", new { url = HomeUrl }, commandTimeout.Token,
                        TimeSpan.FromSeconds(NavigationTimeoutSeconds)).ConfigureAwait(false);
                }

                try
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "ready", CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    AppServices.Logger?.Debug($"Closing home browser websocket threw: {ex.Message}");
                }
            }
        }

        private static async Task CallAsync(ClientWebSocket socket, int id, string method,
            object parameters, CancellationToken cancellationToken, TimeSpan timeout)
        {
            string payload = JsonSerializer.Serialize(new { id, method, @params = parameters });
            byte[] bytes = Encoding.UTF8.GetBytes(payload);
            await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);

            byte[] buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
            try
            {
                using (CancellationTokenSource receiveTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    receiveTimeout.CancelAfter(timeout);
                    using (MemoryStream message = new MemoryStream())
                    {
                        while (socket.State == WebSocketState.Open)
                        {
                            message.SetLength(0);
                            WebSocketReceiveResult receive;
                            do
                            {
                                receive = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), receiveTimeout.Token).ConfigureAwait(false);
                                if (receive.MessageType == WebSocketMessageType.Close)
                                    throw new IOException("The browser DevTools socket closed.");
                                message.Write(buffer, 0, receive.Count);
                            }
                            while (!receive.EndOfMessage);

                            ReadOnlyMemory<byte> jsonBytes = message.TryGetBuffer(out ArraySegment<byte> seg)
                                ? seg.AsMemory(0, (int)message.Length)
                                : message.ToArray();

                            JsonDocument document;
                            try { document = JsonDocument.Parse(jsonBytes); }
                            catch (JsonException) { continue; }

                            using (document)
                            {
                                if (document.RootElement.TryGetProperty("id", out JsonElement replyId) &&
                                    replyId.TryGetInt32(out int receivedId) && receivedId == id)
                                {
                                    if (document.RootElement.TryGetProperty("error", out JsonElement error))
                                    {
                                        string errorText = error.ToString();
                                        throw new InvalidOperationException($"DevTools command {method} failed: {errorText}");
                                    }
                                    return;
                                }
                            }
                        }
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            throw new TimeoutException($"DevTools command {method} did not complete.");
        }

        private static async Task CleanupWhenClosedAsync(int port, Process process, string profileDir)
        {
            try
            {
                bool sawPage = true;
                int missingChecks = 0;

                HttpClient http = DevToolsHttp;
                    while (true)
                    {
                        await Task.Delay(ProbeIntervalMilliseconds).ConfigureAwait(false);
                        int pages = await GetPageCountAsync(http, port).ConfigureAwait(false);

                        if (pages > 0)
                        {
                            sawPage = true;
                            missingChecks = 0;
                            continue;
                        }

                        if (sawPage && ++missingChecks >= 3) break;
                        if (process != null && process.HasExited && missingChecks >= 1) break;
                    }
            }
            catch (Exception exception)
            {
                AppServices.Logger?.Warn("Watching the Chromium Home window failed; cleaning up its temporary profile.", exception);
            }
            finally
            {
                await CloseBrowserAsync(port, process).ConfigureAwait(false);
                if (process != null)
                {
                    SafeExecuteHelper.Try(process.Dispose);
                }
                await WipeProfileWithRetryAsync(profileDir).ConfigureAwait(false);
            }
        }
    }
}

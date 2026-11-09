﻿#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Services;

namespace Shiro.Services
{
    internal static partial class ChromiumHomeBrowser
    {
        private static async Task<int> GetPageCountAsync(HttpClient http, int port)
        {
            try
            {
                string json = await http.GetStringAsync($"http://127.0.0.1:{port}/json").ConfigureAwait(false);
                using (JsonDocument document = JsonDocument.Parse(json))
                {
                    int count = 0;
                    foreach (JsonElement target in document.RootElement.EnumerateArray())
                    {
                        if (target.TryGetProperty("type", out JsonElement type) && type.GetString() == "page")
                            count++;
                    }
                    return count;
                }
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"CountOpenPagesAsync error: {ex.Message}");
                return 0;
            }
        }

        private static async Task CloseBrowserAsync(int port, Process process)
        {
            try
            {
                string? socketUrl = await WaitForBrowserSocketAsync(port, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(socketUrl))
                {
                    using (ClientWebSocket socket = new ClientWebSocket())
                    {
                        using (CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
                        {
                            await socket.ConnectAsync(new Uri(socketUrl), timeout.Token).ConfigureAwait(false);
                            await CallAsync(socket, 1, "Browser.close", new { }, timeout.Token, TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"Browser.close websocket call failed: {ex.Message}");
            }

            try
            {
                if (process != null && !process.HasExited)
                    process.Kill(true);
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"Kill process failed: {ex.Message}");
            }
        }

        private static async Task WipeProfileWithRetryAsync(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory) ||
                !StoragePaths.IsWithinDirectory(directory, StoragePaths.BrowserSessionsDirectory))
                return;

            for (int attempt = 0; attempt < 20; attempt++)
            {
                try
                {
                    if (!Directory.Exists(directory)) return;
                    Directory.Delete(directory, true);
                    return;
                }
                catch (Exception ex)
                {
                    Program.Logger?.Debug($"Directory.Delete attempt {attempt} failed: {ex.Message}");
                    await Task.Delay(500).ConfigureAwait(false);
                }
            }

            // If Chromium still has a file locked, remove credential-bearing files
            // as a final fallback and let the next run finish the directory cleanup.
            try
            {
                string[] sensitiveFiles = { "Cookies", "Login Data", "Web Data", "History", "Network Persistent State", "TransportSecurity", "Trust Tokens" };
                foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                {
                    if (sensitiveFiles.Any(name => Path.GetFileName(file).StartsWith(name, StringComparison.OrdinalIgnoreCase)))
                    {
                        try { File.Delete(file); }
                        catch (Exception ex) { Program.Logger?.Debug($"File.Delete sensitive file failed: {ex.Message}"); }
                    }
                }
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"WipeProfile sensitive files scan failed: {ex.Message}");
            }
        }
    }
}

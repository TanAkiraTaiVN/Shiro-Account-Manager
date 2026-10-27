#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using RBX_Alt_Manager.Classes;

namespace RBX_Alt_Manager.Services
{
    /// <summary>
    /// Direct binary process launcher for RobloxPlayerBeta.exe.
    /// Spawns the game client directly with auth ticket parameters, eliminating protocol handoff delays,
    /// capturing the exact Process ID immediately, and enabling instant singleton mutex closing.
    /// </summary>
    public static class RobloxDirectLauncher
    {
        public sealed class LaunchResult
        {
            public bool Success { get; init; }
            public Process? Process { get; init; }
            public int ProcessId { get; init; }
            public string? ExecutablePath { get; init; }
            public string? ErrorMessage { get; init; }
        }

        /// <summary>
        /// Injects the account's .ROBLOSECURITY session directly into %LOCALAPPDATA%\Roblox\LocalStorage\RobloxCookies.dat
        /// encrypted with Windows DPAPI (CurrentUser). This allows RobloxPlayerBeta.exe to start up directly logged in
        /// without contacting auth.roblox.com/v1/authentication-ticket/redeem, bypassing Error 403 on VPNs completely.
        /// </summary>
        public static bool InjectAccountCookieToLocalStorage(string securityToken, string? browserTrackerId = null)
        {
            if (string.IsNullOrWhiteSpace(securityToken)) return false;
            try
            {
                string token = securityToken.Trim().Trim('"').Trim();
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string localStorageDir = Path.Combine(localAppData, "Roblox", "LocalStorage");
                Directory.CreateDirectory(localStorageDir);
                string cookiesDatPath = Path.Combine(localStorageDir, "RobloxCookies.dat");

                long expiry = DateTimeOffset.UtcNow.AddYears(1).ToUnixTimeSeconds();
                string btid = !string.IsNullOrWhiteSpace(browserTrackerId) ? browserTrackerId : "121002137941";

                string netscapeCookies =
                    $".roblox.com\tTRUE\t/\tTRUE\t{expiry}\t.ROBLOSECURITY\t{token}\r\n" +
                    $".roblox.com\tTRUE\t/\tFALSE\t{expiry}\tRBXEventTrackerV2\tCreateDate={DateTime.UtcNow:MM/dd/yyyy HH:mm:ss}&rbxid=&rbxuid=&browserid={btid};\r\n";

                byte[] plainBytes = System.Text.Encoding.UTF8.GetBytes(netscapeCookies);
                byte[] encryptedBytes = System.Security.Cryptography.ProtectedData.Protect(
                    plainBytes, null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
                string base64Data = Convert.ToBase64String(encryptedBytes);

                var jsonObject = new { CookiesVersion = "1", CookiesData = base64Data };
                string json = System.Text.Json.JsonSerializer.Serialize(jsonObject);

                File.WriteAllText(cookiesDatPath, json, System.Text.Encoding.UTF8);
                Program.Logger?.Info("[RobloxDirectLauncher] Successfully injected .ROBLOSECURITY into RobloxCookies.dat (DPAPI).");
                return true;
            }
            catch (Exception ex)
            {
                Program.Logger?.Warn($"[RobloxDirectLauncher] Failed to inject RobloxCookies.dat: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Attempts to launch RobloxPlayerBeta.exe directly with credentials and server join instructions.
        /// </summary>
        public static LaunchResult LaunchDirect(string ticket, string joinUrl, string username, string? browserTrackerId = null, string? securityToken = null)
        {
            if (string.IsNullOrWhiteSpace(ticket) || string.IsNullOrWhiteSpace(joinUrl))
            {
                return new LaunchResult
                {
                    Success = false,
                    ErrorMessage = "Ticket or JoinUrl is empty."
                };
            }

            string playerPath = Account.FindRobloxPlayerExecutablePublic();
            if (string.IsNullOrWhiteSpace(playerPath) || !File.Exists(playerPath))
            {
                return new LaunchResult
                {
                    Success = false,
                    ErrorMessage = "RobloxPlayerBeta.exe could not be located on disk."
                };
            }

            try
            {
                if (!string.IsNullOrWhiteSpace(securityToken))
                {
                    InjectAccountCookieToLocalStorage(securityToken, browserTrackerId);
                }

                string args = $"--app -t \"{ticket}\" -j \"{joinUrl}\"";
                if (!string.IsNullOrWhiteSpace(browserTrackerId))
                {
                    args += $" -b {browserTrackerId}";
                }

                var startInfo = new ProcessStartInfo(playerPath, args)
                {
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(playerPath) ?? Environment.CurrentDirectory
                };

                Process? process = Process.Start(startInfo);
                if (process == null)
                {
                    return new LaunchResult
                    {
                        Success = false,
                        ErrorMessage = "Process.Start returned null for RobloxPlayerBeta.exe."
                    };
                }

                int pid = process.Id;
                Program.Logger?.Info($"[RobloxDirectLauncher] Successfully spawned RobloxPlayerBeta.exe directly for {username} (PID: {pid}).");

                // Apply initial process optimizations
                RobloxProcessOptimizer.OptimizeLaunchedProcess(pid);

                return new LaunchResult
                {
                    Success = true,
                    Process = process,
                    ProcessId = pid,
                    ExecutablePath = playerPath
                };
            }
            catch (Exception ex)
            {
                Program.Logger?.Error($"[RobloxDirectLauncher] Direct launch failed for {username}: {ex.Message}");
                return new LaunchResult
                {
                    Success = false,
                    ErrorMessage = ex.Message
                };
            }
        }

        /// <summary>
        /// Attempts to launch RobloxPlayerBeta.exe directly to the Home screen for an account.
        /// </summary>
        public static LaunchResult LaunchHomeDirect(string username, string? ticket = null, string? browserTrackerId = null, string? securityToken = null)
        {
            string playerPath = Account.FindRobloxPlayerExecutablePublic();
            if (string.IsNullOrWhiteSpace(playerPath) || !File.Exists(playerPath))
            {
                return new LaunchResult
                {
                    Success = false,
                    ErrorMessage = "RobloxPlayerBeta.exe could not be located on disk."
                };
            }

            try
            {
                if (!string.IsNullOrWhiteSpace(securityToken))
                {
                    InjectAccountCookieToLocalStorage(securityToken, browserTrackerId);
                }

                string args = "--app";
                // Only pass ticket if securityToken was not directly injected, to prevent 403 ticket redeem failures on VPN
                if (string.IsNullOrWhiteSpace(securityToken) && !string.IsNullOrWhiteSpace(ticket))
                {
                    args += $" -t \"{ticket}\"";
                }
                if (!string.IsNullOrWhiteSpace(browserTrackerId))
                {
                    args += $" -b {browserTrackerId}";
                }

                var startInfo = new ProcessStartInfo(playerPath, args)
                {
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(playerPath) ?? Environment.CurrentDirectory
                };

                Process? process = Process.Start(startInfo);
                if (process == null)
                {
                    return new LaunchResult
                    {
                        Success = false,
                        ErrorMessage = "Process.Start returned null for RobloxPlayerBeta.exe."
                    };
                }

                int pid = process.Id;
                Program.Logger?.Info($"[RobloxDirectLauncher] Successfully spawned RobloxPlayerBeta.exe Home directly for {username} (PID: {pid}).");

                RobloxProcessOptimizer.OptimizeLaunchedProcess(pid);

                return new LaunchResult
                {
                    Success = true,
                    Process = process,
                    ProcessId = pid,
                    ExecutablePath = playerPath
                };
            }
            catch (Exception ex)
            {
                Program.Logger?.Error($"[RobloxDirectLauncher] Direct Home launch failed for {username}: {ex.Message}");
                return new LaunchResult
                {
                    Success = false,
                    ErrorMessage = ex.Message
                };
            }
        }
    }
}

#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Classes;

namespace Shiro.Services
{
    /// <summary>
    /// Direct binary process launcher for RobloxPlayerBeta.exe.
    /// Spawns the game client directly with auth ticket parameters, eliminating protocol handoff delays,
    /// capturing the exact Process ID immediately, and enabling instant singleton mutex closing.
    /// </summary>
    public static class RobloxDirectLauncher
    {
        private static readonly object _cookieLock = new();
        private static CancellationTokenSource? _restoreCts;

        public sealed class LaunchResult
        {
            public bool Success { get; init; }
            public Process? Process { get; init; }
            public int ProcessId { get; init; }
            public string? ExecutablePath { get; init; }
            public string? ErrorMessage { get; init; }
        }

        /// <summary>
        /// Generates a random 11-12 digit browser tracker ID matching Roblox's native format.
        /// </summary>
        public static string GenerateRandomTrackerId()
        {
            long high = Random.Shared.Next(100000, 199999);
            long low = Random.Shared.Next(100000, 999999);
            return $"{high}{low}";
        }

        /// <summary>
        /// Injects the account's .ROBLOSECURITY session directly into %LOCALAPPDATA%\Roblox\LocalStorage\RobloxCookies.dat
        /// encrypted with Windows DPAPI (CurrentUser). This allows RobloxPlayerBeta.exe to start up directly logged in
        /// without contacting auth.roblox.com/v1/authentication-ticket/redeem, bypassing Error 403 on VPNs completely.
        /// Automatically protects the original user's session with a safe backup and schedules restoration.
        /// </summary>
        public static bool InjectAccountCookieToLocalStorage(
            string securityToken,
            string? browserTrackerId = null,
            DateTimeOffset? expiryUtc = null,
            string? localStorageDirectoryOverride = null)
        {
            if (string.IsNullOrWhiteSpace(securityToken)) return false;
            try
            {
                string token = securityToken.Trim().Trim('"').Trim();
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string localStorageDir = localStorageDirectoryOverride ?? Path.Combine(localAppData, "Roblox", "LocalStorage");
                Directory.CreateDirectory(localStorageDir);
                string cookiesDatPath = Path.Combine(localStorageDir, "RobloxCookies.dat");
                string backupPath = Path.Combine(localStorageDir, "RobloxCookies.dat.shiro-orig");

                // Validate existing CookiesVersion if file exists to prevent format corruption
                if (File.Exists(cookiesDatPath))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllBytes(cookiesDatPath));
                        if (doc.RootElement.TryGetProperty("CookiesVersion", out var verElement))
                        {
                            string? ver = verElement.GetString();
                            if (!string.IsNullOrEmpty(ver) && ver != "1")
                            {
                                Program.Logger?.Warn($"[RobloxDirectLauncher] RobloxCookies.dat has unsupported CookiesVersion '{ver}'. Direct injection skipped.");
                                return false;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Program.Logger?.Debug($"[RobloxDirectLauncher] Could not inspect existing RobloxCookies.dat JSON: {ex.Message}");
                    }

                    // Preserve original user session backup if not already backed up
                    if (!File.Exists(backupPath))
                    {
                        try
                        {
                            File.Copy(cookiesDatPath, backupPath, overwrite: false);
                            Program.Logger?.Info("[RobloxDirectLauncher] Backed up original RobloxCookies.dat before injection.");
                        }
                        catch (Exception ex)
                        {
                            Program.Logger?.Debug($"[RobloxDirectLauncher] Could not create original cookie backup: {ex.Message}");
                        }
                    }
                }

                long expiry = (expiryUtc ?? DateTimeOffset.UtcNow.AddMonths(3)).ToUnixTimeSeconds();
                string btid = !string.IsNullOrWhiteSpace(browserTrackerId) ? browserTrackerId : GenerateRandomTrackerId();

                string netscapeCookies =
                    $".roblox.com\tTRUE\t/\tTRUE\t{expiry}\t.ROBLOSECURITY\t{token}\r\n" +
                    $".roblox.com\tTRUE\t/\tFALSE\t{expiry}\tRBXEventTrackerV2\tCreateDate={DateTime.UtcNow:MM/dd/yyyy HH:mm:ss}&rbxid=&rbxuid=&browserid={btid};\r\n";

                byte[] plainBytes = System.Text.Encoding.UTF8.GetBytes(netscapeCookies);
                byte[] encryptedBytes = System.Security.Cryptography.ProtectedData.Protect(
                    plainBytes, null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
                string base64Data = Convert.ToBase64String(encryptedBytes);

                var jsonObject = new { CookiesVersion = "1", CookiesData = base64Data };
                string json = JsonSerializer.Serialize(jsonObject);

                StoragePaths.AtomicWriteText(cookiesDatPath, json, durable: true);
                Program.Logger?.Info("[RobloxDirectLauncher] Successfully injected .ROBLOSECURITY into RobloxCookies.dat (DPAPI, atomic).");

                if (localStorageDirectoryOverride == null)
                {
                    ScheduleOriginalCookieRestore(8000);
                }

                return true;
            }
            catch (Exception ex)
            {
                Program.Logger?.Warn($"[RobloxDirectLauncher] Failed to inject RobloxCookies.dat: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Schedules restoration of the original user's RobloxCookies.dat after a delay once launched processes have initialized.
        /// </summary>
        public static void ScheduleOriginalCookieRestore(int delayMs = 8000)
        {
            lock (_cookieLock)
            {
                _restoreCts?.Cancel();
                _restoreCts?.Dispose();
                _restoreCts = new CancellationTokenSource();
                var token = _restoreCts.Token;

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(delayMs, token).ConfigureAwait(false);
                        RestoreOriginalCookies();
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex)
                    {
                        Program.Logger?.Debug($"[RobloxDirectLauncher] Scheduled cookie restoration failed: {ex.Message}");
                    }
                }, token);
            }
        }

        /// <summary>
        /// Restores the original RobloxCookies.dat backup if one exists and cleans up the backup file.
        /// </summary>
        public static bool RestoreOriginalCookies(string? localStorageDirectoryOverride = null)
        {
            lock (_cookieLock)
            {
                _restoreCts?.Cancel();
                _restoreCts?.Dispose();
                _restoreCts = null;
            }

            try
            {
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string localStorageDir = localStorageDirectoryOverride ?? Path.Combine(localAppData, "Roblox", "LocalStorage");
                string cookiesDatPath = Path.Combine(localStorageDir, "RobloxCookies.dat");
                string backupPath = Path.Combine(localStorageDir, "RobloxCookies.dat.shiro-orig");

                if (!File.Exists(backupPath)) return false;

                string backupJson = File.ReadAllText(backupPath, System.Text.Encoding.UTF8);
                StoragePaths.AtomicWriteText(cookiesDatPath, backupJson, durable: true);
                try
                {
                    File.Delete(backupPath);
                }
                catch (Exception ex)
                {
                    Program.Logger?.Debug($"[RobloxDirectLauncher] Failed to delete cookie backup file: {ex.Message}");
                }

                Program.Logger?.Info("[RobloxDirectLauncher] Successfully restored original RobloxCookies.dat from backup.");
                return true;
            }
            catch (Exception ex)
            {
                Program.Logger?.Warn($"[RobloxDirectLauncher] Failed to restore original RobloxCookies.dat: {ex.Message}");
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

            string? playerPath = Account.FindRobloxPlayerExecutablePublic();
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
            string? playerPath = Account.FindRobloxPlayerExecutablePublic();
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

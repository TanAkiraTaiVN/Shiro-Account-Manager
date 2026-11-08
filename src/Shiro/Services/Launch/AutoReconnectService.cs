#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Classes;

namespace Shiro.Services
{
    /// <summary>
    /// Tracks active Roblox game sessions and automatically recovers/reconnects accounts
    /// when the Roblox client crashes, disconnects (Errors 277/268), or terminates unexpectedly.
    /// Provides exponential backoff, maximum retry limits, and suppression on user-initiated closures.
    /// </summary>
    public sealed partial class AutoReconnectService : IDisposable
    {
        public static AutoReconnectService Instance { get; } = new AutoReconnectService();

        private readonly ConcurrentDictionary<string, ReconnectSession> _sessions =
            new ConcurrentDictionary<string, ReconnectSession>(StringComparer.OrdinalIgnoreCase);

        private readonly SemaphoreSlim _reconnectGate = new SemaphoreSlim(1, 1);
        private readonly Timer _monitorTimer;
        private readonly System.Threading.Lock _lock = new();
        private bool _disposed;
        private int _totalReconnections;

        private static string GetSessionKey(Account? account)
        {
            if (account == null) return string.Empty;
            if (!string.IsNullOrWhiteSpace(account.Username)) return account.Username;
            return account.UserID > 0 ? $"uid:{account.UserID}" : string.Empty;
        }

        public bool IsEnabled
        {
            get => _isEnabled && AppSettings.General.Get<bool>("EnableAutoReconnect", false);
            set { _isEnabled = value; AppSettings.General.Set("EnableAutoReconnect", value ? "true" : "false"); }
        }
        private bool _isEnabled = true;
        public int MaxRetries { get; set; } = 5;
        public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromSeconds(10);
        internal int? TestReconnectDelayMsOverride { get; set; }
        internal Func<ReconnectSession, Task<string>>? TestJoinServerOverride { get; set; }
        public TimeSpan LaunchGracePeriod { get; set; } = TimeSpan.FromSeconds(15);
        public TimeSpan DisconnectTimeout { get; set; } = TimeSpan.FromSeconds(20);
        public TimeSpan StableResetWindow { get; set; } = TimeSpan.FromMinutes(10);
        public int ActiveSessionsCount
        {
            get
            {
                int count = 0;
                foreach (var kvp in _sessions)
                {
                    var s = kvp.Value;
                    if (s != null && s.IsActive && !s.IsSuppressed)
                        count++;
                }
                return count;
            }
        }
        public int TotalReconnections => _totalReconnections;

        public event Action<ReconnectSession, int>? ReconnectAttempted;
        public event Action<ReconnectSession>? MaxRetriesExceeded;

        public AutoReconnectService()
        {
            RobloxProcess.Disconnected += NotifyDisconnected;
            RobloxProcess.Connected += NotifyConnected;
            RobloxJobObjectService.ProcessExitedFromJob += OnJobProcessExited;
            MaxRetriesExceeded += OnMaxRetriesExceeded;

            // Scan process states every 5 seconds
            _monitorTimer = new Timer(CheckSessionsCallback, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        }

        private static void OnMaxRetriesExceeded(ReconnectSession session)
        {
            if (session?.Account?.Username is { } username)
            {
                WebhookNotificationService.EnqueueAlert(new WebhookAlert
                {
                    AccountUsername = username,
                    EventType = WebhookEventType.MaxRetriesExceeded,
                    Description = $"Exceeded max reconnect attempts ({session.RetryCount}).",
                    Timestamp = DateTimeOffset.UtcNow
                });
            }
        }

        public void NotifyDisconnected(int processId, int reason = 0)
        {
            if (processId <= 0 || _disposed) return;

            foreach (var kvp in _sessions)
            {
                var session = kvp.Value;
                if (session.ProcessId == processId && session.IsActive && !session.IsSuppressed)
                {
                    bool isFirstDisconnect = !session.LastDisconnectUtc.HasValue;
                    if (isFirstDisconnect)
                    {
                        session.LastDisconnectUtc = DateTime.UtcNow;
                    }
                    session.LastDisconnectReason = reason;
                    Program.Logger?.Warn($"[AutoReconnect] Detected in-game disconnect (reason {reason}) for '{session.Account?.Username}' (PID: {processId}).");

                    if (isFirstDisconnect && session.Account?.Username is { } username)
                    {
                        WebhookNotificationService.EnqueueAlert(new WebhookAlert
                        {
                            AccountUsername = username,
                            EventType = WebhookEventType.Disconnected,
                            Description = reason > 0
                                ? $"Roblox client disconnected with Error {reason}."
                                : "Roblox client disconnected.",
                            Timestamp = DateTimeOffset.UtcNow
                        });
                    }
                }
            }
        }

        public void NotifyConnected(int processId)
        {
            if (processId <= 0 || _disposed) return;

            foreach (var kvp in _sessions)
            {
                var session = kvp.Value;
                if (session.ProcessId == processId && session.LastDisconnectUtc.HasValue)
                {
                    session.LastDisconnectUtc = null;
                    session.LastDisconnectReason = 0;
                }
            }
        }

        /// <summary>
        /// Registers or updates an active launched game session for monitoring.
        /// Preserves RetryCount if re-registered within StableResetWindow, resetting it only after stable play.
        /// </summary>
        public void RegisterSession(Account account, long placeId, string jobId = "", bool followUser = false, bool joinVip = false, int processId = 0, DateTime? nowUtcOverride = null)
        {
            string sessionKey = GetSessionKey(account);
            if (account == null || string.IsNullOrWhiteSpace(sessionKey))
                return;

            DateTime now = nowUtcOverride ?? DateTime.UtcNow;
            int preservedRetries = 0;
            DateTime? preservedLastReconnect = null;

            if (_sessions.TryGetValue(sessionKey, out var existing) && existing != null)
            {
                existing.IsSuppressed = true;
                existing.IsActive = false;
                DateTime baseline = existing.LastReconnectUtc ?? existing.LaunchTimeUtc;
                if (existing.RetryCount > 0 && (now - baseline) < StableResetWindow)
                {
                    preservedRetries = existing.RetryCount;
                    preservedLastReconnect = existing.LastReconnectUtc;
                }
            }

            var session = new ReconnectSession
            {
                Account = account,
                PlaceId = placeId,
                JobId = jobId ?? string.Empty,
                FollowUser = followUser,
                JoinVip = joinVip,
                ProcessId = processId,
                RetryCount = preservedRetries,
                LaunchTimeUtc = now,
                LastCheckUtc = now,
                LastReconnectUtc = preservedLastReconnect,
                LastDisconnectUtc = null,
                LastDisconnectReason = 0,
                IsActive = true,
                IsSuppressed = false
            };

            _sessions.AddOrUpdate(sessionKey, session, (_, _) => session);
            Program.Logger?.Info($"[AutoReconnect] Registered session for '{sessionKey}' (PID: {processId}, PlaceId: {placeId}, RetryCount: {preservedRetries}).");

            if (processId > 0)
            {
                _ = ObserveProcessExitImmediateAsync(session);
            }
        }

        private async Task ObserveProcessExitImmediateAsync(ReconnectSession session)
        {
            if (session == null || session.ProcessId <= 0)
                return;

            try
            {
                using var proc = Process.GetProcessById(session.ProcessId);
                await proc.WaitForExitAsync().ConfigureAwait(false);
                session.ObservedProcessExited = true;
                try { session.ObservedExitCode = proc.ExitCode; }
                catch (Exception ex) { Program.Logger?.Debug($"Get ExitCode for pid {session.ProcessId} failed: {ex.Message}"); }

                if (!IsEnabled || _disposed || !session.IsActive || session.IsSuppressed)
                    return;

                DateTime now = DateTime.UtcNow;
                var decision = Evaluate(
                    session,
                    processExited: true,
                    exitCode: session.ObservedExitCode,
                    nowUtc: now,
                    maxRetries: MaxRetries,
                    gracePeriod: LaunchGracePeriod,
                    disconnectTimeout: DisconnectTimeout);

                if (decision == ReconnectDecision.CloseCleanly)
                {
                    session.IsSuppressed = true;
                    session.IsActive = false;
                    _sessions.TryRemove(GetSessionKey(session.Account), out _);
                    Program.Logger?.Info($"[AutoReconnect] '{session.Account.Username}' exited gracefully by user (ExitCode 0). Session closed.");
                }
                else if (decision == ReconnectDecision.Reconnect || decision == ReconnectDecision.GiveUp)
                {
                    HandleProcessExit(session);
                }
            }
            catch (Exception ex)
            {
                AppServices.Logger.Debug("[AutoReconnectService] Process observation failed", ex);
                // Fallback timer will handle any edge case
            }
        }

        internal void CheckSessionsCallback(object? state)
        {
            if (!IsEnabled || _disposed || _sessions.IsEmpty)
                return;

            DateTime now = DateTime.UtcNow;
            HashSet<int>? runningPids = null;

            foreach (var kvp in _sessions)
            {
                var session = kvp.Value;
                if (!session.IsActive || session.IsSuppressed)
                    continue;

                // Reset retry counter after continuous stable play
                if (session.RetryCount > 0 && !session.LastDisconnectUtc.HasValue && (now - session.LaunchTimeUtc) >= StableResetWindow)
                {
                    session.RetryCount = 0;
                }

                if (session.ProcessId <= 0)
                    continue;

                bool processExited = session.ObservedProcessExited;
                int? exitCode = session.ObservedExitCode;
                if (!processExited)
                {
                    runningPids ??= new HashSet<int>(RobloxProcessService.GetRunningRobloxPids());
                    if (!runningPids.Contains(session.ProcessId))
                    {
                        try
                        {
                            using var proc = Process.GetProcessById(session.ProcessId);
                            if (proc.HasExited)
                            {
                                processExited = true;
                                try { exitCode = proc.ExitCode; }
                                catch (Exception ex) { Program.Logger?.Debug($"Get ExitCode failed: {ex.Message}"); }
                            }
                        }
                        catch (ArgumentException)
                        {
                            // Process ID no longer exists
                            processExited = true;
                        }
                        catch (Exception ex)
                        {
                            Program.Logger?.Debug($"[AutoReconnect] Error checking PID {session.ProcessId}: {ex.Message}");
                        }
                    }
                }

                ReconnectDecision decision = Evaluate(
                    session,
                    processExited,
                    exitCode,
                    now,
                    MaxRetries,
                    LaunchGracePeriod,
                    DisconnectTimeout);

                switch (decision)
                {
                    case ReconnectDecision.CloseCleanly:
                        session.IsSuppressed = true;
                        session.IsActive = false;
                        _sessions.TryRemove(GetSessionKey(session.Account), out _);
                        Program.Logger?.Info($"[AutoReconnect] '{session.Account.Username}' exited gracefully by user (ExitCode 0). Session closed.");
                        break;

                    case ReconnectDecision.KillAndReconnect:
                        try
                        {
                            using var staleProc = Process.GetProcessById(session.ProcessId);
                            if (!staleProc.HasExited)
                            {
                                Program.Logger?.Warn($"[AutoReconnect] Terminating disconnected Roblox client PID {session.ProcessId} for '{session.Account.Username}' before reconnecting...");
                                staleProc.Kill();
                                staleProc.WaitForExit(1000);
                            }
                        }
                        catch (Exception ex)
                        {
                            Program.Logger?.Debug($"Kill stale disconnected client PID {session.ProcessId} failed: {ex.Message}");
                        }
                        HandleProcessExit(session);
                        break;

                    case ReconnectDecision.Reconnect:
                    case ReconnectDecision.GiveUp:
                        HandleProcessExit(session);
                        break;
                }
            }
        }

        internal void HandleProcessExit(ReconnectSession session)
        {
            lock (_lock)
            {
                if (!session.IsActive || session.IsSuppressed)
                    return;

                // Mark inactive while attempting reconnect to prevent re-entrant checks
                session.IsActive = false;
            }

            string sessionKey = GetSessionKey(session.Account);
            if (session.RetryCount >= MaxRetries)
            {
                Program.Logger?.Warn($"[AutoReconnect] '{session.Account.Username}' exceeded max reconnect attempts ({MaxRetries}). Aborting.");
                MaxRetriesExceeded?.Invoke(session);
                _sessions.TryRemove(sessionKey, out _);
                return;
            }

            session.RetryCount++;
            session.LastReconnectUtc = DateTime.UtcNow;
            session.LastDisconnectUtc = null;
            int delaySeconds = (int)(InitialBackoff.TotalSeconds * Math.Pow(1.5, session.RetryCount - 1));
            delaySeconds = Math.Clamp(delaySeconds, 5, 60);

            Program.Logger?.Warn($"[AutoReconnect] Roblox client for '{session.Account.Username}' terminated/disconnected. Scheduling reconnect attempt #{session.RetryCount}/{MaxRetries} in {delaySeconds}s...");
            ReconnectAttempted?.Invoke(session, session.RetryCount);

            Task.Run(async () =>
            {
                try
                {
                    if (TestReconnectDelayMsOverride.HasValue)
                        await Task.Delay(TestReconnectDelayMsOverride.Value).ConfigureAwait(false);
                    else
                        await Task.Delay(TimeSpan.FromSeconds(delaySeconds)).ConfigureAwait(false);

                    if (_disposed || session.IsSuppressed ||
                        !_sessions.TryGetValue(sessionKey, out var activeSession) ||
                        !ReferenceEquals(activeSession, session))
                    {
                        Program.Logger?.Info($"[AutoReconnect] Reconnect for '{session.Account.Username}' cancelled due to user suppression.");
                        return;
                    }

                    await _reconnectGate.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        if (_disposed || session.IsSuppressed ||
                            !_sessions.TryGetValue(sessionKey, out activeSession) ||
                            !ReferenceEquals(activeSession, session))
                            return;

                        await RobloxRateLimitCoordinator.Instance.WaitForBackoffAsync().ConfigureAwait(false);

                        Program.Logger?.Info($"[AutoReconnect] Reconnecting '{session.Account.Username}' to PlaceId: {session.PlaceId}...");
                        string result = TestJoinServerOverride != null
                            ? await TestJoinServerOverride(session).ConfigureAwait(false)
                            : await session.Account.JoinServer(
                                session.PlaceId,
                                session.JobId,
                                session.FollowUser,
                                session.JoinVip).ConfigureAwait(false);

                        if (result == "Success")
                        {
                            Interlocked.Increment(ref _totalReconnections);
                            session.LaunchTimeUtc = DateTime.UtcNow;
                            session.ObservedProcessExited = false;
                            session.ObservedExitCode = null;
                            session.IsActive = true;
                            Program.Logger?.Info($"[AutoReconnect] Successfully reconnected '{session.Account.Username}'.");
                        }
                        else
                        {
                            Program.Logger?.Warn($"[AutoReconnect] Reconnect for '{session.Account.Username}' failed: {result}");
                            // Mark active again so next monitor cycle can retry if under MaxRetries
                            session.IsActive = true;
                        }
                    }
                    finally
                    {
                        _reconnectGate.Release();
                    }
                }
                catch (Exception ex)
                {
                    Program.Logger?.Error($"[AutoReconnect] Reconnect task error for '{session.Account.Username}': {ex.Message}");
                    session.IsActive = true;
                }
            });
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            RobloxProcess.Disconnected -= NotifyDisconnected;
            RobloxProcess.Connected -= NotifyConnected;
            RobloxJobObjectService.ProcessExitedFromJob -= OnJobProcessExited;
            MaxRetriesExceeded -= OnMaxRetriesExceeded;
            _monitorTimer?.Dispose();
            _sessions.Clear();
        }
    }
}

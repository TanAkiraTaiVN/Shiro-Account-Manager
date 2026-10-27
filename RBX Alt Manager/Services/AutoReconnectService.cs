#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RBX_Alt_Manager.Classes;

namespace RBX_Alt_Manager.Services
{
    /// <summary>
    /// Tracks active Roblox game sessions and automatically recovers/reconnects accounts
    /// when the Roblox client crashes, disconnects (Errors 277/268), or terminates unexpectedly.
    /// Provides exponential backoff, maximum retry limits, and suppression on user-initiated closures.
    /// </summary>
    public sealed class AutoReconnectService : IDisposable
    {
        public static AutoReconnectService Instance { get; } = new AutoReconnectService();

        public enum ReconnectDecision
        {
            None,
            CloseCleanly,
            Reconnect,
            KillAndReconnect,
            GiveUp
        }

        public sealed class ReconnectSession
        {
            public required Account Account { get; init; }
            public long PlaceId { get; set; }
            public string JobId { get; set; } = string.Empty;
            public bool FollowUser { get; set; }
            public bool JoinVip { get; set; }
            public int ProcessId { get; set; }
            public int RetryCount { get; set; }
            public DateTime LaunchTimeUtc { get; set; }
            public DateTime LastCheckUtc { get; set; }
            public DateTime? LastReconnectUtc { get; set; }
            public DateTime? LastDisconnectUtc { get; set; }
            public int LastDisconnectReason { get; set; }
            public int? ObservedExitCode { get; set; }
            public bool ObservedProcessExited { get; set; }
            public bool IsActive { get; set; } = true;
            public bool IsSuppressed { get; set; }
        }

        private readonly ConcurrentDictionary<string, ReconnectSession> _sessions =
            new ConcurrentDictionary<string, ReconnectSession>(StringComparer.OrdinalIgnoreCase);

        private readonly Timer _monitorTimer;
        private readonly System.Threading.Lock _lock = new();
        private bool _disposed;
        private int _totalReconnections;

        public bool IsEnabled
        {
            get => _isEnabled && (AccountManager.General?.Get<bool>("EnableAutoReconnect", false) ?? false);
            set => _isEnabled = value;
        }
        private bool _isEnabled = true;
        public int MaxRetries { get; set; } = 5;
        public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromSeconds(10);
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

            // Scan process states every 5 seconds
            _monitorTimer = new Timer(CheckSessionsCallback, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        }

        public void NotifyDisconnected(int processId, int reason = 0)
        {
            if (processId <= 0 || _disposed) return;

            foreach (var kvp in _sessions)
            {
                var session = kvp.Value;
                if (session.ProcessId == processId && session.IsActive && !session.IsSuppressed)
                {
                    if (!session.LastDisconnectUtc.HasValue)
                    {
                        session.LastDisconnectUtc = DateTime.UtcNow;
                    }
                    session.LastDisconnectReason = reason;
                    Program.Logger?.Warn($"[AutoReconnect] Detected in-game disconnect (reason {reason}) for '{session.Account?.Username}' (PID: {processId}).");
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
        /// Evaluates what action AutoReconnect should take for a monitored session.
        /// Pure and deterministic for unit testing.
        /// </summary>
        public static ReconnectDecision Evaluate(
            ReconnectSession session,
            bool processExited,
            int? exitCode,
            DateTime nowUtc,
            int maxRetries = 5,
            TimeSpan? gracePeriod = null,
            TimeSpan? disconnectTimeout = null)
        {
            if (session == null || !session.IsActive || session.IsSuppressed)
                return ReconnectDecision.None;

            TimeSpan grace = gracePeriod ?? TimeSpan.FromSeconds(15);
            if ((nowUtc - session.LaunchTimeUtc) < grace)
                return ReconnectDecision.None;

            if (processExited)
            {
                // Clean exit (code 0) is only treated as user-initiated when no disconnect preceded it
                if (exitCode == 0 && !session.LastDisconnectUtc.HasValue)
                    return ReconnectDecision.CloseCleanly;

                return session.RetryCount >= maxRetries
                    ? ReconnectDecision.GiveUp
                    : ReconnectDecision.Reconnect;
            }

            TimeSpan dcTimeout = disconnectTimeout ?? TimeSpan.FromSeconds(20);
            if (session.LastDisconnectUtc.HasValue && (nowUtc - session.LastDisconnectUtc.Value) >= dcTimeout)
            {
                return session.RetryCount >= maxRetries
                    ? ReconnectDecision.GiveUp
                    : ReconnectDecision.KillAndReconnect;
            }

            return ReconnectDecision.None;
        }

        /// <summary>
        /// Registers or updates an active launched game session for monitoring.
        /// Preserves RetryCount if re-registered within StableResetWindow, resetting it only after stable play.
        /// </summary>
        public void RegisterSession(Account account, long placeId, string jobId = "", bool followUser = false, bool joinVip = false, int processId = 0, DateTime? nowUtcOverride = null)
        {
            if (account == null || string.IsNullOrWhiteSpace(account.Username))
                return;

            DateTime now = nowUtcOverride ?? DateTime.UtcNow;
            int preservedRetries = 0;
            DateTime? preservedLastReconnect = null;

            if (_sessions.TryGetValue(account.Username, out var existing) && existing != null)
            {
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

            _sessions.AddOrUpdate(account.Username, session, (_, _) => session);
            Program.Logger?.Info($"[AutoReconnect] Registered session for '{account.Username}' (PID: {processId}, PlaceId: {placeId}, RetryCount: {preservedRetries}).");

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
                    _sessions.TryRemove(session.Account.Username, out _);
                    Program.Logger?.Info($"[AutoReconnect] '{session.Account.Username}' exited gracefully by user (ExitCode 0). Session closed.");
                }
                else if (decision == ReconnectDecision.Reconnect || decision == ReconnectDecision.GiveUp)
                {
                    HandleProcessExit(session);
                }
            }
            catch
            {
                // Fallback timer will handle any edge case
            }
        }

        /// <summary>
        /// Removes an account session from monitoring (e.g. account removed or gracefully finished).
        /// </summary>
        public void UnregisterSession(string username)
        {
            if (string.IsNullOrWhiteSpace(username)) return;
            if (_sessions.TryRemove(username, out _))
            {
                Program.Logger.Debug($"[AutoReconnect] Unregistered session for '{username}'.");
            }
        }

        /// <summary>
        /// Suppresses reconnecting for a specific account (e.g. user manually killed or closed its client).
        /// </summary>
        public void SuppressReconnect(string username)
        {
            if (string.IsNullOrWhiteSpace(username)) return;
            if (_sessions.TryGetValue(username, out var session))
            {
                session.IsSuppressed = true;
                session.IsActive = false;
                Program.Logger.Debug($"[AutoReconnect] Reconnection suppressed for '{username}'.");
            }
        }

        /// <summary>
        /// Suppresses all active sessions from reconnecting (e.g. user clicked 'Close Roblox' or app is shutting down).
        /// </summary>
        public void SuppressAll()
        {
            foreach (var session in _sessions.Values)
            {
                session.IsSuppressed = true;
                session.IsActive = false;
            }
            Program.Logger.Info("[AutoReconnect] Suppressed reconnect for all active sessions.");
        }

        /// <summary>
        /// Clears all tracked sessions.
        /// </summary>
        public void Clear()
        {
            _sessions.Clear();
        }

        /// <summary>
        /// Retrieves the session for a given account username, if registered.
        /// </summary>
        public bool TryGetSession(string? username, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ReconnectSession? session)
        {
            session = null;
            if (string.IsNullOrWhiteSpace(username))
                return false;
            return _sessions.TryGetValue(username, out session);
        }

        private void CheckSessionsCallback(object? state)
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
                        _sessions.TryRemove(session.Account.Username, out _);
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

        private void HandleProcessExit(ReconnectSession session)
        {
            lock (_lock)
            {
                if (!session.IsActive || session.IsSuppressed)
                    return;

                // Mark inactive while attempting reconnect to prevent re-entrant checks
                session.IsActive = false;
            }

            if (session.RetryCount >= MaxRetries)
            {
                Program.Logger?.Warn($"[AutoReconnect] '{session.Account.Username}' exceeded max reconnect attempts ({MaxRetries}). Aborting.");
                MaxRetriesExceeded?.Invoke(session);
                _sessions.TryRemove(session.Account.Username, out _);
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
                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds)).ConfigureAwait(false);

                    if (session.IsSuppressed)
                    {
                        Program.Logger?.Info($"[AutoReconnect] Reconnect for '{session.Account.Username}' cancelled due to user suppression.");
                        return;
                    }

                    Program.Logger?.Info($"[AutoReconnect] Reconnecting '{session.Account.Username}' to PlaceId: {session.PlaceId}...");
                    string result = await session.Account.JoinServer(
                        session.PlaceId,
                        session.JobId,
                        session.FollowUser,
                        session.JoinVip).ConfigureAwait(false);

                    if (result == "Success")
                    {
                        Interlocked.Increment(ref _totalReconnections);
                        session.LaunchTimeUtc = DateTime.UtcNow;
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
            _monitorTimer?.Dispose();
            _sessions.Clear();
        }
    }
}

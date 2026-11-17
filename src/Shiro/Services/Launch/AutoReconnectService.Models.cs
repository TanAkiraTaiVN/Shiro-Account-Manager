#nullable enable
using System;

namespace Shiro.Services
{
    public sealed partial class AutoReconnectService
    {
        private int _activeGateUsers;
        private int _gateDisposed;

        private bool TryEnterReconnectGate()
        {
            System.Threading.Interlocked.Increment(ref _activeGateUsers);
            if (_disposed)
            {
                ExitReconnectGate(acquired: false);
                return false;
            }
            return true;
        }

        private void ExitReconnectGate(bool acquired)
        {
            if (acquired)
            {
                try { _reconnectGate.Release(); }
                catch (ObjectDisposedException) { /* disposed concurrently */ }
            }

            if (System.Threading.Interlocked.Decrement(ref _activeGateUsers) == 0 && _disposed)
                TryDisposeReconnectGate();
        }

        private void TryDisposeReconnectGate()
        {
            if (System.Threading.Interlocked.Exchange(ref _gateDisposed, 1) == 0)
            {
                try { _reconnectGate.Dispose(); }
                catch (ObjectDisposedException) { /* already disposed */ }
            }
        }

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
            internal bool NeedsRetry { get; set; }
            internal System.Threading.CancellationTokenSource? ReconnectCts { get; set; }

            internal void CancelPendingReconnect()
            {
                var cts = System.Threading.Interlocked.Exchange(ref _ctsBacking, null);
                if (cts != null)
                {
                    try { cts.Cancel(); }
                    catch (ObjectDisposedException) { /* already disposed */ }
                    cts.Dispose();
                }
            }

            internal System.Threading.CancellationToken ResetReconnectToken(System.Threading.CancellationToken outerToken)
            {
                CancelPendingReconnect();
                var linked = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(outerToken);
                _ctsBacking = linked;
                ReconnectCts = linked;
                return linked.Token;
            }
            private System.Threading.CancellationTokenSource? _ctsBacking;
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
        /// Removes an account session from monitoring (e.g. account removed or gracefully finished).
        /// </summary>
        public void UnregisterAccountSession(Account? account)
        {
            string key = GetSessionKey(account);
            if (!string.IsNullOrEmpty(key)) UnregisterSession(key);
        }

        public void UnregisterSession(string username)
        {
            if (string.IsNullOrWhiteSpace(username)) return;
            if (_sessions.TryRemove(username, out var removed))
            {
                removed?.CancelPendingReconnect();
                AppServices.Logger.Debug($"[AutoReconnect] Unregistered session for '{username}'.");
            }
        }

        public void SuppressReconnectForAccount(Account? account)
        {
            string key = GetSessionKey(account);
            if (!string.IsNullOrEmpty(key)) SuppressReconnect(key);
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
                session.CancelPendingReconnect();
                AppServices.Logger?.Debug($"[AutoReconnect] Reconnection suppressed for '{username}'.");
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
                session.CancelPendingReconnect();
            }
            AppServices.Logger.Info("[AutoReconnect] Suppressed reconnect for all active sessions.");
        }

        /// <summary>
        /// Clears all tracked sessions.
        /// </summary>
        public void Clear()
        {
            foreach (var s in _sessions.Values)
                s?.CancelPendingReconnect();
            _sessions.Clear();
        }

        public bool TryGetAccountSession(Account? account, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ReconnectSession? session) =>
            TryGetSession(GetSessionKey(account), out session);

        /// <summary>
        /// Retrieves the session for a given account username, if registered.
        /// </summary>
        public bool TryGetSession(string? username, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ReconnectSession? session)
        {
            session = null;
            return !string.IsNullOrWhiteSpace(username) && _sessions.TryGetValue(username, out session);
        }

        internal void NotifyMaxRetriesExceededForTesting(string username)
        {
            if (TryGetSession(username, out var session) && session != null)
                MaxRetriesExceeded?.Invoke(session);
        }

        /// <summary>
        /// Suppresses reconnecting for any session bound to the specified process ID.
        /// </summary>
        public void SuppressReconnectByPid(int processId)
        {
            if (processId <= 0) return;
            foreach (var kvp in _sessions)
            {
                var session = kvp.Value;
                if (session != null && session.ProcessId == processId)
                {
                    session.IsSuppressed = true;
                    session.IsActive = false;
                    AppServices.Logger?.Debug($"[AutoReconnect] Reconnection suppressed for PID {processId} ('{session.Account?.Username}').");
                }
            }
        }

        private void OnJobProcessExited(int processId, bool abnormalExit)
        {
            if (processId <= 0 || _disposed) return;

            bool matchedAny = false;
            foreach (var kvp in _sessions)
            {
                var session = kvp.Value;
                if (session != null && session.ProcessId == processId && session.IsActive && !session.IsSuppressed)
                {
                    session.ObservedProcessExited = true;
                    if (!session.ObservedExitCode.HasValue)
                    {
                        if (abnormalExit)
                        {
                            session.ObservedExitCode = -1;
                        }
                        else
                        {
                            try
                            {
                                using var p = System.Diagnostics.Process.GetProcessById(processId);
                                session.ObservedExitCode = p.HasExited ? p.ExitCode : 0;
                            }
                            catch (Exception ex)
                            {
                                AppServices.Logger?.Debug($"[AutoReconnect] ExitCode fallback to 0 on clean job exit: {ex.Message}");
                                session.ObservedExitCode = 0;
                            }
                        }
                    }
                    matchedAny = true;
                }
            }

            if (matchedAny)
                _ = System.Threading.Tasks.Task.Run(() => CheckSessionsCallback(null));
        }
    }
}

#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Shiro.Services
{
    public sealed partial class AutoReconnectService
    {
        private int _activeGateUsers;
        private int _gateDisposed;

        private bool TryEnterReconnectGate()
        {
            Interlocked.Increment(ref _activeGateUsers);
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

            if (Interlocked.Decrement(ref _activeGateUsers) == 0 && _disposed)
                TryDisposeReconnectGate();
        }

        private void TryDisposeReconnectGate()
        {
            if (Interlocked.Exchange(ref _gateDisposed, 1) == 0)
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
            public long ProcessCreationTicks { get; set; }
            public int RetryCount { get; set; }
            public DateTime LaunchTimeUtc { get; set; }
            public DateTime LastCheckUtc { get; set; }
            public DateTime? LastReconnectUtc { get; set; }
            public DateTime? LastDisconnectUtc { get; set; }
            public int LastDisconnectReason { get; set; }
            public int? ObservedExitCode { get; set; }
            public bool ObservedProcessExited { get; set; }
            public bool IsActive { get => _isActive; set => _isActive = value; }
            public bool IsSuppressed { get => _isSuppressed; set => _isSuppressed = value; }
            internal bool NeedsRetry { get; set; }
            internal CancellationTokenSource? ReconnectCts { get; set; }
            internal Task? ObserveExitTask { get; set; }

            internal void CancelPendingReconnect() => DetachTokensAndCancel(deactivate: false, suppress: false);
            internal void DeactivateAndCancel() => DetachTokensAndCancel(deactivate: true, suppress: false);
            internal void MarkSuppressedAndCancel() => DetachTokensAndCancel(deactivate: true, suppress: true);

            private void DetachTokensAndCancel(bool deactivate, bool suppress)
            {
                CancellationTokenSource? cts;
                CancellationTokenSource? observeCts;
                lock (_tokenLock)
                {
                    if (suppress) _isSuppressed = true;
                    if (deactivate || suppress) _isActive = false;
                    cts = Interlocked.Exchange(ref _ctsBacking, null);
                    observeCts = Interlocked.Exchange(ref _observeCts, null);
                }

                CancelAndDisposeCts(cts);
                CancelAndDisposeCts(observeCts);
            }

            internal void ReleaseObserveToken()
            {
                CancellationTokenSource? observeCts;
                lock (_tokenLock)
                    observeCts = Interlocked.Exchange(ref _observeCts, null);

                if (observeCts != null)
                {
                    try { observeCts.Dispose(); }
                    catch (ObjectDisposedException) { /* already disposed */ }
                }
            }

            internal CancellationToken ResetReconnectToken(CancellationToken outerToken)
            {
                CancellationTokenSource? oldCts;
                CancellationTokenSource? oldObserveCts;
                CancellationToken token;
                lock (_tokenLock)
                {
                    oldCts = Interlocked.Exchange(ref _ctsBacking, null);
                    oldObserveCts = Interlocked.Exchange(ref _observeCts, null);
                    if (_isSuppressed)
                    {
                        token = new CancellationToken(canceled: true);
                    }
                    else
                    {
                        var linked = CreateLinkedOrCanceledSource(outerToken);
                        _ctsBacking = linked;
                        ReconnectCts = linked;
                        token = linked.Token;
                    }
                }

                CancelAndDisposeCts(oldCts);
                CancelAndDisposeCts(oldObserveCts);
                return token;
            }

            internal CancellationToken GetOrCreateObserveToken(CancellationToken serviceToken)
            {
                lock (_tokenLock)
                {
                    if (_isSuppressed || !_isActive)
                        return new CancellationToken(canceled: true);

                    if (_observeCts != null)
                        return _observeCts.Token;

                    _observeCts = CreateLinkedOrCanceledSource(serviceToken);
                    return _observeCts.Token;
                }
            }

            private static CancellationTokenSource CreateLinkedOrCanceledSource(CancellationToken outerToken)
            {
                try
                {
                    return CancellationTokenSource.CreateLinkedTokenSource(outerToken);
                }
                catch (ObjectDisposedException)
                {
                    var fallback = new CancellationTokenSource();
                    fallback.Cancel();
                    return fallback;
                }
            }

            private static void CancelAndDisposeCts(CancellationTokenSource? cts)
            {
                if (cts == null) return;
                try { cts.Cancel(); }
                catch (ObjectDisposedException) { /* already disposed */ }
                try { cts.Dispose(); }
                catch (ObjectDisposedException) { /* already disposed */ }
            }

            private readonly System.Threading.Lock _tokenLock = new();
            private volatile bool _isActive = true;
            private volatile bool _isSuppressed;
            private CancellationTokenSource? _ctsBacking;
            private CancellationTokenSource? _observeCts;
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
                removed?.DeactivateAndCancel();
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
                session.MarkSuppressedAndCancel();
                AppServices.Logger?.Debug($"[AutoReconnect] Reconnection suppressed for '{username}'.");
            }
        }

        /// <summary>
        /// Suppresses all active sessions from reconnecting (e.g. user clicked 'Close Roblox' or app is shutting down).
        /// </summary>
        public void SuppressAll()
        {
            foreach (var session in _sessions.Values)
                session.MarkSuppressedAndCancel();
            AppServices.Logger.Info("[AutoReconnect] Suppressed reconnect for all active sessions.");
        }

        /// <summary>
        /// Clears all tracked sessions.
        /// </summary>
        public void Clear()
        {
            foreach (var s in _sessions.Values)
                s?.DeactivateAndCancel();
            _sessions.Clear();
        }

        private void SuppressAndRemoveIneligibleSession(ReconnectSession session, string sessionKey)
        {
            lock (_lock)
                session.MarkSuppressedAndCancel();
            if (!string.IsNullOrEmpty(sessionKey))
                _sessions.TryRemove(new KeyValuePair<string, ReconnectSession>(sessionKey, session));
            AppServices.Logger?.Warn($"[AutoReconnect] Skipping reconnect for ineligible account '{session.Account?.Username}'.");
        }

        private bool ShouldAbortReconnectTask(ReconnectSession session, string sessionKey)
        {
            if (_disposed || !IsEnabled || session.IsSuppressed ||
                !_sessions.TryGetValue(sessionKey, out var activeSession) || !ReferenceEquals(activeSession, session))
                return true;

            if (!IsAccountEligibleForReconnect(session.Account))
            {
                SuppressAndRemoveIneligibleSession(session, sessionKey);
                return true;
            }

            return false;
        }

        private void ApplyReconnectOutcomeState(ReconnectSession session, CancellationToken reconnectToken, bool succeeded)
        {
            lock (_lock)
            {
                if (_disposed || session.IsSuppressed || reconnectToken.IsCancellationRequested)
                    return;

                if (succeeded)
                {
                    session.LaunchTimeUtc = DateTime.UtcNow;
                    session.ObservedProcessExited = false;
                    session.ObservedExitCode = null;
                }
                session.NeedsRetry = !succeeded;
                session.IsActive = true;
            }
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
                    session.MarkSuppressedAndCancel();
                    AppServices.Logger?.Debug($"[AutoReconnect] Reconnection suppressed for PID {processId} ('{session.Account?.Username}').");
                }
            }
        }

        internal void OnJobProcessExited(int processId, bool abnormalExit)
        {
            if (processId <= 0 || _disposed) return;

            bool matchedAny = false;
            foreach (var kvp in _sessions)
            {
                var session = kvp.Value;
                if (session != null && session.ProcessId == processId && session.IsActive && !session.IsSuppressed)
                {
                    RecordObservedProcessExit(session, () => ResolveJobExitCode(processId, abnormalExit));
                    matchedAny = true;
                }
            }

            if (matchedAny)
                _ = System.Threading.Tasks.Task.Run(() => CheckSessionsCallback(null));
        }

        private static int? ResolveJobExitCode(int processId, bool abnormalExit)
        {
            if (abnormalExit)
                return -1;

            try
            {
                using var p = System.Diagnostics.Process.GetProcessById(processId);
                return p.HasExited ? p.ExitCode : 0;
            }
            catch (Exception ex)
            {
                AppServices.Logger?.Debug($"[AutoReconnect] ExitCode fallback to 0 on clean job exit: {ex.Message}");
                return 0;
            }
        }
    }
}

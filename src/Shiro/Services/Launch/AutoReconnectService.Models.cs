#nullable enable
using System;
using Shiro.Classes;

namespace Shiro.Services
{
    public sealed partial class AutoReconnectService
    {
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
                    Program.Logger?.Debug($"[AutoReconnect] Reconnection suppressed for PID {processId} ('{session.Account?.Username}').");
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
                    if (abnormalExit && !session.ObservedExitCode.HasValue)
                    {
                        session.ObservedExitCode = -1;
                    }
                    matchedAny = true;
                }
            }

            if (matchedAny)
            {
                CheckSessionsCallback(null);
            }
        }
    }
}

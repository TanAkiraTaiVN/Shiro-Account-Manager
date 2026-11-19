#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Shiro.Services
{
    public sealed partial class AutoReconnectService
    {
        /// <summary>
        /// Returns true only when a session is actively running, has not exited or queued a retry,
        /// has not reported an active disconnect, and has remained stable for at least <paramref name="stableResetWindow"/>.
        /// </summary>
        internal static bool ShouldResetRetryCount(ReconnectSession? session, DateTime nowUtc, TimeSpan stableResetWindow)
        {
            return session != null
                && session.IsActive
                && !session.IsSuppressed
                && session.ProcessId > 0
                && !session.NeedsRetry
                && !session.ObservedProcessExited
                && !session.LastDisconnectUtc.HasValue
                && (nowUtc - session.LaunchTimeUtc) >= stableResetWindow;
        }

        internal void RecordObservedProcessExit(ReconnectSession? session, Func<int?> resolveExitCode)
        {
            if (session == null)
                return;

            int? currentExitCode;
            lock (_lock)
            {
                currentExitCode = session.ObservedExitCode;
            }

            int? exitCode = currentExitCode ?? resolveExitCode();

            lock (_lock)
            {
                if (!session.ObservedExitCode.HasValue && exitCode.HasValue)
                    session.ObservedExitCode = exitCode;
                Interlocked.MemoryBarrier();
                session.ObservedProcessExited = true;
            }
        }

        internal (bool ProcessExited, int? ExitCode) ReadObservedProcessExitState(ReconnectSession session)
        {
            lock (_lock)
            {
                bool processExited = session.ObservedProcessExited;
                Interlocked.MemoryBarrier();
                int? exitCode = session.ObservedExitCode;
                return (processExited, exitCode);
            }
        }

        internal void CompleteObservedProcessExit(ReconnectSession? session, Func<int?> resolveExitCode)
        {
            if (session == null)
                return;

            RecordObservedProcessExit(session, resolveExitCode);

            if (!IsEnabled || _disposed || !session.IsActive || session.IsSuppressed)
                return;

            (_, int? exitCode) = ReadObservedProcessExitState(session);
            ReconnectDecision decision = Evaluate(
                session,
                processExited: true,
                exitCode: exitCode,
                nowUtc: DateTime.UtcNow,
                maxRetries: MaxRetries,
                gracePeriod: LaunchGracePeriod,
                disconnectTimeout: DisconnectTimeout);

            ApplyExitedDecision(session, decision);
        }

        private async Task ObserveProcessExitImmediateAsync(ReconnectSession session)
        {
            if (session == null || session.ProcessId <= 0)
                return;

            try
            {
                using var proc = Process.GetProcessById(session.ProcessId);
                if (session.ProcessCreationTicks > 0 &&
                    !RobloxProcessIdentityHelper.IsSameProcessInstance(session.ProcessId, session.ProcessCreationTicks, out _))
                {
                    CompleteObservedProcessExit(session, () => null);
                    return;
                }

                await proc.WaitForExitAsync().ConfigureAwait(false);
                CompleteObservedProcessExit(session, () => TryGetProcessExitCode(proc, session.ProcessId));
            }
            catch (Exception ex)
            {
                AppServices.Logger?.Debug("[AutoReconnectService] Process observation failed", ex);
            }
        }

        private static int? TryGetProcessExitCode(Process proc, int processId)
        {
            try
            {
                return proc.ExitCode;
            }
            catch (Exception ex)
            {
                AppServices.Logger?.Debug($"Get ExitCode for pid {processId} failed: {ex.Message}");
                return null;
            }
        }

        private void ApplyExitedDecision(ReconnectSession session, ReconnectDecision decision)
        {
            if (decision == ReconnectDecision.CloseCleanly)
            {
                session.IsSuppressed = true;
                session.IsActive = false;
                _sessions.TryRemove(GetSessionKey(session.Account), out _);
                AppServices.Logger?.Info($"[AutoReconnect] '{session.Account?.Username}' exited gracefully by user (ExitCode 0). Session closed.");
            }
            else if (decision == ReconnectDecision.Reconnect || decision == ReconnectDecision.GiveUp)
            {
                HandleProcessExit(session);
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

                if (session.ProcessId <= 0)
                {
                    if (session.NeedsRetry)
                        HandleProcessExit(session);
                    continue;
                }

                (bool processExited, int? exitCode) = ReadObservedProcessExitState(session);
                if (!processExited && session.ProcessCreationTicks > 0 &&
                    !RobloxProcessIdentityHelper.IsSameProcessInstance(session.ProcessId, session.ProcessCreationTicks, out _))
                {
                    processExited = true;
                }
                if (!processExited)
                {
                    runningPids ??= new HashSet<int>(RobloxProcessService.GetRunningRobloxPids());
                    if (!runningPids.Contains(session.ProcessId))
                        TryProbeProcessExit(session.ProcessId, out processExited, out exitCode);
                }

                // Reset retry counter only after continuous healthy play
                if (!processExited && session.RetryCount > 0 && ShouldResetRetryCount(session, now, StableResetWindow))
                    session.RetryCount = 0;

                ReconnectDecision decision = Evaluate(
                    session,
                    processExited,
                    exitCode,
                    now,
                    MaxRetries,
                    LaunchGracePeriod,
                    DisconnectTimeout);

                if (decision == ReconnectDecision.KillAndReconnect)
                {
                    TryKillStaleProcess(session);
                    HandleProcessExit(session);
                }
                else
                {
                    ApplyExitedDecision(session, decision);
                }
            }
        }

        private static void TryProbeProcessExit(int processId, out bool processExited, out int? exitCode)
        {
            processExited = false;
            exitCode = null;
            try
            {
                using var proc = Process.GetProcessById(processId);
                if (proc.HasExited)
                {
                    processExited = true;
                    exitCode = TryGetProcessExitCode(proc, processId);
                }
            }
            catch (ArgumentException)
            {
                // Process ID no longer exists
                processExited = true;
            }
            catch (Exception ex)
            {
                AppServices.Logger?.Debug($"[AutoReconnect] Error checking PID {processId}: {ex.Message}");
            }
        }

        private static void TryKillStaleProcess(ReconnectSession session)
        {
            try
            {
                if (session.ProcessCreationTicks > 0 &&
                    !RobloxProcessIdentityHelper.IsSameProcessInstance(session.ProcessId, session.ProcessCreationTicks, out _))
                {
                    return;
                }

                using var staleProc = Process.GetProcessById(session.ProcessId);
                if (!staleProc.HasExited)
                {
                    AppServices.Logger?.Warn($"[AutoReconnect] Terminating disconnected Roblox client PID {session.ProcessId} for '{session.Account?.Username}' before reconnecting...");
                    staleProc.Kill();
                    staleProc.WaitForExit(1000);
                }
            }
            catch (Exception ex)
            {
                AppServices.Logger?.Debug($"Kill stale disconnected client PID {session.ProcessId} failed: {ex.Message}");
            }
        }
    }
}

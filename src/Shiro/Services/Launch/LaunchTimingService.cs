#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Shiro.Services
{
    internal sealed class LaunchAttemptContext
    {
        public long AttemptStartTicks { get; } = Stopwatch.GetTimestamp();
        public bool RebindPerformed { get; set; }
        public long RebindMs { get; set; } = -1;
        public long TicketMs { get; set; } = -1;
        public long SpawnMs { get; set; } = -1;
        public int ProcessId { get; set; }
    }

    internal readonly record struct LaunchTimingEntry(
        int Index,
        int Total,
        string Username,
        int Pass,
        bool OpenHome,
        LaunchOutcome Outcome,
        ClientGameJoinState CaptchaState,
        bool RebindPerformed,
        long RebindMs,
        long TicketMs,
        long SpawnMs,
        long WindowMs,
        long JoinMs,
        long GapMs,
        int Recent10mCount);

    /// <summary>
    /// Records non-blocking [LaunchTiming] telemetry (spawn, window appearance, join/captcha state,
    /// HBA rebind duration, inter-launch gap, and rolling 10-minute launch density) per launched tab.
    /// </summary>
    internal sealed class LaunchTimingService
    {
        private static readonly TimeSpan RollingWindow = TimeSpan.FromMinutes(10);
        private static readonly AsyncLocal<LaunchAttemptContext?> _currentAttempt = new();

        private readonly Func<DateTimeOffset> _utcNow;
        private readonly IAppLogger? _loggerOverride;
        private readonly List<DateTimeOffset> _recentStarts = new();
        private readonly System.Threading.Lock _lock = new();
        private DateTimeOffset? _lastStartUtc;

        public static LaunchTimingService Default { get; } = new();

        public static LaunchAttemptContext? CurrentAttempt
        {
            get => _currentAttempt.Value;
            set => _currentAttempt.Value = value;
        }

        public LaunchTimingService(Func<DateTimeOffset>? utcNow = null, IAppLogger? logger = null)
        {
            _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
            _loggerOverride = logger;
        }

        public (long GapMs, int Recent10mCount) RecordStart(DateTimeOffset? timestampOverride = null)
        {
            DateTimeOffset now = timestampOverride ?? _utcNow();
            lock (_lock)
            {
                long gapMs = _lastStartUtc.HasValue
                    ? (long)Math.Max(0, (now - _lastStartUtc.Value).TotalMilliseconds)
                    : -1;
                _lastStartUtc = now;

                DateTimeOffset cutoff = now - RollingWindow;
                _recentStarts.RemoveAll(ts => ts < cutoff);
                _recentStarts.Add(now);

                return (gapMs, _recentStarts.Count);
            }
        }

        public void FormatAndLog(LaunchTimingEntry entry)
        {
            IAppLogger? logger = _loggerOverride ?? AppServices.Logger;
            string rebindText = entry.RebindPerformed
                ? $"yes({entry.RebindMs}ms)"
                : $"no({Math.Max(0, entry.RebindMs)}ms)";
            string mode = entry.OpenHome ? "Home" : "Game";

            string line =
                $"[LaunchTiming] [{entry.Index}/{entry.Total}] {entry.Username} pass={entry.Pass} mode={mode} " +
                $"outcome={entry.Outcome} captcha={entry.CaptchaState} rebind={rebindText} " +
                $"ticketMs={entry.TicketMs} spawnMs={entry.SpawnMs} windowMs={entry.WindowMs} " +
                $"joinMs={entry.JoinMs} gapMs={entry.GapMs} recent10m={entry.Recent10mCount}";

            logger?.Info(line);
        }

        public void ScheduleCompletionLog(
            LaunchTimingEntry initialEntry,
            LaunchAttemptContext? context,
            int targetPid,
            string? trackerId,
            CancellationToken cancellationToken)
        {
            bool canObserveLiveProcess =
                targetPid > 0 &&
                LaunchCoordinator.TestInspectProcessGameJoinStateOverride == null &&
                RobloxProcessService.IsProcessAlive(targetPid) &&
                (initialEntry.WindowMs < 0 || (!initialEntry.OpenHome && initialEntry.CaptchaState == ClientGameJoinState.Pending));

            if (!canObserveLiveProcess)
            {
                FormatAndLog(initialEntry);
                return;
            }

            AccountLaunchService.ObserveBackgroundTask(
                ObserveLiveMilestonesAsync(initialEntry, context, targetPid, trackerId, cancellationToken),
                "launch timing telemetry");
        }

        private async Task ObserveLiveMilestonesAsync(
            LaunchTimingEntry initialEntry,
            LaunchAttemptContext? context,
            int targetPid,
            string? trackerId,
            CancellationToken cancellationToken)
        {
            long startTicks = context?.AttemptStartTicks ?? Stopwatch.GetTimestamp();
            long windowMs = initialEntry.WindowMs;
            long joinMs = initialEntry.JoinMs;
            ClientGameJoinState state = initialEntry.CaptchaState;
            string? cachedLogPath = null;

            TimeSpan maxWait = TimeSpan.FromSeconds(25);
            long observeStart = Stopwatch.GetTimestamp();

            while (!cancellationToken.IsCancellationRequested && Stopwatch.GetElapsedTime(observeStart) < maxWait)
            {
                if (windowMs < 0 && RobloxWindowService.FindWindowForPid((uint)targetPid) != IntPtr.Zero)
                {
                    windowMs = (long)Stopwatch.GetElapsedTime(startTicks).TotalMilliseconds;
                }

                if (!initialEntry.OpenHome && state == ClientGameJoinState.Pending)
                {
                    state = LaunchCoordinator.InspectProcessGameJoinState(targetPid, trackerId, ref cachedLogPath);
                    if (state != ClientGameJoinState.Pending)
                    {
                        joinMs = (long)Stopwatch.GetElapsedTime(startTicks).TotalMilliseconds;
                    }
                }

                bool windowDone = windowMs >= 0;
                bool joinDone = initialEntry.OpenHome || state != ClientGameJoinState.Pending;
                if ((windowDone && joinDone) || !RobloxProcessService.IsProcessAlive(targetPid))
                {
                    if (!RobloxProcessService.IsProcessAlive(targetPid) && state == ClientGameJoinState.Pending)
                        state = ClientGameJoinState.ProcessExited;
                    break;
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

            FormatAndLog(initialEntry with
            {
                WindowMs = windowMs,
                JoinMs = joinMs,
                CaptchaState = state
            });
        }
    }
}

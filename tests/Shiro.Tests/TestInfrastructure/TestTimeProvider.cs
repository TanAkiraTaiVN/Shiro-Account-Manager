#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Shiro.Tests.TestInfrastructure
{
    internal sealed class TestTimeProvider : TimeProvider
    {
        private readonly Lock _sync = new();
        private readonly List<TestTimer> _timers = new();
        private readonly TimeZoneInfo _localTimeZone;
        private DateTimeOffset _utcNow;

        public TestTimeProvider(DateTimeOffset? initialUtcNow = null, TimeZoneInfo? localTimeZone = null)
        {
            _utcNow = (initialUtcNow ?? new DateTimeOffset(2026, 11, 24, 8, 0, 0, TimeSpan.Zero)).ToUniversalTime();
            _localTimeZone = localTimeZone ?? TimeZoneInfo.Utc;
        }

        public override TimeZoneInfo LocalTimeZone => _localTimeZone;

        public override DateTimeOffset GetUtcNow()
        {
            lock (_sync)
                return _utcNow;
        }

        public void SetUtcNow(DateTimeOffset utcNow)
        {
            lock (_sync)
                _utcNow = utcNow.ToUniversalTime();
        }

        public void Advance(TimeSpan delta)
        {
            if (delta < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(delta));

            DateTimeOffset targetUtc;
            lock (_sync)
                targetUtc = _utcNow + delta;

            while (true)
            {
                List<(TestTimer Timer, TimerCallback Callback, object? State)> due = new();
                DateTimeOffset nextBoundary = targetUtc;

                lock (_sync)
                {
                    foreach (TestTimer timer in _timers)
                    {
                        if (!timer.IsDisposed && timer.NextTriggerUtc.HasValue && timer.NextTriggerUtc.Value < nextBoundary)
                            nextBoundary = timer.NextTriggerUtc.Value;
                    }

                    if (nextBoundary > _utcNow)
                        _utcNow = nextBoundary;

                    foreach (TestTimer timer in _timers)
                    {
                        if (timer.IsDisposed || !timer.NextTriggerUtc.HasValue || timer.NextTriggerUtc.Value > _utcNow)
                            continue;

                        timer.AdvanceSchedule(_utcNow);
                        due.Add((timer, timer.Callback, timer.State));
                    }
                }

                foreach (var item in due)
                {
                    if (!item.Timer.IsDisposed)
                        item.Callback(item.State);
                }

                lock (_sync)
                {
                    bool anyMoreDue = false;
                    foreach (TestTimer timer in _timers)
                    {
                        if (!timer.IsDisposed && timer.NextTriggerUtc.HasValue && timer.NextTriggerUtc.Value <= targetUtc)
                        {
                            anyMoreDue = true;
                            break;
                        }
                    }

                    if (!anyMoreDue)
                    {
                        if (_utcNow < targetUtc)
                            _utcNow = targetUtc;
                        break;
                    }
                }
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ArgumentNullException.ThrowIfNull(callback);
            var timer = new TestTimer(this, callback, state);
            lock (_sync)
            {
                _timers.Add(timer);
            }

            ScheduleTimer(timer, dueTime, period);
            return timer;
        }

        internal void ScheduleTimer(TestTimer timer, TimeSpan dueTime, TimeSpan period)
        {
            bool fireImmediately = false;
            lock (_sync)
            {
                if (timer.IsDisposed)
                    return;

                timer.Period = period;
                if (dueTime == Timeout.InfiniteTimeSpan)
                {
                    timer.NextTriggerUtc = null;
                }
                else if (dueTime <= TimeSpan.Zero)
                {
                    timer.AdvanceSchedule(_utcNow);
                    fireImmediately = true;
                }
                else
                {
                    timer.NextTriggerUtc = _utcNow + dueTime;
                }
            }

            if (fireImmediately)
                ThreadPool.QueueUserWorkItem(_ => { if (!timer.IsDisposed) timer.Callback(timer.State); });
        }

        internal void RemoveTimer(TestTimer timer)
        {
            lock (_sync)
                _timers.Remove(timer);
        }

        internal sealed class TestTimer : ITimer
        {
            private readonly TestTimeProvider _owner;
            private int _disposed;

            public TestTimer(TestTimeProvider owner, TimerCallback callback, object? state)
            {
                _owner = owner;
                Callback = callback;
                State = state;
            }

            public TimerCallback Callback { get; }
            public object? State { get; }
            public DateTimeOffset? NextTriggerUtc { get; set; }
            public TimeSpan Period { get; set; }
            public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

            public void AdvanceSchedule(DateTimeOffset currentUtc)
            {
                NextTriggerUtc = Period > TimeSpan.Zero && Period != Timeout.InfiniteTimeSpan
                    ? currentUtc + Period
                    : null;
            }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (IsDisposed)
                    return false;

                _owner.ScheduleTimer(this, dueTime, period);
                return true;
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    NextTriggerUtc = null;
                    _owner.RemoveTimer(this);
                }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}

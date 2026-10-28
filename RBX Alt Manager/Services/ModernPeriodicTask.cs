#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Shiro.Services
{
    /// <summary>
    /// Modern periodic worker backed by .NET's PeriodicTimer.
    /// Completely eliminates timer re-entrancy and thread-pool exhaustion
    /// by ensuring the next tick is only awaited after the previous callback completes.
    /// </summary>
    public sealed class ModernPeriodicTask : IDisposable
    {
        private readonly Func<CancellationToken, Task> _action;
        private readonly CancellationTokenSource _lifecycleCts = new CancellationTokenSource();
        private CancellationTokenSource? _loopCts;
        private TimeSpan _interval;
        private bool _enabled;
        private readonly System.Threading.Lock _gate = new();

        public ModernPeriodicTask(TimeSpan interval, Func<CancellationToken, Task> action, bool enabled = true)
        {
            _interval = interval > TimeSpan.Zero ? interval : TimeSpan.FromSeconds(5);
            _action = action ?? throw new ArgumentNullException(nameof(action));
            if (enabled)
            {
                Enabled = true;
            }
        }

        public bool Enabled
        {
            get
            {
                lock (_gate) return _enabled;
            }
            set
            {
                lock (_gate)
                {
                    if (_enabled == value) return;
                    _enabled = value;
                    if (_enabled)
                        StartLoop();
                    else
                        StopLoop();
                }
            }
        }

        public TimeSpan Interval
        {
            get
            {
                lock (_gate) return _interval;
            }
            set
            {
                lock (_gate)
                {
                    _interval = value > TimeSpan.Zero ? value : TimeSpan.FromSeconds(5);
                    if (_enabled)
                    {
                        StopLoop();
                        StartLoop();
                    }
                }
            }
        }

        public void Restart(TimeSpan newInterval)
        {
            lock (_gate)
            {
                _interval = newInterval > TimeSpan.Zero ? newInterval : TimeSpan.FromSeconds(5);
                StopLoop();
                if (_enabled)
                    StartLoop();
            }
        }

        private void StartLoop()
        {
            if (_lifecycleCts.IsCancellationRequested) return;

            StopLoop();
            _loopCts = CancellationTokenSource.CreateLinkedTokenSource(_lifecycleCts.Token);
            var token = _loopCts.Token;
            var currentInterval = _interval;

            _ = Task.Run(async () =>
            {
                try
                {
                    using var timer = new PeriodicTimer(currentInterval);
                    while (!token.IsCancellationRequested && await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                    {
                        try
                        {
                            await _action(token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (token.IsCancellationRequested)
                        {
                            break;
                        }
                        catch (Exception ex)
                        {
                            Program.Logger?.Error($"PeriodicTask error: {ex.Message}");
                        }
                    }
                }
                catch (OperationCanceledException) { }
            }, token);
        }

        private void StopLoop()
        {
            try
            {
                _loopCts?.Cancel();
                _loopCts?.Dispose();
            }
            catch (Exception) { }
            _loopCts = null;
        }

        public void Stop() => Enabled = false;
        public void Start() => Enabled = true;

        public void Dispose()
        {
            lock (_gate)
            {
                _enabled = false;
                StopLoop();
                try
                {
                    _lifecycleCts.Cancel();
                    _lifecycleCts.Dispose();
                }
                catch (Exception) { }
            }
        }
    }
}

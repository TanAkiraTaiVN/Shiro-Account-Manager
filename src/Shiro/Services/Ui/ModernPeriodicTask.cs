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
        private CancellationTokenSource? _loopCts;
        private TimeSpan _interval;
        private bool _enabled;
        private bool _disposed;
        private readonly System.Threading.Lock _gate = new();

        public ModernPeriodicTask(TimeSpan interval, Func<CancellationToken, Task> action, bool enabled = true)
        {
            _interval = NormalizeInterval(interval);
            _action = action ?? throw new ArgumentNullException(nameof(action));
            if (enabled)
            {
                Enabled = true;
            }
        }

        private static TimeSpan NormalizeInterval(TimeSpan value)
            => value > TimeSpan.Zero ? value : TimeSpan.FromSeconds(5);

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
                    if (_disposed || _enabled == value) return;
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
                    if (_disposed) return;
                    _interval = NormalizeInterval(value);
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
                if (_disposed) return;
                _interval = NormalizeInterval(newInterval);
                StopLoop();
                if (_enabled)
                    StartLoop();
            }
        }

        private void StartLoop()
        {
            if (_disposed || !_enabled) return;

            StopLoop();
            var loopCts = new CancellationTokenSource();
            _loopCts = loopCts;
            var token = loopCts.Token;
            var currentInterval = _interval;

            _ = Task.Run(async () =>
            {
                try
                {
                    using (loopCts)
                    using (var timer = new PeriodicTimer(currentInterval))
                    {
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
                                AppServices.Logger?.Error($"PeriodicTask error: {ex.Message}");
                            }
                        }
                    }
                }
                catch (OperationCanceledException) { /* periodic loop stopped */ }
                finally
                {
                    lock (_gate)
                    {
                        if (ReferenceEquals(_loopCts, loopCts))
                            _loopCts = null;
                    }
                }
            });
        }

        private void StopLoop()
        {
            CancellationTokenSource? ctsToCancel = _loopCts;
            _loopCts = null;
            if (ctsToCancel == null) return;

            try
            {
                ctsToCancel.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Worker already exited and disposed its own CancellationTokenSource.
            }
            catch (Exception ex)
            {
                AppServices.Logger?.Debug($"ModernPeriodicTask.StopLoop: {ex.Message}");
            }
        }

        public void Stop() => Enabled = false;
        public void Start() => Enabled = true;

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _enabled = false;
                StopLoop();
            }
        }
    }
}

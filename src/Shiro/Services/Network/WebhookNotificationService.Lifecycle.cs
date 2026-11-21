#nullable enable
using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Shiro.Services
{
    public static partial class WebhookNotificationService
    {
        private static readonly System.Threading.Lock _lifecycleLock = new();
        private static Channel<WebhookAlert>? _alertChannel;
        private static CancellationTokenSource? _loopCts;
        private static Task? _loopTask;

        private static Channel<WebhookAlert> CreateChannel() =>
            Channel.CreateBounded<WebhookAlert>(
                new BoundedChannelOptions(100)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                    SingleWriter = false
                });

        private static ChannelWriter<WebhookAlert> EnsureLoopStartedAndGetWriter()
        {
            lock (_lifecycleLock)
            {
                if (_alertChannel == null || _loopTask == null || _loopTask.IsCompleted)
                {
                    _loopCts?.Dispose();
                    _alertChannel = CreateChannel();
                    _loopCts = new CancellationTokenSource();
                    var channel = _alertChannel;
                    var cts = _loopCts;
                    _loopTask = Task.Run(() => ProcessAlertsLoopAsync(channel.Reader, cts.Token));
                }

                return _alertChannel.Writer;
            }
        }

        /// <summary>
        /// Completes the active alert channel writer, waits up to the bounded drain timeout
        /// for pending alerts to flush, cancels if still pending, and resets lifecycle state
        /// so subsequent alerts cleanly restart the dispatch loop.
        /// </summary>
        public static void Shutdown(int drainTimeoutMs = 1000)
        {
            Channel<WebhookAlert>? channel;
            CancellationTokenSource? cts;
            Task? loopTask;

            lock (_lifecycleLock)
            {
                channel = _alertChannel;
                cts = _loopCts;
                loopTask = _loopTask;
                _alertChannel = null;
                _loopCts = null;
                _loopTask = null;
            }

            channel?.Writer.TryComplete();

            int boundedTimeoutMs = Math.Clamp(drainTimeoutMs, 50, 5000);
            if (loopTask != null && !loopTask.IsCompleted)
            {
                try
                {
                    if (!loopTask.Wait(boundedTimeoutMs))
                    {
                        cts?.Cancel();
                        loopTask.Wait(200);
                    }
                }
                catch (AggregateException ex)
                {
                    _logger.Debug($"[Webhook] Shutdown drain ended with exception: {ex.InnerException?.Message ?? ex.Message}");
                }
                catch (OperationCanceledException ex)
                {
                    _logger.Debug($"[Webhook] Shutdown drain canceled: {ex.Message}");
                }
                catch (ObjectDisposedException ex)
                {
                    _logger.Debug($"[Webhook] Shutdown CTS already disposed: {ex.Message}");
                }
            }

            if (cts != null)
            {
                try { cts.Cancel(); }
                catch (ObjectDisposedException ex) { _logger.Debug($"[Webhook] Shutdown CTS cancel skipped: {ex.Message}"); }

                if (loopTask == null || loopTask.IsCompleted)
                {
                    cts.Dispose();
                }
                else
                {
                    _ = loopTask.ContinueWith(
                        static (_, state) => SafeExecuteHelper.Try(() => ((CancellationTokenSource)state!).Dispose(), "WebhookNotificationService.DisposeCts"),
                        cts,
                        TaskScheduler.Default);
                }
            }
        }

        private static async Task ProcessAlertsLoopAsync(ChannelReader<WebhookAlert> reader, CancellationToken ct)
        {
            try
            {
                while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
                {
                    while (reader.TryRead(out var alert))
                    {
                        if (alert != null)
                        {
                            await DispatchSingleAlertAsync(alert, ct).ConfigureAwait(false);
                        }
                    }
                }
            }
            catch (OperationCanceledException ex)
            {
                _logger.Debug($"[Webhook] Alert processing loop canceled: {ex.Message}");
            }
            catch (Exception ex)
            {
                _logger.Debug($"[Webhook] Alert processing loop ended: {ex.Message}");
            }
        }
    }
}

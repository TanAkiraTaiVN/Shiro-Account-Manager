#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Shiro.Services
{
    /// <summary>
    /// Condition-driven asynchronous polling helper that replaces brittle fixed <see cref="Task.Delay(int)"/>
    /// waits across browser automation, WebView2 session capture, and process readiness checks.
    /// Completes immediately as soon as the condition is satisfied while enforcing an upper-bound timeout.
    /// </summary>
    public static class AsyncWaitHelper
    {
        public static async Task<bool> WaitUntilAsync(
            Func<bool> condition,
            TimeSpan timeout,
            TimeSpan pollInterval,
            CancellationToken cancellationToken = default,
            TimeProvider? timeProvider = null)
        {
            ArgumentNullException.ThrowIfNull(condition);

            if (condition())
                return true;

            if (timeout <= TimeSpan.Zero)
                return false;

            TimeProvider provider = timeProvider ?? TimeProvider.System;
            TimeSpan step = pollInterval <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(25) : pollInterval;
            long startTimestamp = provider.GetTimestamp();

            while (provider.GetElapsedTime(startTimestamp) < timeout)
            {
                cancellationToken.ThrowIfCancellationRequested();

                TimeSpan remaining = timeout - provider.GetElapsedTime(startTimestamp);
                if (remaining <= TimeSpan.Zero)
                    break;

                TimeSpan delay = step < remaining ? step : remaining;
                await Task.Delay(delay, provider, cancellationToken);

                if (condition())
                    return true;
            }

            return condition();
        }

        public static async Task<bool> WaitUntilAsync(
            Func<Task<bool>> asyncCondition,
            TimeSpan timeout,
            TimeSpan pollInterval,
            CancellationToken cancellationToken = default,
            TimeProvider? timeProvider = null)
        {
            ArgumentNullException.ThrowIfNull(asyncCondition);

            if (await asyncCondition())
                return true;

            if (timeout <= TimeSpan.Zero)
                return false;

            TimeProvider provider = timeProvider ?? TimeProvider.System;
            TimeSpan step = pollInterval <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(25) : pollInterval;
            long startTimestamp = provider.GetTimestamp();

            while (provider.GetElapsedTime(startTimestamp) < timeout)
            {
                cancellationToken.ThrowIfCancellationRequested();

                TimeSpan remaining = timeout - provider.GetElapsedTime(startTimestamp);
                if (remaining <= TimeSpan.Zero)
                    break;

                TimeSpan delay = step < remaining ? step : remaining;
                await Task.Delay(delay, provider, cancellationToken);

                if (await asyncCondition())
                    return true;
            }

            return await asyncCondition();
        }
    }
}

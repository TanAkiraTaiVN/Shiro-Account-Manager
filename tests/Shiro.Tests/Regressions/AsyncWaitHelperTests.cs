#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    public class AsyncWaitHelperTests
    {
        [Fact]
        public async Task WaitUntilAsync_SyncConditionAlreadyTrue_ReturnsImmediately()
        {
            int calls = 0;
            bool ok = await AsyncWaitHelper.WaitUntilAsync(
                () => { calls++; return true; },
                TimeSpan.FromSeconds(5),
                TimeSpan.FromMilliseconds(50));

            Assert.True(ok);
            Assert.Equal(1, calls);
        }

        [Fact]
        public async Task WaitUntilAsync_SyncConditionBecomesTrue_ReturnsTrue()
        {
            int attempts = 0;
            bool ok = await AsyncWaitHelper.WaitUntilAsync(
                () => ++attempts >= 3,
                TimeSpan.FromSeconds(2),
                TimeSpan.FromMilliseconds(15));

            Assert.True(ok);
            Assert.True(attempts >= 3);
        }

        [Fact]
        public async Task WaitUntilAsync_SyncConditionTimeout_ReturnsFalse()
        {
            bool ok = await AsyncWaitHelper.WaitUntilAsync(
                () => false,
                TimeSpan.FromMilliseconds(60),
                TimeSpan.FromMilliseconds(15));

            Assert.False(ok);
        }

        [Fact]
        public async Task WaitUntilAsync_ZeroTimeoutAndFalse_ReturnsFalseImmediately()
        {
            bool ok = await AsyncWaitHelper.WaitUntilAsync(
                () => false,
                TimeSpan.Zero,
                TimeSpan.FromMilliseconds(10));

            Assert.False(ok);
        }

        [Fact]
        public async Task WaitUntilAsync_AsyncConditionBecomesTrue_ReturnsTrue()
        {
            int attempts = 0;
            bool ok = await AsyncWaitHelper.WaitUntilAsync(
                async () =>
                {
                    await Task.Yield();
                    return ++attempts >= 2;
                },
                TimeSpan.FromSeconds(2),
                TimeSpan.FromMilliseconds(15));

            Assert.True(ok);
            Assert.True(attempts >= 2);
        }

        [Fact]
        public async Task WaitUntilAsync_AsyncConditionTimeout_ReturnsFalse()
        {
            bool ok = await AsyncWaitHelper.WaitUntilAsync(
                () => Task.FromResult(false),
                TimeSpan.FromMilliseconds(50),
                TimeSpan.FromMilliseconds(15));

            Assert.False(ok);
        }

        [Fact]
        public async Task WaitUntilAsync_Cancelled_ThrowsOperationCanceledException()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                AsyncWaitHelper.WaitUntilAsync(
                    () => false,
                    TimeSpan.FromSeconds(5),
                    TimeSpan.FromMilliseconds(15),
                    cts.Token));
        }

        [Fact]
        public async Task WaitUntilAsync_NullCondition_ThrowsArgumentNullException()
        {
            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                AsyncWaitHelper.WaitUntilAsync((Func<bool>)null!, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(10)));

            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                AsyncWaitHelper.WaitUntilAsync((Func<Task<bool>>)null!, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(10)));
        }

        [Fact]
        public async Task WaitUntilAsync_SyncConditionWithUiSynchronizationContext_InvokesConditionOnUiThreadEveryPoll()
        {
            using var uiContext = new DedicatedThreadSynchronizationContext();
            bool ok = await uiContext.RunAsync(async () =>
            {
                int uiThreadId = Environment.CurrentManagedThreadId;
                int polls = 0;

                return await AsyncWaitHelper.WaitUntilAsync(
                    () =>
                    {
                        if (Environment.CurrentManagedThreadId != uiThreadId)
                            throw new InvalidOperationException("CoreWebView2 members can only be accessed from the UI thread.");

                        return ++polls >= 3;
                    },
                    TimeSpan.FromSeconds(2),
                    TimeSpan.FromMilliseconds(15));
            });

            Assert.True(ok);
        }

        [Fact]
        public async Task WaitUntilAsync_AsyncConditionWithUiSynchronizationContext_InvokesConditionOnUiThreadEveryPoll()
        {
            using var uiContext = new DedicatedThreadSynchronizationContext();
            bool ok = await uiContext.RunAsync(async () =>
            {
                int uiThreadId = Environment.CurrentManagedThreadId;
                int polls = 0;

                return await AsyncWaitHelper.WaitUntilAsync(
                    async () =>
                    {
                        if (Environment.CurrentManagedThreadId != uiThreadId)
                            throw new InvalidOperationException("CoreWebView2 members can only be accessed from the UI thread.");

                        await Task.Yield();

                        if (Environment.CurrentManagedThreadId != uiThreadId)
                            throw new InvalidOperationException("CoreWebView2 members can only be accessed from the UI thread.");

                        return ++polls >= 3;
                    },
                    TimeSpan.FromSeconds(2),
                    TimeSpan.FromMilliseconds(15));
            });

            Assert.True(ok);
        }

        private sealed class DedicatedThreadSynchronizationContext : SynchronizationContext, IDisposable
        {
            private readonly System.Collections.Concurrent.BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
            private readonly Thread _thread;

            public DedicatedThreadSynchronizationContext()
            {
                _thread = new Thread(MessagePump)
                {
                    IsBackground = true,
                    Name = "TestUiThread"
                };
                _thread.Start();
            }

            public override void Post(SendOrPostCallback d, object? state)
            {
                if (!_queue.IsAddingCompleted)
                    _queue.Add((d, state));
            }

            public Task<T> RunAsync<T>(Func<Task<T>> action)
            {
                var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
                Post(async _ =>
                {
                    try
                    {
                        T result = await action();
                        tcs.TrySetResult(result);
                    }
                    catch (Exception ex)
                    {
                        tcs.TrySetException(ex);
                    }
                }, null);
                return tcs.Task;
            }

            private void MessagePump()
            {
                SetSynchronizationContext(this);
                foreach (var (callback, state) in _queue.GetConsumingEnumerable())
                {
                    callback(state);
                }
            }

            public void Dispose()
            {
                _queue.CompleteAdding();
                _thread.Join(TimeSpan.FromSeconds(2));
                _queue.Dispose();
            }
        }
    }
}

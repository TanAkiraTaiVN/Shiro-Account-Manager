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
    }
}

#nullable enable
using System;
using System.Threading.Tasks;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public sealed class P0UiAndViewModelRegressionTests : IDisposable
    {
        private readonly Action<Action>? _originalDispatcher = AppDispatcher.CustomDispatcher;

        public void Dispose()
        {
            AppDispatcher.CustomDispatcher = _originalDispatcher;
        }

        [Fact]
        public async Task AppDispatcher_Headless_InvokeAsyncAction_ThrowsOnceAndExecutesOnce()
        {
            AppDispatcher.CustomDispatcher = null;
            int invocationCount = 0;

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                AppDispatcher.InvokeAsync(() =>
                {
                    invocationCount++;
                    throw new InvalidOperationException("boom");
                }));

            Assert.Equal("boom", ex.Message);
            Assert.Equal(1, invocationCount);
        }

        [Fact]
        public async Task AppDispatcher_Headless_InvokeAsyncFunc_ThrowsOnceAndExecutesOnce()
        {
            AppDispatcher.CustomDispatcher = null;
            int invocationCount = 0;

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                AppDispatcher.InvokeAsync<int>(() =>
                {
                    invocationCount++;
                    throw new InvalidOperationException("boom-func");
                }));

            Assert.Equal("boom-func", ex.Message);
            Assert.Equal(1, invocationCount);
        }

        [Fact]
        public void AppDispatcher_Headless_Post_ThrowsOnceAndExecutesOnce()
        {
            AppDispatcher.CustomDispatcher = null;
            int invocationCount = 0;

            var ex = Assert.Throws<InvalidOperationException>(() =>
                AppDispatcher.Post(() =>
                {
                    invocationCount++;
                    throw new InvalidOperationException("boom-post");
                }));

            Assert.Equal("boom-post", ex.Message);
            Assert.Equal(1, invocationCount);
        }

        [Fact]
        public void AppDispatcher_DispatchOrFallback_WhenDispatcherActiveAndDispatchThrows_DoesNotInvokeFallback()
        {
            int dispatchCount = 0;
            int fallbackCount = 0;

            AppDispatcher.DispatchOrFallback(
                hasActiveDispatcher: true,
                dispatch: () =>
                {
                    dispatchCount++;
                    throw new InvalidOperationException("dispatcher-boom");
                },
                fallback: () => fallbackCount++);

            Assert.Equal(1, dispatchCount);
            Assert.Equal(0, fallbackCount);
        }

        [Fact]
        public void AppDispatcher_DispatchOrFallback_WhenNoActiveDispatcher_InvokesFallbackOnce()
        {
            int dispatchCount = 0;
            int fallbackCount = 0;

            AppDispatcher.DispatchOrFallback(
                hasActiveDispatcher: false,
                dispatch: () => dispatchCount++,
                fallback: () => fallbackCount++);

            Assert.Equal(0, dispatchCount);
            Assert.Equal(1, fallbackCount);
        }

        [Fact]
        public async Task AppDispatcher_DispatchOrFallbackAsync_WhenDispatcherActiveAndDispatchThrows_PropagatesAndDoesNotInvokeFallback()
        {
            int dispatchCount = 0;
            int fallbackCount = 0;

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                AppDispatcher.DispatchOrFallbackAsync(
                    hasActiveDispatcher: true,
                    dispatch: () =>
                    {
                        dispatchCount++;
                        throw new InvalidOperationException("async-boom");
                    },
                    fallback: () =>
                    {
                        fallbackCount++;
                        return Task.CompletedTask;
                    }));

            Assert.Equal("async-boom", ex.Message);
            Assert.Equal(1, dispatchCount);
            Assert.Equal(0, fallbackCount);
        }

        [Fact]
        public async Task AppDispatcher_DispatchOrFallbackAsyncGeneric_WhenDispatcherActiveAndDispatchThrows_PropagatesAndDoesNotInvokeFallback()
        {
            int dispatchCount = 0;
            int fallbackCount = 0;

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                AppDispatcher.DispatchOrFallbackAsync<int>(
                    hasActiveDispatcher: true,
                    dispatch: () =>
                    {
                        dispatchCount++;
                        throw new InvalidOperationException("async-generic-boom");
                    },
                    fallback: () =>
                    {
                        fallbackCount++;
                        return Task.FromResult(42);
                    }));

            Assert.Equal("async-generic-boom", ex.Message);
            Assert.Equal(1, dispatchCount);
            Assert.Equal(0, fallbackCount);
        }
    }
}

#nullable enable
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public sealed class GameThumbnailServiceTests : IDisposable
    {
        private static readonly byte[] OneByOnePngBytes = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

        private readonly long _testPlaceId = 98765432101L;

        public GameThumbnailServiceTests()
        {
            GameThumbnailService.ClearCache();
            DeleteDiskFileIfExists(_testPlaceId);
        }

        public void Dispose()
        {
            GameThumbnailService.ClearCache();
            DeleteDiskFileIfExists(_testPlaceId);
        }

        private static void DeleteDiskFileIfExists(long placeId)
        {
            string? diskPath = GameThumbnailService.GetDiskCachePath(placeId);
            if (!string.IsNullOrEmpty(diskPath) && File.Exists(diskPath))
            {
                try { File.Delete(diskPath); }
                catch (IOException) { }
            }
        }

        [AvaloniaFact]
        public void TryGetCachedBitmap_DoesNotTouchDiskOnMiss()
        {
            const long placeId = 987654321L;
            string? diskPath = GameThumbnailService.GetDiskCachePath(placeId);
            Assert.NotNull(diskPath);

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(diskPath!)!);
                File.WriteAllBytes(diskPath!, OneByOnePngBytes);

                bool found = GameThumbnailService.TryGetCachedBitmap(placeId, out Bitmap? bitmap);

                Assert.False(found);
                Assert.Null(bitmap);
            }
            finally
            {
                DeleteDiskFileIfExists(placeId);
            }
        }

        [AvaloniaFact]
        public async Task GetThumbnailBitmapAsync_DecodesDiskCacheOffCallingUiSynchronizationContext_AndPopulatesMemoryCache()
        {
            string? diskPath = GameThumbnailService.GetDiskCachePath(_testPlaceId);
            Assert.NotNull(diskPath);
            Directory.CreateDirectory(Path.GetDirectoryName(diskPath!)!);
            await File.WriteAllBytesAsync(diskPath!, OneByOnePngBytes);

            Assert.False(GameThumbnailService.TryGetCachedBitmap(_testPlaceId, out _));

            int uiThreadId = Environment.CurrentManagedThreadId;
            var prevSyncCtx = SynchronizationContext.Current;
            int postedBackToSyncCtx = 0;
            var trackingSyncCtx = new TrackingSynchronizationContext(
                prevSyncCtx,
                () => Interlocked.Increment(ref postedBackToSyncCtx));

            int postsDuringFetch = 0;
            using var done = new ManualResetEventSlim(false);
            SynchronizationContext.SetSynchronizationContext(trackingSyncCtx);
            Task<Bitmap?> fetchTask;
            try
            {
                fetchTask = GameThumbnailService.GetThumbnailBitmapAsync(_testPlaceId);
                _ = fetchTask.ContinueWith(_ =>
                {
                    postsDuringFetch = Volatile.Read(ref postedBackToSyncCtx);
                    done.Set();
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(prevSyncCtx);
            }

            try
            {
                Bitmap? bmp = await fetchTask;
                Assert.True(done.Wait(TimeSpan.FromSeconds(2)));
                Assert.NotNull(bmp);
                Assert.Equal(0, postsDuringFetch);
                Assert.True(GameThumbnailService.TryGetCachedBitmap(_testPlaceId, out Bitmap? cached));
                Assert.Same(bmp, cached);
                Assert.Equal(uiThreadId, Environment.CurrentManagedThreadId);
            }
            finally
            {
                Dispatcher.UIThread.RunJobs();
            }
        }

        [AvaloniaFact]
        public async Task GetThumbnailBitmapAsync_RespectsPreCancelledToken()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => GameThumbnailService.GetThumbnailBitmapAsync(_testPlaceId, cancellationToken: cts.Token));
        }

        private sealed class TrackingSynchronizationContext : SynchronizationContext
        {
            private readonly SynchronizationContext? _inner;
            private readonly Action _onPost;

            public TrackingSynchronizationContext(SynchronizationContext? inner, Action onPost)
            {
                _inner = inner;
                _onPost = onPost;
            }

            public override void Post(SendOrPostCallback d, object? state)
            {
                _onPost();
                if (_inner != null)
                    _inner.Post(d, state);
                else
                    ThreadPool.QueueUserWorkItem(_ => d(state));
            }
        }
    }
}

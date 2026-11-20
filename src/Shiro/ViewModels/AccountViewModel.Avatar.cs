#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Shiro.Services;

namespace Shiro.ViewModels
{
    public partial class AccountViewModel
    {
        private const int MaxAvatarBitmapCacheEntries = 2048;
        private static readonly ConcurrentDictionary<(long UserId, int Generation), Task<(string Path, Bitmap? Bitmap)>> _inFlightAvatarLoads = new();
        private static readonly LruCache<long, Bitmap> _avatarBitmapCache = new(MaxAvatarBitmapCacheEntries, disposeOnEvict: false);
        private static readonly SemaphoreSlim _diskDecodeGate = new(6, 6);
        private static readonly System.Threading.Lock _avatarCacheLock = new();
        private static int _avatarCacheGeneration = 1;

        internal static int InFlightAvatarLoadCountForTesting => _inFlightAvatarLoads.Count;

        private readonly System.Threading.Lock _avatarStateLock = new();
        private string? _avatarUrl;
        private Bitmap? _cachedAvatarBitmap;
        private long _cachedAvatarUserId;
        private int _cachedAvatarGeneration;
        private int _avatarLoadState; // 0 = Unchecked, 1 = Loading, 2 = Resolved

        private static void ScheduleBitmapDisposal(IEnumerable<Bitmap?>? bitmaps)
        {
            if (bitmaps == null) return;
            var distinct = new List<Bitmap>();
            foreach (var bmp in bitmaps)
            {
                if (bmp != null && !distinct.Contains(bmp))
                    distinct.Add(bmp);
            }
            if (distinct.Count == 0) return;

            AppDispatcher.Post(() => AppDispatcher.PostBackground(() =>
            {
                foreach (var bmp in distinct)
                {
                    SafeExecuteHelper.Try(() => bmp.Dispose(), "AccountViewModel.DisposeBitmap");
                }
            }));
        }

        public static void ClearAvatarMemoryCache()
        {
            List<Bitmap> drained;
            lock (_avatarCacheLock)
            {
                Interlocked.Increment(ref _avatarCacheGeneration);
                drained = _avatarBitmapCache.ClearAndDrain();
                _inFlightAvatarLoads.Clear();
            }
            ScheduleBitmapDisposal(drained);
        }

        /// <summary>
        /// Pure O(1) non-blocking memory read for smooth 120+ FPS scrolling.
        /// Never touches disk I/O or decodes PNG streams on the UI thread.
        /// </summary>
        public Bitmap? AvatarImage
        {
            get
            {
                long userId = UserId;
                if (userId <= 0)
                    return null;

                int gen = Volatile.Read(ref _avatarCacheGeneration);
                lock (_avatarStateLock)
                {
                    if (_cachedAvatarUserId == userId && _cachedAvatarGeneration == gen)
                    {
                        if (_cachedAvatarBitmap != null)
                            return _cachedAvatarBitmap;
                        if (_avatarLoadState != 0)
                            return null;
                    }

                    if (_avatarBitmapCache.TryGetValue(userId, out var cached) && cached != null && Volatile.Read(ref _avatarCacheGeneration) == gen)
                    {
                        _cachedAvatarUserId = userId;
                        _cachedAvatarGeneration = gen;
                        _cachedAvatarBitmap = cached;
                        _avatarLoadState = 2;
                        return cached;
                    }

                    _cachedAvatarUserId = userId;
                    _cachedAvatarGeneration = gen;
                    _cachedAvatarBitmap = null;
                    _avatarLoadState = 1;
                }

                _ = LoadOrFetchAvatarInBackgroundAsync(userId, gen);
                return null;
            }
        }

        public string AvatarUrl
        {
            get
            {
                lock (_avatarStateLock)
                {
                    if (_avatarUrl != null)
                        return _avatarUrl;
                }
                if (UserId <= 0)
                    return string.Empty;

                try
                {
                    string diskPath = Path.Combine(StoragePaths.CacheDirectory, "Avatars", $"{UserId}.png");
                    if (File.Exists(diskPath))
                    {
                        lock (_avatarStateLock) { _avatarUrl = diskPath; }
                        return diskPath;
                    }
                }
                catch (Exception ex)
                {
                    _logger.Debug($"[AvatarUrl] Could not read cached avatar for {UserId}: {ex.Message}");
                }

                _ = FetchAndCacheAvatarAsync();
                return string.Empty;
            }
        }

        private static Task<(string Path, Bitmap? Bitmap)> GetOrStartAvatarLoadAsync(long userId, int expectedGeneration)
        {
            return _inFlightAvatarLoads.GetOrAdd((userId, expectedGeneration), static k => LoadAndDecodeAvatarCoreAsync(k.UserId, k.Generation));
        }

        private static async Task<(string Path, Bitmap? Bitmap)> LoadAndDecodeAvatarCoreAsync(long userId, int expectedGeneration)
        {
            await Task.Yield();
            try
            {
                if (Volatile.Read(ref _avatarCacheGeneration) != expectedGeneration)
                    return (string.Empty, null);

                if (_avatarBitmapCache.TryGetValue(userId, out var alreadyCached) && alreadyCached != null)
                {
                    if (Volatile.Read(ref _avatarCacheGeneration) != expectedGeneration)
                        return (string.Empty, null);
                    string diskCandidate = Path.Combine(StoragePaths.CacheDirectory, "Avatars", $"{userId}.png");
                    return (File.Exists(diskCandidate) ? diskCandidate : string.Empty, alreadyCached);
                }

                string? resolvedPath = Path.Combine(StoragePaths.CacheDirectory, "Avatars", $"{userId}.png");
                if (!File.Exists(resolvedPath))
                {
                    resolvedPath = await AvatarService.EnsureAvatarAsync(userId).ConfigureAwait(false);
                }

                if (Volatile.Read(ref _avatarCacheGeneration) != expectedGeneration)
                    return (string.Empty, null);

                if (!string.IsNullOrEmpty(resolvedPath) && File.Exists(resolvedPath))
                {
                    Bitmap? resultBitmap = null;
                    await _diskDecodeGate.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        if (Volatile.Read(ref _avatarCacheGeneration) != expectedGeneration)
                            return (string.Empty, null);

                        if (!_avatarBitmapCache.TryGetValue(userId, out var existing) || existing == null)
                        {
                            using var stream = File.OpenRead(resolvedPath);
                            var fresh = Bitmap.DecodeToWidth(stream, 72);
                            lock (_avatarCacheLock)
                            {
                                if (Volatile.Read(ref _avatarCacheGeneration) != expectedGeneration)
                                {
                                    SafeExecuteHelper.Try(() => fresh.Dispose(), "LoadAndDecodeAvatarCoreAsync.StaleDispose");
                                    return (string.Empty, null);
                                }

                                if (_avatarBitmapCache.TryGetValue(userId, out var raceExisting) && raceExisting != null)
                                {
                                    SafeExecuteHelper.Try(() => fresh.Dispose(), "LoadAndDecodeAvatarCoreAsync.DuplicateDispose");
                                    resultBitmap = raceExisting;
                                }
                                else
                                {
                                    _avatarBitmapCache[userId] = fresh;
                                    resultBitmap = fresh;
                                }
                            }
                        }
                        else
                        {
                            resultBitmap = existing;
                        }
                    }
                    catch (Exception decodeEx)
                    {
                        AppServices.Logger.Debug($"[LoadAndDecodeAvatarCoreAsync] Decode skipped for {userId}: {decodeEx.Message}");
                    }
                    finally
                    {
                        _diskDecodeGate.Release();
                    }

                    return Volatile.Read(ref _avatarCacheGeneration) == expectedGeneration
                        ? (resolvedPath, resultBitmap)
                        : (string.Empty, null);
                }
            }
            catch (Exception ex)
            {
                AppServices.Logger.Debug($"[LoadAndDecodeAvatarCoreAsync] Error loading avatar for {userId}: {ex.Message}");
            }
            finally
            {
                _inFlightAvatarLoads.TryRemove((userId, expectedGeneration), out _);
            }

            return (string.Empty, null);
        }

        private async Task LoadOrFetchAvatarInBackgroundAsync(long userId, int expectedGeneration)
        {
            await Task.Yield();
            try
            {
                var (path, decoded) = await GetOrStartAvatarLoadAsync(userId, expectedGeneration).ConfigureAwait(false);
                if (decoded != null)
                {
                    ApplyResolvedAvatar(userId, expectedGeneration, path, decoded);
                }
                else
                {
                    lock (_avatarStateLock)
                    {
                        if (Volatile.Read(ref _avatarCacheGeneration) == expectedGeneration && _cachedAvatarUserId == userId)
                            _avatarLoadState = 2;
                    }
                }
            }
            catch (Exception ex)
            {
                lock (_avatarStateLock)
                {
                    if (Volatile.Read(ref _avatarCacheGeneration) == expectedGeneration && _cachedAvatarUserId == userId)
                        _avatarLoadState = 2;
                }
                _logger.Debug($"[LoadOrFetchAvatar] Error loading avatar for {userId}: {ex.Message}");
            }
        }

        public async Task<string> FetchAndCacheAvatarAsync()
        {
            long userId = UserId;
            if (userId <= 0)
                return string.Empty;

            int gen = Volatile.Read(ref _avatarCacheGeneration);
            try
            {
                var (path, decoded) = await GetOrStartAvatarLoadAsync(userId, gen).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(path) && Volatile.Read(ref _avatarCacheGeneration) == gen && UserId == userId)
                {
                    ApplyResolvedAvatar(userId, gen, path, decoded);
                    return path;
                }
            }
            catch (Exception ex)
            {
                _logger.Debug($"[FetchAndCacheAvatarAsync] Could not fetch avatar for {userId}: {ex.Message}");
            }
            finally
            {
                lock (_avatarStateLock)
                {
                    if (string.IsNullOrEmpty(_avatarUrl) && Volatile.Read(ref _avatarCacheGeneration) == gen && _cachedAvatarUserId == userId)
                        _avatarLoadState = 2;
                }
            }
            return string.Empty;
        }

        private void ApplyResolvedAvatar(long userId, int expectedGeneration, string? path, Bitmap? bitmap)
        {
            lock (_avatarStateLock)
            {
                if (Volatile.Read(ref _avatarCacheGeneration) != expectedGeneration || UserId != userId)
                    return;

                if (!string.IsNullOrEmpty(path))
                    _avatarUrl = path;

                _cachedAvatarUserId = userId;
                _cachedAvatarGeneration = expectedGeneration;
                _cachedAvatarBitmap = bitmap;
                _avatarLoadState = 2;
            }

            if (bitmap != null || !string.IsNullOrEmpty(path))
            {
                AppDispatcher.PostBackground(() =>
                {
                    if (Volatile.Read(ref _avatarCacheGeneration) != expectedGeneration || UserId != userId)
                        return;
                    OnPropertyChanged(nameof(AvatarUrl));
                    OnPropertyChanged(nameof(AvatarImage));
                });
            }
        }

        public void InvalidateAvatar()
        {
            Bitmap? prevCacheBmp;
            lock (_avatarCacheLock)
            {
                Interlocked.Increment(ref _avatarCacheGeneration);
                _avatarBitmapCache.TryRemove(UserId, out prevCacheBmp);
            }
            Bitmap? prevInstanceBmp;
            lock (_avatarStateLock)
            {
                prevInstanceBmp = _cachedAvatarBitmap;
                _avatarUrl = null;
                _cachedAvatarBitmap = null;
                _cachedAvatarUserId = 0;
                _cachedAvatarGeneration = 0;
                _avatarLoadState = 0;
            }

            AppDispatcher.Post(() =>
            {
                OnPropertyChanged(nameof(AvatarUrl));
                OnPropertyChanged(nameof(AvatarImage));
                ScheduleBitmapDisposal(new[] { prevInstanceBmp, prevCacheBmp });
            });
        }
    }
}

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
        private static readonly ConcurrentDictionary<(long UserId, int GlobalGen, int UserGen), Task<(string Path, Bitmap? Bitmap)>> _inFlightAvatarLoads = new();
        private static readonly ConcurrentDictionary<long, int> _userAvatarGenerations = new();
        private static readonly LruCache<long, Bitmap> _avatarBitmapCache = new(MaxAvatarBitmapCacheEntries, disposeOnEvict: false);
        private static readonly SemaphoreSlim _diskDecodeGate = new(6, 6);
        private static readonly System.Threading.Lock _avatarCacheLock = new();
        private static int _globalAvatarCacheGeneration = 1;

        internal static int InFlightAvatarLoadCountForTesting => _inFlightAvatarLoads.Count;

        private readonly System.Threading.Lock _avatarStateLock = new();
        private int _instanceAvatarGeneration = 1;
        private Bitmap? _cachedAvatarBitmap;
        private long _cachedAvatarUserId;
        private int _cachedAvatarGlobalGeneration;
        private int _cachedAvatarUserGeneration;
        private int _cachedAvatarInstanceGeneration;
        private int _avatarLoadState; // 0 = Unchecked, 1 = Loading, 2 = Resolved

        private static int GetUserAvatarGeneration(long userId)
            => userId > 0 && _userAvatarGenerations.TryGetValue(userId, out int gen) ? gen : 0;

        private static void BumpUserAvatarGeneration(long userId)
        {
            if (userId > 0)
                _userAvatarGenerations.AddOrUpdate(userId, 1, static (_, current) => unchecked(current + 1));
        }

        private static bool IsLoadGenerationCurrent(long userId, int expectedGlobalGen, int expectedUserGen)
            => Volatile.Read(ref _globalAvatarCacheGeneration) == expectedGlobalGen
               && GetUserAvatarGeneration(userId) == expectedUserGen;

        private bool IsInstanceAvatarCurrent(long userId, int expectedGlobalGen, int expectedUserGen, int expectedInstanceGen)
            => UserId == userId
               && IsLoadGenerationCurrent(userId, expectedGlobalGen, expectedUserGen)
               && Volatile.Read(ref _instanceAvatarGeneration) == expectedInstanceGen;

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
                Interlocked.Increment(ref _globalAvatarCacheGeneration);
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

                int globalGen = Volatile.Read(ref _globalAvatarCacheGeneration);
                int userGen = GetUserAvatarGeneration(userId);
                int instanceGen = Volatile.Read(ref _instanceAvatarGeneration);
                lock (_avatarStateLock)
                {
                    if (_cachedAvatarUserId == userId &&
                        _cachedAvatarGlobalGeneration == globalGen &&
                        _cachedAvatarUserGeneration == userGen &&
                        _cachedAvatarInstanceGeneration == instanceGen)
                    {
                        if (_cachedAvatarBitmap != null)
                            return _cachedAvatarBitmap;
                        if (_avatarLoadState != 0)
                            return null;
                    }

                    if (_avatarBitmapCache.TryGetValue(userId, out var cached) &&
                        cached != null &&
                        IsLoadGenerationCurrent(userId, globalGen, userGen) &&
                        Volatile.Read(ref _instanceAvatarGeneration) == instanceGen)
                    {
                        _cachedAvatarUserId = userId;
                        _cachedAvatarGlobalGeneration = globalGen;
                        _cachedAvatarUserGeneration = userGen;
                        _cachedAvatarInstanceGeneration = instanceGen;
                        _cachedAvatarBitmap = cached;
                        _avatarLoadState = 2;
                        return cached;
                    }

                    _cachedAvatarUserId = userId;
                    _cachedAvatarGlobalGeneration = globalGen;
                    _cachedAvatarUserGeneration = userGen;
                    _cachedAvatarInstanceGeneration = instanceGen;
                    _cachedAvatarBitmap = null;
                    _avatarLoadState = 1;
                }

                _ = LoadOrFetchAvatarInBackgroundAsync(userId, globalGen, userGen, instanceGen);
                return null;
            }
        }

        private static Task<(string Path, Bitmap? Bitmap)> GetOrStartAvatarLoadAsync(long userId, int expectedGlobalGen, int expectedUserGen)
        {
            return _inFlightAvatarLoads.GetOrAdd(
                (userId, expectedGlobalGen, expectedUserGen),
                static k => LoadAndDecodeAvatarCoreAsync(k.UserId, k.GlobalGen, k.UserGen));
        }

        private static async Task<(string Path, Bitmap? Bitmap)> LoadAndDecodeAvatarCoreAsync(long userId, int expectedGlobalGen, int expectedUserGen)
        {
            await Task.Yield();
            try
            {
                if (!IsLoadGenerationCurrent(userId, expectedGlobalGen, expectedUserGen))
                    return (string.Empty, null);

                if (_avatarBitmapCache.TryGetValue(userId, out var alreadyCached) && alreadyCached != null)
                {
                    if (!IsLoadGenerationCurrent(userId, expectedGlobalGen, expectedUserGen))
                        return (string.Empty, null);
                    string diskCandidate = Path.Combine(StoragePaths.CacheDirectory, "Avatars", $"{userId}.png");
                    return (File.Exists(diskCandidate) ? diskCandidate : string.Empty, alreadyCached);
                }

                string? resolvedPath = Path.Combine(StoragePaths.CacheDirectory, "Avatars", $"{userId}.png");
                if (!File.Exists(resolvedPath))
                {
                    resolvedPath = await AvatarService.EnsureAvatarAsync(userId).ConfigureAwait(false);
                }

                if (!IsLoadGenerationCurrent(userId, expectedGlobalGen, expectedUserGen))
                    return (string.Empty, null);

                if (!string.IsNullOrEmpty(resolvedPath) && File.Exists(resolvedPath))
                {
                    Bitmap? resultBitmap = null;
                    await _diskDecodeGate.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        if (!IsLoadGenerationCurrent(userId, expectedGlobalGen, expectedUserGen))
                            return (string.Empty, null);

                        if (!_avatarBitmapCache.TryGetValue(userId, out var existing) || existing == null)
                        {
                            using var stream = File.OpenRead(resolvedPath);
                            var fresh = Bitmap.DecodeToWidth(stream, 72);
                            lock (_avatarCacheLock)
                            {
                                if (!IsLoadGenerationCurrent(userId, expectedGlobalGen, expectedUserGen))
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

                    return IsLoadGenerationCurrent(userId, expectedGlobalGen, expectedUserGen)
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
                _inFlightAvatarLoads.TryRemove((userId, expectedGlobalGen, expectedUserGen), out _);
            }

            return (string.Empty, null);
        }

        private async Task LoadOrFetchAvatarInBackgroundAsync(long userId, int expectedGlobalGen, int expectedUserGen, int expectedInstanceGen)
        {
            await Task.Yield();
            try
            {
                var (path, decoded) = await GetOrStartAvatarLoadAsync(userId, expectedGlobalGen, expectedUserGen).ConfigureAwait(false);
                if (decoded != null)
                {
                    ApplyResolvedAvatar(userId, expectedGlobalGen, expectedUserGen, expectedInstanceGen, path, decoded);
                }
                else
                {
                    lock (_avatarStateLock)
                    {
                        if (IsInstanceAvatarCurrent(userId, expectedGlobalGen, expectedUserGen, expectedInstanceGen) &&
                            _cachedAvatarUserId == userId)
                            _avatarLoadState = 2;
                    }
                }
            }
            catch (Exception ex)
            {
                lock (_avatarStateLock)
                {
                    if (IsInstanceAvatarCurrent(userId, expectedGlobalGen, expectedUserGen, expectedInstanceGen) &&
                        _cachedAvatarUserId == userId)
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

            int globalGen = Volatile.Read(ref _globalAvatarCacheGeneration);
            int userGen = GetUserAvatarGeneration(userId);
            int instanceGen = Volatile.Read(ref _instanceAvatarGeneration);
            try
            {
                var (path, decoded) = await GetOrStartAvatarLoadAsync(userId, globalGen, userGen).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(path) && IsInstanceAvatarCurrent(userId, globalGen, userGen, instanceGen))
                {
                    ApplyResolvedAvatar(userId, globalGen, userGen, instanceGen, path, decoded);
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
                    if (_cachedAvatarBitmap == null &&
                        IsInstanceAvatarCurrent(userId, globalGen, userGen, instanceGen) &&
                        _cachedAvatarUserId == userId)
                        _avatarLoadState = 2;
                }
            }
            return string.Empty;
        }

        private void ApplyResolvedAvatar(long userId, int expectedGlobalGen, int expectedUserGen, int expectedInstanceGen, string? path, Bitmap? bitmap)
        {
            lock (_avatarStateLock)
            {
                if (!IsInstanceAvatarCurrent(userId, expectedGlobalGen, expectedUserGen, expectedInstanceGen))
                    return;

                _cachedAvatarUserId = userId;
                _cachedAvatarGlobalGeneration = expectedGlobalGen;
                _cachedAvatarUserGeneration = expectedUserGen;
                _cachedAvatarInstanceGeneration = expectedInstanceGen;
                _cachedAvatarBitmap = bitmap;
                _avatarLoadState = 2;
            }

            if (bitmap != null || !string.IsNullOrEmpty(path))
            {
                AppDispatcher.PostBackground(() =>
                {
                    if (!IsInstanceAvatarCurrent(userId, expectedGlobalGen, expectedUserGen, expectedInstanceGen))
                        return;
                    OnPropertyChanged(nameof(AvatarImage));
                });
            }
        }

        public void InvalidateAvatar()
        {
            long currentUserId = UserId;
            Bitmap? prevInstanceBmp;
            long prevCachedUserId;
            lock (_avatarStateLock)
            {
                Interlocked.Increment(ref _instanceAvatarGeneration);
                prevInstanceBmp = _cachedAvatarBitmap;
                prevCachedUserId = _cachedAvatarUserId;
                _cachedAvatarBitmap = null;
                _cachedAvatarUserId = 0;
                _cachedAvatarGlobalGeneration = 0;
                _cachedAvatarUserGeneration = 0;
                _cachedAvatarInstanceGeneration = 0;
                _avatarLoadState = 0;
            }

            Bitmap? prevCacheBmp = null;
            Bitmap? prevAlternateCacheBmp = null;
            lock (_avatarCacheLock)
            {
                BumpUserAvatarGeneration(currentUserId);
                _avatarBitmapCache.TryRemove(currentUserId, out prevCacheBmp);
                if (prevCachedUserId > 0 && prevCachedUserId != currentUserId)
                {
                    BumpUserAvatarGeneration(prevCachedUserId);
                    _avatarBitmapCache.TryRemove(prevCachedUserId, out prevAlternateCacheBmp);
                }
            }

            AppDispatcher.Post(() =>
            {
                OnPropertyChanged(nameof(AvatarImage));
                ScheduleBitmapDisposal(new[] { prevInstanceBmp, prevCacheBmp, prevAlternateCacheBmp });
            });
        }
    }
}

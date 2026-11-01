#nullable enable
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Shiro.Classes;
using Shiro.Services;

namespace Shiro.ViewModels
{
    public partial class AccountViewModel
    {
        private const int MaxAvatarBitmapCacheEntries = 2048;
        private static readonly ConcurrentDictionary<long, byte> _requestedAvatarDownloads = new();
        private static readonly LruCache<long, Bitmap> _avatarBitmapCache = new(MaxAvatarBitmapCacheEntries);
        private static readonly SemaphoreSlim _diskDecodeGate = new(6, 6);
        private static int _avatarCacheGeneration = 1;

        private string? _avatarUrl;
        private Bitmap? _cachedAvatarBitmap;
        private long _cachedAvatarUserId;
        private int _cachedAvatarGeneration;
        private int _avatarLoadState; // 0 = Unchecked, 1 = Loading, 2 = Resolved

        public static void ClearAvatarMemoryCache()
        {
            Interlocked.Increment(ref _avatarCacheGeneration);
            _avatarBitmapCache.Clear();
            _requestedAvatarDownloads.Clear();
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
                if (_cachedAvatarUserId == userId && _cachedAvatarGeneration == gen)
                {
                    if (_cachedAvatarBitmap != null)
                        return _cachedAvatarBitmap;
                    if (_avatarLoadState != 0)
                        return null;
                }

                if (_avatarBitmapCache.TryGetValue(userId, out var cached) && cached != null)
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
                _ = LoadOrFetchAvatarInBackgroundAsync(userId, gen);
                return null;
            }
        }

        public string AvatarUrl
        {
            get
            {
                if (_avatarUrl != null)
                    return _avatarUrl;
                if (UserId <= 0)
                    return string.Empty;

                try
                {
                    string diskPath = Path.Combine(StoragePaths.CacheDirectory, "Avatars", $"{UserId}.png");
                    if (File.Exists(diskPath))
                    {
                        _avatarUrl = diskPath;
                        return _avatarUrl;
                    }
                }
                catch (Exception ex)
                {
                    Program.Logger?.Debug($"[AvatarUrl] Could not read cached avatar for {UserId}: {ex.Message}");
                }

                if (_requestedAvatarDownloads.TryAdd(UserId, 0))
                {
                    _ = FetchAndCacheAvatarAsync();
                }

                return string.Empty;
            }
        }

        private async Task LoadOrFetchAvatarInBackgroundAsync(long userId, int expectedGeneration)
        {
            await Task.Yield();
            Bitmap? decoded = null;
            string? resolvedPath = _avatarUrl;

            try
            {
                await _diskDecodeGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (_avatarBitmapCache.TryGetValue(userId, out var alreadyCached) && alreadyCached != null)
                    {
                        decoded = alreadyCached;
                    }
                    else
                    {
                        if (string.IsNullOrEmpty(resolvedPath))
                        {
                            string candidate = Path.Combine(StoragePaths.CacheDirectory, "Avatars", $"{userId}.png");
                            if (File.Exists(candidate))
                            {
                                resolvedPath = candidate;
                                _avatarUrl = candidate;
                            }
                        }

                        if (!string.IsNullOrEmpty(resolvedPath) && File.Exists(resolvedPath))
                        {
                            using var stream = File.OpenRead(resolvedPath);
                            decoded = Bitmap.DecodeToWidth(stream, 72);
                            _avatarBitmapCache[userId] = decoded;
                        }
                    }
                }
                finally
                {
                    _diskDecodeGate.Release();
                }

                if (decoded != null)
                {
                    ApplyResolvedAvatar(userId, expectedGeneration, decoded);
                    return;
                }

                if (_requestedAvatarDownloads.TryAdd(userId, 0))
                {
                    await FetchAndCacheAvatarAsync().ConfigureAwait(false);
                }
                else
                {
                    _avatarLoadState = 2;
                }
            }
            catch (Exception ex)
            {
                _avatarLoadState = 2;
                Program.Logger?.Debug($"[LoadOrFetchAvatar] Error loading avatar for {userId}: {ex.Message}");
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
                string? path = await AvatarService.EnsureAvatarAsync(userId).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    _avatarUrl = path;
                    Bitmap? decoded = null;
                    await _diskDecodeGate.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        using var stream = File.OpenRead(path);
                        decoded = Bitmap.DecodeToWidth(stream, 72);
                        _avatarBitmapCache[userId] = decoded;
                    }
                    finally
                    {
                        _diskDecodeGate.Release();
                    }

                    ApplyResolvedAvatar(userId, gen, decoded);
                    return path;
                }
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"[FetchAndCacheAvatarAsync] Could not fetch avatar for {userId}: {ex.Message}");
            }
            finally
            {
                if (string.IsNullOrEmpty(_avatarUrl))
                {
                    _avatarLoadState = 2;
                    _requestedAvatarDownloads.TryRemove(userId, out _);
                }
            }
            return string.Empty;
        }

        private void ApplyResolvedAvatar(long userId, int expectedGeneration, Bitmap? bitmap)
        {
            if (Volatile.Read(ref _avatarCacheGeneration) != expectedGeneration || UserId != userId)
                return;

            _cachedAvatarUserId = userId;
            _cachedAvatarGeneration = expectedGeneration;
            _cachedAvatarBitmap = bitmap;
            _avatarLoadState = 2;

            if (bitmap != null)
            {
                AppDispatcher.PostBackground(() =>
                {
                    OnPropertyChanged(nameof(AvatarUrl));
                    OnPropertyChanged(nameof(AvatarImage));
                });
            }
        }

        public void InvalidateAvatar()
        {
            _avatarUrl = null;
            _cachedAvatarBitmap = null;
            _avatarLoadState = 0;
            _avatarBitmapCache.TryRemove(UserId, out _);
            _requestedAvatarDownloads.TryRemove(UserId, out _);
            OnPropertyChanged(nameof(AvatarUrl));
            OnPropertyChanged(nameof(AvatarImage));
        }
    }
}

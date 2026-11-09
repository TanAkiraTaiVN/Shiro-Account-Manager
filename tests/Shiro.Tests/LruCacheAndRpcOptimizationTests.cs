﻿#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Shiro.Services;
using Shiro.ViewModels;
using Xunit;

namespace Shiro.Tests
{
    public class LruCacheAndRpcOptimizationTests
    {
        // Valid 1x1 transparent PNG
        private static readonly byte[] OneByOnePngBytes = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

        [Fact]
        public async Task AccountViewModel_Avatar_DiskCachedLoadRemovesInFlightTaskAndCoalescesCallers()
        {
            AccountViewModel.ClearAvatarMemoryCache();
            long testUserId = 9876543210123L;
            string avatarsDir = Path.Combine(StoragePaths.CacheDirectory, "Avatars");
            Directory.CreateDirectory(avatarsDir);
            string diskPath = Path.Combine(avatarsDir, $"{testUserId}.png");

            try
            {
                await File.WriteAllBytesAsync(diskPath, OneByOnePngBytes);

                var vm1 = new AccountViewModel(new Account { UserID = testUserId, Username = "DiskAvatarUser1" });
                string firstPath = await vm1.FetchAndCacheAvatarAsync();
                string secondPath = await vm1.FetchAndCacheAvatarAsync();

                Assert.Equal(diskPath, firstPath);
                Assert.Equal(diskPath, secondPath);
                Assert.Equal(0, AccountViewModel.InFlightAvatarLoadCountForTesting);

                // Reset memory cache so 8 concurrent callers hit the disk decode path simultaneously
                AccountViewModel.ClearAvatarMemoryCache();

                var vms = Enumerable.Range(0, 8)
                    .Select(i => new AccountViewModel(new Account { UserID = testUserId, Username = $"ConcurrentUser{i}" }))
                    .ToArray();

                string[] paths = await Task.WhenAll(vms.Select(v => v.FetchAndCacheAvatarAsync()));
                Assert.All(paths, p => Assert.Equal(diskPath, p));
                Assert.Equal(0, AccountViewModel.InFlightAvatarLoadCountForTesting);
            }
            finally
            {
                AccountViewModel.ClearAvatarMemoryCache();
                if (File.Exists(diskPath))
                    File.Delete(diskPath);
            }
        }

        [Fact]
        public void LruCache_DisposeOnEvictFalse_NeverDisposesEvictedOverwrittenOrClearedValues()
        {
            using var cache = new LruCache<string, TrackedDisposable>(capacity: 2, disposeOnEvict: false);
            var item1 = new TrackedDisposable();
            var item2 = new TrackedDisposable();
            var item3 = new TrackedDisposable();
            var item2Replacement = new TrackedDisposable();

            cache.Set("k1", item1);
            cache.Set("k2", item2);
            cache.Set("k3", item3); // Evicts k1
            cache.Set("k2", item2Replacement); // Overwrites k2
            cache.Clear(); // Clears k2Replacement and k3

            Assert.Equal(0, cache.Count);
            Assert.False(item1.Disposed);
            Assert.False(item2.Disposed);
            Assert.False(item3.Disposed);
            Assert.False(item2Replacement.Disposed);
        }

        [Fact]
        public void LruCache_ConcurrentReadsAndWrites_RemainConsistentWithinCapacity()
        {
            using var cache = new LruCache<int, TrackedDisposable>(capacity: 32, disposeOnEvict: false);

            Parallel.For(0, 4000, i =>
            {
                int key = i % 64;
                if ((i & 1) == 0)
                {
                    cache.Set(key, new TrackedDisposable());
                }
                else
                {
                    cache.TryGetValue(key, out _);
                }
            });

            Assert.True(cache.Count <= 32);
            Assert.Equal(32, cache.Capacity);
        }

        [Fact]
        public void DiscordRpcService_BuildSetActivityPayloadAndFrame_ProducesValidHeaderAndJson()
        {
            var activity = new DiscordActivityItem
            {
                Details = "Playing Blox Fruits",
                State = "3 Clients Online",
                StartTimestamp = 1700000000L,
                LargeImageKey = "shiro_logo",
                LargeImageText = "Shiro Account Manager",
                Buttons = new List<DiscordButtonItem>
                {
                    new DiscordButtonItem { Label = "Join", Url = "https://www.roblox.com/games/123" }
                }
            };

            byte[] payloadUtf8 = DiscordRpcService.BuildSetActivityPayload(activity, "nonce-123");
            string json = Encoding.UTF8.GetString(payloadUtf8);

            Assert.Contains("\"cmd\":\"SET_ACTIVITY\"", json, StringComparison.Ordinal);
            Assert.Contains($"\"pid\":{Environment.ProcessId}", json, StringComparison.Ordinal);
            Assert.Contains("\"details\":\"Playing Blox Fruits\"", json, StringComparison.Ordinal);
            Assert.Contains("\"buttons\":[{\"label\":\"Join\",\"url\":\"https://www.roblox.com/games/123\"}]", json, StringComparison.Ordinal);

            byte[] frame = DiscordRpcService.BuildFrame(1, payloadUtf8);
            Assert.Equal(8 + payloadUtf8.Length, frame.Length);
            Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(frame.AsSpan(0, 4)));
            Assert.Equal(payloadUtf8.Length, BinaryPrimitives.ReadInt32LittleEndian(frame.AsSpan(4, 4)));

            byte[] clearPayload = DiscordRpcService.BuildSetActivityPayload(null, "nonce-clear");
            string clearJson = Encoding.UTF8.GetString(clearPayload);
            Assert.Contains("\"activity\":null", clearJson, StringComparison.Ordinal);
        }

        [Fact]
        public async Task DiscordRpcService_ReadFrameFromStreamAsync_HandlesFragmentedHeaderAndPayload()
        {
            byte[] payloadBytes = Encoding.UTF8.GetBytes("{\"evt\":\"READY\"}");
            byte[] fullFrame = DiscordRpcService.BuildFrame(1, payloadBytes);

            using var fragmentedStream = new FragmentedReadStream(fullFrame, maxBytesPerRead: 3);
            var (opcode, payload) = await DiscordRpcService.ReadFrameFromStreamAsync(fragmentedStream);

            Assert.Equal(1, opcode);
            Assert.Equal("{\"evt\":\"READY\"}", payload);
        }

        [Fact]
        public void FlagIconService_ParseTransform_SpanAndGeneratedRegexProduceExpectedMatrices()
        {
            Matrix translate = FlagIconService.ParseTransformForTesting("translate(10, 20)");
            Assert.Equal(Matrix.CreateTranslation(10, 20), translate);

            Matrix scale = FlagIconService.ParseTransformForTesting("scale(2.5)");
            Assert.Equal(Matrix.CreateScale(2.5, 2.5), scale);

            Matrix matrix = FlagIconService.ParseTransformForTesting("matrix(1 0 0 1 3 4)");
            Assert.Equal(new Matrix(1, 0, 0, 1, 3, 4), matrix);

            Matrix chained = FlagIconService.ParseTransformForTesting("translate(1 2) scale(3)");
            Matrix expectedChained = Matrix.CreateScale(3, 3) * Matrix.CreateTranslation(1, 2);
            Assert.Equal(expectedChained, chained);
        }

        private sealed class TrackedDisposable : IDisposable
        {
            public bool Disposed { get; private set; }
            public void Dispose() => Disposed = true;
        }

        private sealed class FragmentedReadStream : Stream
        {
            private readonly byte[] _data;
            private readonly int _maxBytesPerRead;
            private int _position;

            public FragmentedReadStream(byte[] data, int maxBytesPerRead)
            {
                _data = data;
                _maxBytesPerRead = maxBytesPerRead;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => _data.Length;
            public override long Position
            {
                get => _position;
                set => throw new NotSupportedException();
            }

            public override void Flush() { }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_position >= _data.Length) return 0;
                int toCopy = Math.Min(Math.Min(count, _maxBytesPerRead), _data.Length - _position);
                Array.Copy(_data, _position, buffer, offset, toCopy);
                _position += toCopy;
                return toCopy;
            }

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (_position >= _data.Length) return ValueTask.FromResult(0);
                int toCopy = Math.Min(Math.Min(buffer.Length, _maxBytesPerRead), _data.Length - _position);
                _data.AsSpan(_position, toCopy).CopyTo(buffer.Span);
                _position += toCopy;
                return ValueTask.FromResult(toCopy);
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}


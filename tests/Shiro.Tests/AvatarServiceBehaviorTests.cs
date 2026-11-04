#nullable enable
using System;
using System.IO;
using System.Threading.Tasks;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("StoragePathsCollection")]
    public class AvatarServiceBehaviorTests
    {
        [Fact]
        public void GetDiskCachePath_ReturnsExpectedPathStructure()
        {
            string? path = AvatarService.GetDiskCachePath(123456789);
            Assert.NotNull(path);
            Assert.EndsWith("123456789.png", path, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Avatars", path, StringComparison.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(-999)]
        public void HasDiskCache_InvalidUserId_ReturnsFalse(long invalidId)
        {
            Assert.False(AvatarService.HasDiskCache(invalidId));
        }

        [Fact]
        public void HasDiskCache_NonExistentUser_ReturnsFalse()
        {
            long randomId = Random.Shared.Next(100_000_000, 999_999_999);
            Assert.False(AvatarService.HasDiskCache(randomId));
        }

        [Fact]
        public async Task HasDiskCache_And_EnsureAvatarAsync_ExistingFile_ReturnsTrueAndPath()
        {
            string isolated = Path.Combine(Path.GetTempPath(), "Shiro-AvatarTest-" + Guid.NewGuid().ToString("N"));
            try
            {
                using (StoragePaths.OverrideDataDirectoryForTesting(isolated))
                {
                    long testUserId = 888777666;
                    string? cachePath = AvatarService.GetDiskCachePath(testUserId);
                    Assert.NotNull(cachePath);

                    Directory.CreateDirectory(Path.GetDirectoryName(cachePath!)!);
                    File.WriteAllBytes(cachePath!, new byte[] { 0x89, 0x50, 0x4E, 0x47 }); // PNG header bytes

                    Assert.True(AvatarService.HasDiskCache(testUserId));

                    string? resolvedPath = await AvatarService.EnsureAvatarAsync(testUserId);
                    Assert.Equal(cachePath, resolvedPath);
                }
            }
            finally
            {
                if (Directory.Exists(isolated))
                {
                    try { Directory.Delete(isolated, true); }
                    catch (IOException) { }
                }
            }
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-5)]
        public async Task EnsureAvatarAsync_InvalidUserId_ReturnsNull(long invalidId)
        {
            string? result = await AvatarService.EnsureAvatarAsync(invalidId);
            Assert.Null(result);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public async Task DownloadAsync_NullOrWhitespace_ReturnsNull(string? url)
        {
            var service = new AvatarService();
            byte[]? result = await service.DownloadAsync(url);
            Assert.Null(result);
        }
    }
}

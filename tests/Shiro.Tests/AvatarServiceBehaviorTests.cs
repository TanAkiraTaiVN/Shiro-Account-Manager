#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public class AvatarServiceBehaviorTests : IDisposable
    {
        private readonly string _tempDir;
        private readonly IDisposable _storageScope;

        public AvatarServiceBehaviorTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), $"ShiroAvatarCov_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_tempDir);
            _storageScope = StoragePaths.OverrideDataDirectoryForTesting(_tempDir);
        }

        public void Dispose()
        {
            _storageScope.Dispose();
            try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
        }

        [Fact]
        public async Task AvatarService_DiskCacheAndBasicValidation_CoversBranches()
        {
            var service = new AvatarService();

            // Null / empty url
            var nullData = await service.DownloadAsync(null);
            Assert.Null(nullData);

            var emptyData = await service.DownloadAsync("   ");
            Assert.Null(emptyData);

            // Invalid userIds
            Assert.False(AvatarService.HasDiskCache(0));
            Assert.False(AvatarService.HasDiskCache(-10));
            Assert.Null(await AvatarService.EnsureAvatarAsync(0));
            Assert.Null(await AvatarService.EnsureAvatarAsync(-5));

            // Disk path for positive userId
            string? path = AvatarService.GetDiskCachePath(12345);
            Assert.False(string.IsNullOrEmpty(path));
            Assert.Contains("12345.png", path);

            // Existing disk cache file
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllBytes(path!, new byte[] { 0x89, 0x50, 0x4E, 0x47 });

            Assert.True(AvatarService.HasDiskCache(12345));
            string? ensured = await AvatarService.EnsureAvatarAsync(12345);
            Assert.Equal(path, ensured);

            // Batch with all cached or empty
            var resEmpty = await AvatarService.BatchEnsureAvatarsAsync(new long[] { 12345, 0, -1 });
            Assert.Empty(resEmpty);

            var resNull = await AvatarService.BatchEnsureAvatarsAsync(null!);
            Assert.Empty(resNull);
        }

        [Fact]
        public async Task AvatarService_DownloadAndEnsureWithMockHttp_CoversNetworkBranches()
        {
            var fakeHandler = new StubHttpHandler(request =>
            {
                string url = request.RequestUri?.ToString() ?? string.Empty;
                if (url.Contains("avatar-headshot"))
                {
                    // Thumbnail JSON response
                    string json = "{\"data\":[{\"targetId\":99901,\"state\":\"Completed\",\"imageUrl\":\"https://fake.rbxcdn.com/avatar.png\"}]}";
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(json, Encoding.UTF8, "application/json")
                    };
                }
                if (url.Contains("fake.rbxcdn.com"))
                {
                    // Image payload
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(new byte[] { 1, 2, 3, 4 })
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });

            var testClient = new HttpClient(fakeHandler);
            SharedHttpClient.SetTestClient("default", testClient);

            try
            {
                var service = new AvatarService();

                // 1. DownloadAsync valid URL
                byte[]? downloaded = await service.DownloadAsync("https://fake.rbxcdn.com/avatar.png");
                Assert.NotNull(downloaded);
                Assert.Equal(4, downloaded.Length);

                // 2. EnsureAvatarAsync non-cached user
                string? avatarPath = await AvatarService.EnsureAvatarAsync(99901);
                Assert.False(string.IsNullOrEmpty(avatarPath));
                Assert.True(File.Exists(avatarPath));
                Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(avatarPath));

                // 3. BatchEnsureAvatarsAsync
                var batchRes = await AvatarService.BatchEnsureAvatarsAsync(new long[] { 99901, 99902 });
                Assert.NotEmpty(batchRes);
            }
            finally
            {
                SharedHttpClient.ResetTestClient("default");
            }
        }

        private sealed class StubHttpHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;
            public StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) => _handler = handler;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                Task.FromResult(_handler(request));
        }
    }
}

#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Http;
using Shiro.Services;
using Shiro.ViewModels;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public class StreamerModeAndStorageOptimizationTests
    {
        [Theory]
        [InlineData("obs64.exe", true)]
        [InlineData("OBS64", true)]
        [InlineData("obs32.exe", true)]
        [InlineData("Streamlabs OBS.exe", true)]
        [InlineData("TwitchStudio.EXE", true)]
        [InlineData("XSplit.Core.exe", true)]
        [InlineData("PrismLiveStudio", true)]
        [InlineData("Action.exe", true)]
        [InlineData("bdcam.exe", true)]
        [InlineData("RobloxPlayerBeta.exe", false)]
        [InlineData("explorer.exe", false)]
        [InlineData("", false)]
        [InlineData(".exe", false)]
        public void StreamerModeService_MatchesStreamingProcessName_HandlesSpanAndExeSuffix(string input, bool expected)
        {
            bool actual = StreamerModeService.MatchesStreamingProcessName(input.AsSpan());
            Assert.Equal(expected, actual);
        }

        [Fact]
        public void StreamerModeService_HasAnyStreamingProcessRunning_ExecutesSinglePassSnapshotWithoutError()
        {
            bool result = StreamerModeService.HasAnyStreamingProcessRunning();
            Assert.Equal(result, StreamerModeService.HasAnyStreamingProcessRunning());
        }

        [Theory]
        [InlineData(null, "")]
        [InlineData("", "")]
        [InlineData("A", "**")]
        [InlineData("AB", "**")]
        [InlineData("ABC", "A*C")]
        [InlineData("RobloxMaster", "R**********r")]
        public void StreamerModeService_MaskUsername_UsesStringCreateAccurately(string? input, string expected)
        {
            string masked = StreamerModeService.MaskUsername(input);
            Assert.Equal(expected, masked);
        }

        [Fact]
        public void AccountRepository_IndexedLookup_ResolvesByIdAndUsernameAndHandlesMutations()
        {
            var previous = AppServices.AccountRepository.GetAll().ToList();
            try
            {
                var acc1 = new Account { UserID = 1001, Username = "AlphaPlayer", Valid = true };
                var acc2 = new Account { UserID = 1002, Username = "BetaPlayer", Valid = true };
                var acc3 = new Account { UserID = 1003, Username = "GammaPlayer", Valid = true };

                lock (AccountManager.AccountListLock)
                {
                    AccountManager.ResetAccountsForTests(new[] { acc1, acc2, acc3 });
                }

                var repo = AccountRepository.Default;

                // O(1) indexed lookups
                Assert.Same(acc1, repo.GetById(1001));
                Assert.Same(acc2, repo.GetById(1002));
                Assert.Null(repo.GetById(9999));

                Assert.Same(acc2, repo.GetByUsername("betaplayer"));
                Assert.Same(acc3, repo.GetByUsername("GAMMAPLAYER"));
                Assert.Null(repo.GetByUsername("NonExistentUser"));

                // In-place mutation fallback & index refresh
                acc2.UserID = 2002;
                acc2.Username = "RenamedBeta";

                Assert.Same(acc2, repo.GetById(2002));
                Assert.Null(repo.GetById(1002));
                Assert.Same(acc2, repo.GetByUsername("renamedbeta"));
                Assert.Null(repo.GetByUsername("BetaPlayer"));
            }
            finally
            {
                lock (AccountManager.AccountListLock)
                {
                    AccountManager.ResetAccountsForTests(previous);
                }
            }
        }

        [Fact]
        public void ProxyBindingService_DistributeProxiesSticky_MinHeapPreservesStickyAndTieOrder()
        {
            var a1 = new Account { Username = "A1", Proxy = "http://p2:8080" };
            var a2 = new Account { Username = "A2" };
            var a3 = new Account { Username = "A3" };
            var a4 = new Account { Username = "A4" };
            var a5 = new Account { Username = "A5" };

            var proxies = new[]
            {
                "http://p1:8080",
                "http://p2:8080",
                "http://p3:8080"
            };

            ProxyBindingService.DistributeProxiesSticky(new[] { a1, a2, a3, a4, a5 }, proxies);

            // a1 keeps its sticky proxy p2 (usage=1)
            Assert.Equal("http://p2:8080", a1.Proxy);
            // a2 gets least-used first in order: p1 (usage 0 -> 1)
            Assert.Equal("http://p1:8080", a2.Proxy);
            // a3 gets next least-used in order: p3 (usage 0 -> 1)
            Assert.Equal("http://p3:8080", a3.Proxy);
            // Now all 3 have usage=1; a4 gets first in order: p1 (usage 1 -> 2)
            Assert.Equal("http://p1:8080", a4.Proxy);
            // Next with usage=1 is p2: a5 gets p2 (usage 1 -> 2)
            Assert.Equal("http://p2:8080", a5.Proxy);
        }

        [Fact]
        public async Task RestClient_JsonBody_SerializesDirectlyToUtf8ByteArrayContent()
        {
            string? capturedContentType = null;
            string? capturedCharset = null;
            byte[]? capturedBytes = null;
            bool wasByteArrayContent = false;

            var handler = new DelegateHttpMessageHandler(async (req, ct) =>
            {
                wasByteArrayContent = req.Content is ByteArrayContent && req.Content is not StringContent;
                capturedContentType = req.Content?.Headers.ContentType?.MediaType;
                capturedCharset = req.Content?.Headers.ContentType?.CharSet;
                capturedBytes = req.Content != null
                    ? await req.Content.ReadAsByteArrayAsync(ct)
                    : Array.Empty<byte>();

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"userPresences\":[]}", Encoding.UTF8, "application/json")
                };
            });

            using var httpClient = new HttpClient(handler);
            using var restClient = new RestClient(httpClient, "https://presence.roblox.com/");

            var request = new RestRequest("v1/presence/users", Method.Post)
                .AddJsonBody(new RobloxPresenceUsersRequest(new long[] { 123L, 456L }));

            var response = await restClient.ExecuteAsync(request);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(wasByteArrayContent);
            Assert.Equal("application/json", capturedContentType);
            Assert.Equal("utf-8", capturedCharset);
            Assert.NotNull(capturedBytes);
            Assert.Equal("{\"userIds\":[123,456]}", Encoding.UTF8.GetString(capturedBytes!));
        }

        [Fact]
        public void AccountStore_HistoricalBackups_UsesDirectoryInfoAndPreservesLargestBackup()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ShiroAccountStore_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                string accountFile = Path.Combine(tempDir, "AccountData.json");
                string backupsDir = Path.Combine(tempDir, "Backups");
                Directory.CreateDirectory(backupsDir);

                var store = new AccountStore(accountFile, backupsDir);

                // Create 12 historical backup files where the oldest file is the largest
                for (int i = 0; i < 12; i++)
                {
                    string filePath = Path.Combine(backupsDir, $"AccountData_20261104_1200{i:D2}.json.bak");
                    byte[] payload = i == 0 ? new byte[4096] : new byte[64 + i];
                    Array.Fill(payload, (byte)'A');
                    File.WriteAllBytes(filePath, payload);
                    File.SetCreationTimeUtc(filePath, DateTime.UtcNow.AddMinutes(-30 + i));
                }

                // Trigger PruneHistoricalBackups via CreateHistoricalBackup
                store.CreateHistoricalBackup(Encoding.UTF8.GetBytes("{\"latest\":true}"));

                IReadOnlyList<string> backups = store.GetHistoricalBackups();
                Assert.Equal(AccountStore.MaxHistoricalBackups, backups.Count);

                // Largest file (AccountData_20261104_120000.json.bak, 4096 bytes) must be preserved
                Assert.Contains(backups, p => p.EndsWith("AccountData_20261104_120000.json.bak", StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
        }

        private sealed class DelegateHttpMessageHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

            public DelegateHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
            {
                _handler = handler;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                _handler(request, cancellationToken);
        }
    }
}


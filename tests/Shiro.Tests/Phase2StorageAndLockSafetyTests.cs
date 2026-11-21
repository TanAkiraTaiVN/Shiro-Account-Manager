#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public class Phase2StorageAndLockSafetyTests
    {
        private static string CreateTempDir(string prefix)
        {
            string dir = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void CleanupTempDir(string dir)
        {
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        [Fact]
        public void CleanupStaleRuntimeArtifacts_PreservesEmbeddedBrowser_AndPreservesDirWithFreshNestedFile()
        {
            string tempDir = CreateTempDir("Shiro-P2-Cleanup-");
            try
            {
                using var dirScope = StoragePaths.OverrideDataDirectoryForTesting(tempDir);
                string browserRoot = StoragePaths.BrowserSessionsDirectory;
                DateTime staleTime = DateTime.UtcNow.AddDays(-30);

                string embeddedBrowserDir = Path.Combine(browserRoot, "EmbeddedBrowser");
                Directory.CreateDirectory(embeddedBrowserDir);
                string embeddedOldFile = Path.Combine(embeddedBrowserDir, "cookies.dat");
                File.WriteAllText(embeddedOldFile, "active-profile");
                File.SetLastWriteTimeUtc(embeddedOldFile, staleTime);
                Directory.SetLastWriteTimeUtc(embeddedBrowserDir, staleTime);

                string proxyDir = Path.Combine(browserRoot, "EmbeddedBrowser_Proxy_abc");
                string nestedDir = Path.Combine(proxyDir, "nested");
                Directory.CreateDirectory(nestedDir);
                string freshNestedFile = Path.Combine(nestedDir, "fresh.dat");
                File.WriteAllText(freshNestedFile, "fresh-nested-state");
                Directory.SetLastWriteTimeUtc(nestedDir, staleTime);
                Directory.SetLastWriteTimeUtc(proxyDir, staleTime);

                string abandonedDir = Path.Combine(browserRoot, "abandoned-old");
                Directory.CreateDirectory(abandonedDir);
                string abandonedFile = Path.Combine(abandonedDir, "old.dat");
                File.WriteAllText(abandonedFile, "abandoned");
                File.SetLastWriteTimeUtc(abandonedFile, staleTime);
                Directory.SetLastWriteTimeUtc(abandonedDir, staleTime);

                int removed = StoragePaths.CleanupStaleRuntimeArtifacts(TimeSpan.FromDays(7));

                Assert.Equal(1, removed);
                Assert.True(Directory.Exists(embeddedBrowserDir));
                Assert.True(File.Exists(embeddedOldFile));
                Assert.True(File.Exists(freshNestedFile));
                Assert.False(Directory.Exists(abandonedDir));
            }
            finally
            {
                CleanupTempDir(tempDir);
            }
        }

        [Fact]
        public async Task GameInfoService_RecentGamesSnapshot_ConcurrentWithWrites_NeverThrowsOrCorruptsFile()
        {
            string tempDir = CreateTempDir("Shiro-P2-RecentGames-");
            var prevRecent = AppServices.ServerBrowserState.RecentGames?.ToList() ?? new List<Game>();
            var prevHttpFactory = GameInfoService.HttpClientFactoryForTests;

            try
            {
                using var dirScope = StoragePaths.OverrideDataDirectoryForTesting(tempDir);
                AppServices.ServerBrowserState.RecentGames = new List<Game>();
                const int iterations = 40;
                var errors = new ConcurrentQueue<Exception>();
                using var startGate = new Barrier(4);

                Task writer1 = Task.Run(async () =>
                {
                    try
                    {
                        startGate.SignalAndWait();
                        for (int i = 0; i < iterations; i++)
                        {
                            long placeId = 910_000 + (i % 5);
                            var game = new Game { Details = GameDetails.Unknown(placeId, $"Place {placeId}") };
                            await GameInfoService.AddRecentGameAsync(game, loading: false).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex) { errors.Enqueue(ex); }
                });

                Task writer2 = Task.Run(async () =>
                {
                    try
                    {
                        startGate.SignalAndWait();
                        for (int i = 0; i < iterations; i++)
                        {
                            long placeId = 910_005 + (i % 5);
                            var game = new Game { Details = GameDetails.Unknown(placeId, $"Place {placeId}") };
                            await GameInfoService.AddRecentGameAsync(game, loading: false).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex) { errors.Enqueue(ex); }
                });

                Task titleCacheWorker = Task.Run(() =>
                {
                    try
                    {
                        startGate.SignalAndWait();
                        for (int i = 0; i < iterations; i++)
                        {
                            long placeId = 910_000 + (i % 10);
                            GameInfoService.CachePlaceName(placeId, $"Resolved Game {placeId} #{i}");
                        }
                    }
                    catch (Exception ex) { errors.Enqueue(ex); }
                });

                Task snapshotReader = Task.Run(() =>
                {
                    try
                    {
                        startGate.SignalAndWait();
                        for (int i = 0; i < iterations; i++)
                        {
                            List<Game> snapshot = GameInfoService.GetRecentGamesSnapshot();
                            foreach (var item in snapshot)
                            {
                                _ = item?.Details?.BestDisplayName;
                            }
                        }
                    }
                    catch (Exception ex) { errors.Enqueue(ex); }
                });

                await Task.WhenAll(writer1, writer2, titleCacheWorker, snapshotReader);

                Assert.Empty(errors);
                Assert.True(File.Exists(StoragePaths.RecentGamesFile));

                string rawJson = File.ReadAllText(StoragePaths.RecentGamesFile);
                using var parsed = JsonDocument.Parse(rawJson);
                Assert.Equal(JsonValueKind.Array, parsed.RootElement.ValueKind);
                Assert.InRange(parsed.RootElement.GetArrayLength(), 1, 20);
            }
            finally
            {
                GameInfoService.HttpClientFactoryForTests = prevHttpFactory;
                AppServices.ServerBrowserState.RecentGames = prevRecent;
                CleanupTempDir(tempDir);
            }
        }

        [Fact]
        public void AccountPersistenceService_LoadCorruptAccounts_InvokesShowErrorOutsideSaveLock()
        {
            string tempDir = CreateTempDir("Shiro-P2-LoadLock-");
            var prevLastValid = AppServices.SelectionState.LastValidAccount;

            try
            {
                using var dirScope = StoragePaths.OverrideDataDirectoryForTesting(tempDir);
                var repo = new AccountRepository();
                var persistence = new AccountPersistenceService(repo)
                {
                    Store = new AccountStore(StoragePaths.AccountDataFile, StoragePaths.BackupsDirectory),
                    StartTime = DateTime.Now.AddMinutes(-5)
                };

                File.WriteAllBytes(StoragePaths.AccountDataFile, Encoding.UTF8.GetBytes("CORRUPT_ACCOUNT_FILE_BYTES"));

                bool nestedSaveCompleted = false;
                int showErrorCalls = 0;
                string? capturedMessage = null;

                var dialogSub = Substitute.For<IDialogService>();
                dialogSub
                    .When(d => d.ShowError(Arg.Any<string>(), Arg.Any<string>()))
                    .Do(call =>
                    {
                        Interlocked.Increment(ref showErrorCalls);
                        capturedMessage = call.ArgAt<string>(0);
                        Task saveTask = Task.Run(() => persistence.SaveAccounts(bypassRateLimit: false));
                        nestedSaveCompleted = saveTask.Wait(TimeSpan.FromSeconds(2));
                    });

                using var dialogScope = AppServices.Override<IDialogService>(dialogSub);

                persistence.LoadAccounts(null, out bool unlocked);

                Assert.False(unlocked);
                Assert.Equal(1, showErrorCalls);
                Assert.True(nestedSaveCompleted, "SaveAccounts deadlocked because saveLock was still held when ShowError was called.");
                Assert.Contains("Failed to load accounts!", capturedMessage, StringComparison.Ordinal);
            }
            finally
            {
                AppServices.SelectionState.LastValidAccount = prevLastValid;
                CleanupTempDir(tempDir);
            }
        }

        [Fact]
        public async Task AccountPersistenceService_TryLoadFromBackupAsync_InvokesShowErrorOutsideSaveLock()
        {
            string tempDir = CreateTempDir("Shiro-P2-BackupLock-");
            var prevLastValid = AppServices.SelectionState.LastValidAccount;

            try
            {
                using var dirScope = StoragePaths.OverrideDataDirectoryForTesting(tempDir);
                var repo = new AccountRepository();
                var persistence = new AccountPersistenceService(repo)
                {
                    Store = new AccountStore(StoragePaths.AccountDataFile, StoragePaths.BackupsDirectory),
                    StartTime = DateTime.Now.AddMinutes(-5)
                };

                bool nestedSaveCompleted = false;
                int showErrorCalls = 0;
                string? capturedMessage = null;

                var dialogSub = Substitute.For<IDialogService>();
                dialogSub.ShowConfirmationAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(Task.FromResult(true));
                dialogSub
                    .When(d => d.ShowError(Arg.Any<string>(), Arg.Any<string>()))
                    .Do(call =>
                    {
                        Interlocked.Increment(ref showErrorCalls);
                        capturedMessage = call.ArgAt<string>(0);
                        Task saveTask = Task.Run(() => persistence.SaveAccounts(bypassRateLimit: false));
                        nestedSaveCompleted = saveTask.Wait(TimeSpan.FromSeconds(2));
                    });

                using var dialogScope = AppServices.Override<IDialogService>(dialogSub);

                BackupLoadResult result = await persistence.TryLoadFromBackupAsync(new byte[] { 1, 2, 3, 4 });

                Assert.Equal(BackupLoadResult.Failed, result);
                Assert.Equal(1, showErrorCalls);
                Assert.True(nestedSaveCompleted, "SaveAccounts deadlocked because saveLock was still held when ShowError was called.");
                Assert.Equal("Failed to load backup file!", capturedMessage);
            }
            finally
            {
                AppServices.SelectionState.LastValidAccount = prevLastValid;
                CleanupTempDir(tempDir);
            }
        }

        [Fact]
        public void StoragePaths_GetOrCreateVaultKey_ReturnsDefensiveCopy_ZeroingDoesNotCorruptCache()
        {
            string tempDir = CreateTempDir("Shiro-P2-VaultKeyCopy-");

            try
            {
                using var dirScope = StoragePaths.OverrideDataDirectoryForTesting(tempDir);
                StoragePaths.ReloadVaultKeyCache();

                byte[] key1 = StoragePaths.GetOrCreateVaultKey();
                Assert.Equal(32, key1.Length);
                Assert.Contains(key1, b => b != 0);
                byte[] expected = key1.ToArray();

                CryptographicOperations.ZeroMemory(key1);

                byte[] key2 = StoragePaths.GetOrCreateVaultKey();
                Assert.Equal(32, key2.Length);
                Assert.Contains(key2, b => b != 0);
                Assert.NotSame(key1, key2);
                Assert.Equal(expected, key2);
            }
            finally
            {
                StoragePaths.ReloadVaultKeyCache();
                CleanupTempDir(tempDir);
            }
        }

        [Fact]
        public void StoragePaths_IsDirectoryStale_FailsSafeOnInvalidOrMissing_AndChecksNestedEntries()
        {
            string tempDir = CreateTempDir("Shiro-P2-IsStale-");
            try
            {
                DateTime cutoffUtc = DateTime.UtcNow.AddDays(-7);
                DateTime staleTime = DateTime.UtcNow.AddDays(-30);

                Assert.False(StoragePaths.IsDirectoryStale(string.Empty, cutoffUtc));
                Assert.False(StoragePaths.IsDirectoryStale(Path.Combine(tempDir, "does-not-exist"), cutoffUtc));

                string filePath = Path.Combine(tempDir, "not-a-dir.txt");
                File.WriteAllText(filePath, "data");
                File.SetLastWriteTimeUtc(filePath, staleTime);
                Assert.False(StoragePaths.IsDirectoryStale(filePath, cutoffUtc));

                string staleDir = Path.Combine(tempDir, "all-stale");
                string staleSub = Path.Combine(staleDir, "sub");
                Directory.CreateDirectory(staleSub);
                string staleLeaf = Path.Combine(staleSub, "old.dat");
                File.WriteAllText(staleLeaf, "old");
                File.SetLastWriteTimeUtc(staleLeaf, staleTime);
                Directory.SetLastWriteTimeUtc(staleSub, staleTime);
                Directory.SetLastWriteTimeUtc(staleDir, staleTime);
                Assert.True(StoragePaths.IsDirectoryStale(staleDir, cutoffUtc));

                File.SetLastWriteTimeUtc(staleLeaf, DateTime.UtcNow);
                Assert.False(StoragePaths.IsDirectoryStale(staleDir, cutoffUtc));
            }
            finally
            {
                CleanupTempDir(tempDir);
            }
        }
    }
}

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public class StoragePathsTests
    {
        [Fact]
        public void StoragePaths_Logger_GetterAndSetter_Work()
        {
            var prev = StoragePaths.Logger;
            StoragePaths.Logger = prev;
            Assert.Same(prev, StoragePaths.Logger);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void LogRedactor_NullOrEmpty_ReturnsEmpty(string? msg)
        {
            Assert.Equal(string.Empty, LogRedactor.Redact(msg));
        }

        [Fact]
        public void EnsureInitialized_SetsBaseAndDataDirectory()
        {
            StoragePaths.EnsureInitialized();

            Assert.False(string.IsNullOrWhiteSpace(StoragePaths.BaseDirectory));
            Assert.False(string.IsNullOrWhiteSpace(StoragePaths.DataDirectory));
            Assert.True(Directory.Exists(StoragePaths.DataDirectory));
        }

        [Fact]
        public void ResolveUserFile_WithRelativeFileName_ReturnsPathInDataDirectory()
        {
            StoragePaths.EnsureInitialized();

            string resolved = StoragePaths.ResolveUserFile("TestFile.json");

            Assert.StartsWith(StoragePaths.DataDirectory, resolved, StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith("TestFile.json", resolved, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void ResolveUserFile_WithRootedPath_ReturnsOriginalPath()
        {
            StoragePaths.EnsureInitialized();

            string rooted = @"C:\CustomFolder\MyFile.json";
            string resolved = StoragePaths.ResolveUserFile(rooted);

            Assert.Equal(rooted, resolved);
        }

        [Fact]
        public void AtomicWriteBytes_WritesFileSafely()
        {
            StoragePaths.EnsureInitialized();
            string testFile = StoragePaths.GetUserFile($"unit_test_{Guid.NewGuid():N}.tmp");

            try
            {
                byte[] testData = new byte[] { 1, 2, 3, 4, 5, 42 };
                StoragePaths.AtomicWriteBytes(testFile, testData, durable: false);

                Assert.True(File.Exists(testFile));
                byte[] readBack = File.ReadAllBytes(testFile);
                Assert.Equal(testData, readBack);
            }
            finally
            {
                if (File.Exists(testFile))
                {
                    try { File.Delete(testFile); }
                    catch (IOException) { }
                }
            }
        }

        [Fact]
        public void GetOrCreateVaultKey_ReturnsValid32ByteKey()
        {
            StoragePaths.EnsureInitialized();
            byte[] key1 = StoragePaths.GetOrCreateVaultKey();
            byte[] key2 = StoragePaths.GetOrCreateVaultKey();

            Assert.NotNull(key1);
            Assert.Equal(32, key1.Length);
            Assert.Equal(key1, key2);
            Assert.NotSame(key1, key2);
            Assert.True(File.Exists(StoragePaths.VaultKeyFile));
        }

        [Fact]
        public void GetOrCreateVaultKey_InstalledMode_StoresDpapiWrappedKeyFile()
        {
            StoragePaths.EnsureInitialized();
            if (StoragePaths.IsPortable)
                return;

            _ = StoragePaths.GetOrCreateVaultKey();
            byte[] onDisk = File.ReadAllBytes(StoragePaths.VaultKeyFile);

            Assert.NotEqual(32, onDisk.Length);
            Assert.StartsWith("SHIRO_DPAPI_KEY_V1", System.Text.Encoding.ASCII.GetString(onDisk));
        }

        [Fact]
        public void MainViewModel_SearchText_ClearAndRetype_DoesNotThrowObjectDisposedException()
        {
            var vm = new Shiro.ViewModels.MainViewModel();
            vm.SearchText = "abc";
            vm.SearchText = string.Empty;
            vm.SearchText = "t";
            vm.SearchText = "ta";
            vm.SearchText = "tan";
            vm.SearchText = null!;
            vm.SearchText = "jackpot";
            Assert.Equal("jackpot", vm.SearchText);
        }

        [Fact]
        public void IsUserIdInBlockedListJson_MatchesExactUserIdOnly()
        {
            string json = @"{""blockedUsers"":[{""userId"":1234567,""name"":""PlayerA""},{""id"":987654,""name"":""PlayerB""}]}";

            Assert.True(Shiro.Account.IsUserIdInBlockedListJson(json, 1234567));
            Assert.True(Shiro.Account.IsUserIdInBlockedListJson(json, 987654));
            Assert.False(Shiro.Account.IsUserIdInBlockedListJson(json, 123));
            Assert.False(Shiro.Account.IsUserIdInBlockedListJson(json, 456));
        }

        [Fact]
        public void IsWithinDirectory_DetectsValidChildrenAndRejectsTraversal()
        {
            string baseDir = Path.Combine(Path.GetTempPath(), "Shiro-BoundaryTest");
            Directory.CreateDirectory(baseDir);

            try
            {
                string safeChild = Path.Combine(baseDir, "SubFolder", "file.txt");
                string traversal = Path.Combine(baseDir, "..", "evil.txt");
                string outsideRoot = @"C:\Windows\System32\cmd.exe";

                Assert.True(StoragePaths.IsWithinDirectory(safeChild, baseDir));
                Assert.False(StoragePaths.IsWithinDirectory(traversal, baseDir));
                Assert.False(StoragePaths.IsWithinDirectory(outsideRoot, baseDir));
                Assert.False(StoragePaths.IsWithinDirectory(null!, baseDir));
                Assert.False(StoragePaths.IsWithinDirectory(safeChild, null!));
            }
            finally
            {
                if (Directory.Exists(baseDir))
                {
                    try { Directory.Delete(baseDir, true); }
                    catch (IOException) { }
                }
            }
        }

        [Fact]
        public void OverrideDataDirectoryForTesting_IsolatesStorageEnvironment()
        {
            string isolated = Path.Combine(Path.GetTempPath(), "Shiro-Isolated-" + Guid.NewGuid().ToString("N"));
            try
            {
                using (StoragePaths.OverrideDataDirectoryForTesting(isolated))
                {
                    Assert.Equal(isolated, StoragePaths.DataDirectory);
                    string userFile = StoragePaths.GetUserFile("CustomData.json");
                    Assert.StartsWith(isolated, userFile, StringComparison.OrdinalIgnoreCase);

                    string customDir = StoragePaths.GetUserDirectory("CustomDir");
                    Assert.True(Directory.Exists(customDir));
                    Assert.StartsWith(isolated, customDir, StringComparison.OrdinalIgnoreCase);
                }

                // After dispose, it should revert back
                Assert.NotEqual(isolated, StoragePaths.DataDirectory);
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

        [Fact]
        public void SaveRecoveredVaultKey_PersistsAndCaches32ByteKey()
        {
            string isolated = Path.Combine(Path.GetTempPath(), "Shiro-RecoverKey-" + Guid.NewGuid().ToString("N"));
            try
            {
                using (StoragePaths.OverrideDataDirectoryForTesting(isolated))
                {
                    byte[] rawKey = new byte[32];
                    Random.Shared.NextBytes(rawKey);

                    // Argument validation
                    Assert.Throws<ArgumentException>(() => StoragePaths.SaveRecoveredVaultKey(null!));
                    Assert.Throws<ArgumentException>(() => StoragePaths.SaveRecoveredVaultKey(new byte[16]));

                    StoragePaths.SaveRecoveredVaultKey(rawKey);
                    byte[] loaded = StoragePaths.GetOrCreateVaultKey();
                    Assert.Equal(rawKey, loaded);

                    // Reload cache test
                    StoragePaths.ReloadVaultKeyCache();
                    byte[] reloaded = StoragePaths.GetOrCreateVaultKey();
                    Assert.Equal(rawKey, reloaded);
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

        [Fact]
        public void CleanupStaleRuntimeArtifacts_RemovesExpiredTempFilesAndBrowserSessions()
        {
            string isolated = Path.Combine(Path.GetTempPath(), "Shiro-Cleanup-" + Guid.NewGuid().ToString("N"));
            try
            {
                using (StoragePaths.OverrideDataDirectoryForTesting(isolated))
                {
                    string oldTmp = StoragePaths.GetUserFile("old_test.tmp-12345");
                    File.WriteAllText(oldTmp, "stale temp content");
                    File.SetLastWriteTimeUtc(oldTmp, DateTime.UtcNow.AddDays(-10));

                    string freshTmp = StoragePaths.GetUserFile("fresh_test.tmp-67890");
                    File.WriteAllText(freshTmp, "fresh temp content");

                    string browserRoot = StoragePaths.BrowserSessionsDirectory;
                    string staleBrowserDir = Path.Combine(browserRoot, "stale-session-1");
                    Directory.CreateDirectory(staleBrowserDir);
                    Directory.SetLastWriteTimeUtc(staleBrowserDir, DateTime.UtcNow.AddDays(-10));

                    string freshBrowserDir = Path.Combine(browserRoot, "fresh-session-1");
                    Directory.CreateDirectory(freshBrowserDir);

                    // Test with zero TimeSpan (defaults to 7 days)
                    int removed = StoragePaths.CleanupStaleRuntimeArtifacts(TimeSpan.Zero);
                    Assert.True(removed >= 2);
                    Assert.False(File.Exists(oldTmp));
                    Assert.True(File.Exists(freshTmp));
                    Assert.False(Directory.Exists(staleBrowserDir));
                    Assert.True(Directory.Exists(freshBrowserDir));
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

        [Fact]
        public async Task OverrideDataDirectoryForTesting_Dispose_DoesNotZeroConcurrentGlobalOrOtherScopeVaultKey()
        {
            string dirB = Path.Combine(Path.GetTempPath(), "ShiroVaultIsoB_" + Guid.NewGuid().ToString("N"));
            string dirC = Path.Combine(Path.GetTempPath(), "ShiroVaultIsoC_" + Guid.NewGuid().ToString("N"));
            try
            {
                byte[] globalKeyRef = StoragePaths.GetOrCreateVaultKey();
                byte[] expectedGlobal = globalKeyRef.ToArray();
                Assert.Equal(32, expectedGlobal.Length);
                Assert.Contains(expectedGlobal, b => b != 0);

                var taskBReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var taskCDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                var taskB = Task.Run(async () =>
                {
                    using var scopeB = StoragePaths.OverrideDataDirectoryForTesting(dirB);
                    byte[] keyB = StoragePaths.GetOrCreateVaultKey();
                    byte[] expectedB = keyB.ToArray();
                    taskBReady.TrySetResult(true);
                    await taskCDone.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    Assert.Equal(expectedB, keyB);
                    byte[] currentB = StoragePaths.GetOrCreateVaultKey();
                    Assert.Equal(expectedB, currentB);
                    Assert.NotSame(keyB, currentB);
                });

                var taskC = Task.Run(async () =>
                {
                    await taskBReady.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    using (StoragePaths.OverrideDataDirectoryForTesting(dirC))
                    {
                        byte[] keyC = StoragePaths.GetOrCreateVaultKey();
                        Assert.Equal(32, keyC.Length);
                    }
                    taskCDone.TrySetResult(true);
                });

                await Task.WhenAll(taskB, taskC);
                Assert.Equal(expectedGlobal, globalKeyRef);
                byte[] currentGlobal = StoragePaths.GetOrCreateVaultKey();
                Assert.Equal(expectedGlobal, currentGlobal);
                Assert.NotSame(globalKeyRef, currentGlobal);
            }
            finally
            {
                if (Directory.Exists(dirB)) Directory.Delete(dirB, true);
                if (Directory.Exists(dirC)) Directory.Delete(dirC, true);
            }
        }
    }
}

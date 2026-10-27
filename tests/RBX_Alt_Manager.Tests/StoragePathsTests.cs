using System;
using System.IO;
using RBX_Alt_Manager.Services;
using Xunit;

namespace RBX_Alt_Manager.Tests
{
    [Collection("StoragePathsCollection")]
    public class StoragePathsTests
    {
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
                    try { File.Delete(testFile); } catch { }
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
            Assert.Same(key1, key2);
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
            var vm = new RBX_Alt_Manager.ViewModels.MainViewModel();
            vm.SearchText = "abc";
            vm.SearchText = string.Empty;
            vm.SearchText = "t";
            vm.SearchText = "ta";
            vm.SearchText = "tan";
            vm.SearchText = null;
            vm.SearchText = "jackpot";
            Assert.Equal("jackpot", vm.SearchText);
        }

        [Fact]
        public void IsUserIdInBlockedListJson_MatchesExactUserIdOnly()
        {
            string json = @"{""blockedUsers"":[{""userId"":1234567,""name"":""PlayerA""},{""id"":987654,""name"":""PlayerB""}]}";

            Assert.True(RBX_Alt_Manager.Account.IsUserIdInBlockedListJson(json, 1234567));
            Assert.True(RBX_Alt_Manager.Account.IsUserIdInBlockedListJson(json, 987654));
            Assert.False(RBX_Alt_Manager.Account.IsUserIdInBlockedListJson(json, 123));
            Assert.False(RBX_Alt_Manager.Account.IsUserIdInBlockedListJson(json, 456));
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
                    try { Directory.Delete(baseDir, true); } catch { }
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
                    try { Directory.Delete(isolated, true); } catch { }
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
                    try { Directory.Delete(isolated, true); } catch { }
                }
            }
        }

        [Fact]
        public void CleanupStaleRuntimeArtifacts_RemovesExpiredTempFilesOnly()
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

                    int removed = StoragePaths.CleanupStaleRuntimeArtifacts(TimeSpan.FromDays(5));
                    Assert.True(removed >= 1);
                    Assert.False(File.Exists(oldTmp));
                    Assert.True(File.Exists(freshTmp));
                }
            }
            finally
            {
                if (Directory.Exists(isolated))
                {
                    try { Directory.Delete(isolated, true); } catch { }
                }
            }
        }
    }
}

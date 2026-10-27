using System;
using System.IO;
using RBX_Alt_Manager.Services;
using Xunit;

namespace RBX_Alt_Manager.Tests
{
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
    }
}

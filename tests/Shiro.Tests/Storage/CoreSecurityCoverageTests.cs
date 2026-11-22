#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public class CoreSecurityCoverageTests : IDisposable
    {
        private readonly string _tempDir;

        public CoreSecurityCoverageTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), $"ShiroCoreCov_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { /* ignore */ }
        }

        [Fact]
        public void StoragePaths_AtomicWriteAndCleanup_CoversBranches()
        {
            string testFile = Path.Combine(_tempDir, "sub", "test-atomic.dat");
            byte[] data = Encoding.UTF8.GetBytes("hello atomic storage");

            // 1. AtomicWriteBytes with durable = true
            StoragePaths.AtomicWriteBytes(testFile, data, durable: true);
            Assert.True(File.Exists(testFile));
            Assert.Equal(data, File.ReadAllBytes(testFile));

            // 2. AtomicWriteText with durable = true
            string textFile = Path.Combine(_tempDir, "sub", "test-text.txt");
            StoragePaths.AtomicWriteText(textFile, "sample text content", durable: true);
            Assert.True(File.Exists(textFile));
            Assert.Equal("sample text content", File.ReadAllText(textFile));

            // 3. CleanupStaleRuntimeArtifacts
            string browserRoot = StoragePaths.BrowserSessionsDirectory;
            Directory.CreateDirectory(browserRoot);
            string staleSessionDir = Path.Combine(browserRoot, "session_old_test");
            Directory.CreateDirectory(staleSessionDir);
            Directory.SetLastWriteTimeUtc(staleSessionDir, DateTime.UtcNow.AddHours(-3));

            string staleTmpFile = Path.Combine(StoragePaths.DataDirectory, $"stale.tmp-{Guid.NewGuid():N}");
            File.WriteAllText(staleTmpFile, "old tmp");
            File.SetLastWriteTimeUtc(staleTmpFile, DateTime.UtcNow.AddHours(-3));

            try
            {
                int removed = StoragePaths.CleanupStaleRuntimeArtifacts(TimeSpan.FromHours(1));
                Assert.True(removed >= 2, $"Expected at least 2 cleaned items, got {removed}");
                Assert.False(Directory.Exists(staleSessionDir));
                Assert.False(File.Exists(staleTmpFile));
            }
            finally
            {
                try { if (Directory.Exists(staleSessionDir)) Directory.Delete(staleSessionDir, true); } catch { }
                try { if (File.Exists(staleTmpFile)) File.Delete(staleTmpFile); } catch { }
            }

            // 4. ResolveUserFile root & relative
            string resolvedRoot = StoragePaths.ResolveUserFile(testFile);
            Assert.Equal(testFile, resolvedRoot);

            string resolvedRel = StoragePaths.ResolveUserFile("relative.txt");
            Assert.True(Path.IsPathRooted(resolvedRel));
        }

        [Fact]
        public void AccountStore_ReadWriteAndHistoricalBackups_CoversBranches()
        {
            string accFile = Path.Combine(_tempDir, "accounts.dat");
            string backupDir = Path.Combine(_tempDir, "backups");
            var store = new AccountStore(accFile, backupDir);

            Assert.Equal(accFile, store.AccountFile);
            Assert.Equal(accFile + ".backup", store.BackupFile);
            Assert.Equal(backupDir, store.BackupsDirectory);

            // 1. Initial read on non-existent file returns empty byte array
            byte[] initial = store.Read();
            Assert.Empty(initial);

            // 2. Direct Write
            byte[] testPayload1 = Encoding.UTF8.GetBytes("payload-v1");
            store.Write(testPayload1);
            Assert.True(File.Exists(accFile));
            Assert.Equal(testPayload1, store.Read());

            // 3. CreateBackup and CreateHistoricalBackup
            store.CreateBackup(testPayload1);
            Assert.True(File.Exists(store.BackupFile));
            Assert.Equal(testPayload1, File.ReadAllBytes(store.BackupFile));

            store.CreateHistoricalBackup(testPayload1);
            var backups = store.GetHistoricalBackups();
            Assert.NotEmpty(backups);

            // 4. WriteWithBackup
            byte[] testPayload2 = Encoding.UTF8.GetBytes("payload-v2");
            store.WriteWithBackup(testPayload2, forceHistoricalBackup: true);
            Assert.Equal(testPayload2, store.Read());

            // 5. RestoreHistoricalBackup
            bool restored = store.RestoreHistoricalBackup(backups[0]);
            Assert.True(restored);
            Assert.Equal(testPayload1, store.Read());
        }

        [Fact]
        public void AccountStore_ExportAndImportVault_CoversCoreSecurity()
        {
            string vaultPath = Path.Combine(_tempDir, "exported.vault");
            var accounts = new[]
            {
                new Account { Username = "VaultUser1", SecurityToken = "sec-tok-1", UserID = 112233 },
                new Account { Username = "VaultUser2", SecurityToken = "sec-tok-2", UserID = 445566 }
            };
            var store = new AccountStore(Path.Combine(_tempDir, "acc.dat"), Path.Combine(_tempDir, "bak"));

            store.ExportVault(accounts, "StrongPassphrase123!", vaultPath);
            Assert.True(File.Exists(vaultPath));

            var imported = store.ImportVault(vaultPath, "StrongPassphrase123!");
            Assert.Equal(2, imported.Count);
            Assert.Equal("VaultUser1", imported[0].Username);
            Assert.Equal("VaultUser2", imported[1].Username);
            Assert.Equal(112233, imported[0].UserID);
            Assert.Equal(445566, imported[1].UserID);
        }

        [Fact]
        public void AccountDataSerializer_VariousFormatsAndValidation_CoversBranches()
        {
            // Empty / null
            var empty = AccountDataSerializer.Deserialize(Array.Empty<byte>(), out bool reqMigEmpty);
            Assert.Empty(empty);
            Assert.False(reqMigEmpty);

            // Legacy array format
            byte[] legacyBytes = Encoding.UTF8.GetBytes("[{\"Username\":\"LegacyUser\",\"UserID\":999}]");
            var legacy = AccountDataSerializer.Deserialize(legacyBytes, out bool reqMigLegacy);
            Assert.Single(legacy);
            Assert.Equal("LegacyUser", legacy[0].Username);
            Assert.Equal(999, legacy[0].UserID);
            Assert.True(reqMigLegacy);

            // Serialize with passwords included vs omitted
            var accs = new[] { new Account { Username = "U1", Password = "SecretPassword123" } };
            byte[] serializedWithPw = AccountDataSerializer.Serialize(accs, includePasswords: true);
            string jsonWithPw = Encoding.UTF8.GetString(serializedWithPw);
            Assert.Contains("SecretPassword123", jsonWithPw);

            byte[] serializedNoPw = AccountDataSerializer.Serialize(accs, includePasswords: false);
            string jsonNoPw = Encoding.UTF8.GetString(serializedNoPw);
            Assert.DoesNotContain("SecretPassword123", jsonNoPw);

            // Validation exceptions
            Assert.Throws<InvalidDataException>(() => AccountDataSerializer.Deserialize(Encoding.UTF8.GetBytes("\"not an object or array\""), out _));
            Assert.Throws<InvalidDataException>(() => AccountDataSerializer.Deserialize(Encoding.UTF8.GetBytes("{\"schemaVersion\":1}"), out _));
            Assert.Throws<InvalidDataException>(() => AccountDataSerializer.Deserialize(Encoding.UTF8.GetBytes("{\"schemaVersion\":999,\"accounts\":[]}"), out _));
        }

        [Fact]
        public void StoragePaths_VaultKey_GetOrCreateAndReload_CoversBranches()
        {
            byte[] key1 = StoragePaths.GetOrCreateVaultKeyCopy();
            Assert.Equal(32, key1.Length);

            byte[] key2 = StoragePaths.GetOrCreateVaultKey();
            Assert.Equal(key1, key2);

            StoragePaths.ReloadVaultKeyCache();
            byte[] key3 = StoragePaths.GetOrCreateVaultKeyCopy();
            Assert.Equal(32, key3.Length);
            Assert.Equal(key1, key3);

            StoragePaths.ClearCachedVaultKey();
        }

        [Fact]
        public void AccountFileDecoder_DecodeEmpty_Encrypted_AndPlaintext_CoversBranches()
        {
            var dummyKeys = new AccountDecodeKeys(
                GetVaultKeyCopy: () => new byte[32],
                TryRecoverVaultKey: null,
                UnprotectDpapi: b => (b, false),
                PlaintextOptIn: false);

            // 1. Empty data
            var emptyRes = AccountFileDecoder.Decode(Array.Empty<byte>(), null, dummyKeys);
            Assert.Equal(AccountDecodeStatus.Empty, emptyRes.Status);
            Assert.Empty(emptyRes.Accounts);

            // 2. Encrypted data with master password
            byte[] rawAccounts = AccountDataSerializer.Serialize(new[] { new Account { Username = "EncUser", UserID = 777 } });
            byte[] masterKey = Encoding.UTF8.GetBytes("MyMasterPass1234");
            byte[] encrypted = Cryptography.Encrypt(rawAccounts, masterKey);

            var loadedRes = AccountFileDecoder.Decode(encrypted, masterKey, dummyKeys);
            Assert.Equal(AccountDecodeStatus.Loaded, loadedRes.Status);
            Assert.Single(loadedRes.Accounts);
            Assert.Equal("EncUser", loadedRes.Accounts[0].Username);

            // 3. Encrypted data with wrong master password
            byte[] wrongKey = Encoding.UTF8.GetBytes("WrongKey12345678");
            var needsPwRes = AccountFileDecoder.Decode(encrypted, wrongKey, dummyKeys);
            Assert.Equal(AccountDecodeStatus.NeedsMasterPassword, needsPwRes.Status);

            // 4. Plaintext with PlaintextOptIn = true
            var optInKeys = new AccountDecodeKeys(
                GetVaultKeyCopy: () => Array.Empty<byte>(),
                TryRecoverVaultKey: null,
                UnprotectDpapi: _ => throw new InvalidOperationException("Not DPAPI"),
                PlaintextOptIn: true);

            var plainRes = AccountFileDecoder.Decode(rawAccounts, null, optInKeys);
            Assert.Equal(AccountDecodeStatus.Loaded, plainRes.Status);
            Assert.Single(plainRes.Accounts);
            Assert.True(plainRes.LegacyPlaintext);
        }

        [Fact]
        public void StoragePaths_PropertiesAndDirectoryOverride_CoversBranches()
        {
            string isolatedTemp = Path.Combine(_tempDir, "isolated_override");
            using (StoragePaths.OverrideDataDirectoryForTesting(isolatedTemp))
            {
                Assert.Equal(isolatedTemp, StoragePaths.DataDirectory);
                Assert.Contains("AccountData.json", StoragePaths.AccountDataFile);
                Assert.Contains("AccountRuntimeState.json", StoragePaths.AccountRuntimeStateFile);
                Assert.Contains("RecentGames.json", StoragePaths.RecentGamesFile);
                Assert.Contains("RAMSettings.json", StoragePaths.SettingsFile);
                Assert.Contains("RAMSettings.ini", StoragePaths.LegacySettingsFile);
                Assert.Contains("Cache", StoragePaths.CacheDirectory);
                Assert.Contains("AccountDumps", StoragePaths.AccountDumpsDirectory);
                Assert.Contains("Logs", StoragePaths.LogDirectory);

                // Test raw 32-byte key upgrade to DPAPI format
                byte[] raw32 = new byte[32];
                for (int i = 0; i < 32; i++) raw32[i] = (byte)(i + 1);

                File.WriteAllBytes(StoragePaths.VaultKeyFile, raw32);
                StoragePaths.ReloadVaultKeyCache();

                byte[] key = StoragePaths.GetOrCreateVaultKey();
                Assert.Equal(raw32, key);

                // File should now be upgraded (larger than 32 bytes due to DPAPI wrapper and header)
                byte[] upgradedFileBytes = File.ReadAllBytes(StoragePaths.VaultKeyFile);
                Assert.True(upgradedFileBytes.Length > 32);
            }

            // IsWithinDirectory branches
            Assert.True(StoragePaths.IsWithinDirectory("C:\\Shiro\\Data", "C:\\Shiro"));
            Assert.True(StoragePaths.IsWithinDirectory("C:\\Shiro", "C:\\Shiro"));
            Assert.False(StoragePaths.IsWithinDirectory("C:\\Other\\Dir", "C:\\Shiro"));
            Assert.False(StoragePaths.IsWithinDirectory(string.Empty, "C:\\Shiro"));
            Assert.False(StoragePaths.IsWithinDirectory("C:\\Shiro", string.Empty));
            Assert.False(StoragePaths.IsWithinDirectory("invalid\0char", "C:\\Shiro"));

            // AtomicWriteText empty content and existing file overwrite
            string emptyFile = Path.Combine(_tempDir, "empty.txt");
            StoragePaths.AtomicWriteText(emptyFile, string.Empty);
            Assert.True(File.Exists(emptyFile));
            Assert.Empty(File.ReadAllBytes(emptyFile));

            // Overwrite existing file to test ReplaceFile
            StoragePaths.AtomicWriteText(emptyFile, "updated content 1");
            Assert.Equal("updated content 1", File.ReadAllText(emptyFile));
            StoragePaths.AtomicWriteText(emptyFile, "updated content 2");
            Assert.Equal("updated content 2", File.ReadAllText(emptyFile));
        }

        [Fact]
        public void StoragePaths_MigrationAndPathHelpers_CoversBranches()
        {
            string legacySrc = Path.Combine(_tempDir, "legacy_source");
            string migDst = Path.Combine(_tempDir, "migrated_dest");
            Directory.CreateDirectory(legacySrc);
            Directory.CreateDirectory(migDst);

            // Populate legacy files
            File.WriteAllText(Path.Combine(legacySrc, "AccountData.json"), "[{\"Username\":\"migrated\"}]");
            File.WriteAllText(Path.Combine(legacySrc, "RecentGames.json"), "{}");
            File.WriteAllText(Path.Combine(legacySrc, "vault.key"), "dummy-vault-key");

            Directory.CreateDirectory(Path.Combine(legacySrc, "AccountDumps"));
            File.WriteAllText(Path.Combine(legacySrc, "AccountDumps", "dump1.txt"), "dump");

            Directory.CreateDirectory(Path.Combine(legacySrc, "Cache"));
            File.WriteAllText(Path.Combine(legacySrc, "Cache", "avatar.png"), "image");

            Directory.CreateDirectory(Path.Combine(legacySrc, "Logs"));
            File.WriteAllText(Path.Combine(legacySrc, "Logs", "app.log"), "log");

            // Execute migration
            StoragePaths.MigrateLegacyFiles(legacySrc, migDst);

            // Assert destination received files
            Assert.True(File.Exists(Path.Combine(migDst, "AccountData.json")));
            Assert.True(File.Exists(Path.Combine(migDst, "AccountData.json.backup")));
            Assert.True(File.Exists(Path.Combine(migDst, "RecentGames.json")));
            Assert.True(File.Exists(Path.Combine(migDst, "vault.key")));
            Assert.True(File.Exists(Path.Combine(migDst, "AccountDumps", "dump1.txt")));
            Assert.True(File.Exists(Path.Combine(migDst, "Cache", "avatar.png")));
            Assert.True(File.Exists(Path.Combine(migDst, "Logs", "app.log")));

            // EnsureTrailingSeparator
            Assert.Equal(string.Empty, StoragePaths.EnsureTrailingSeparator(string.Empty));
            string withSep = StoragePaths.EnsureTrailingSeparator("C:\\test");
            Assert.EndsWith(Path.DirectorySeparatorChar.ToString(), withSep);
            Assert.Equal(withSep, StoragePaths.EnsureTrailingSeparator(withSep));

            // PathEquals
            Assert.True(StoragePaths.PathEquals("C:\\test\\dir", "C:\\test\\dir\\"));
            Assert.False(StoragePaths.PathEquals("C:\\test\\dir1", "C:\\test\\dir2"));
            Assert.False(StoragePaths.PathEquals("invalid\0char", "C:\\test\\dir"));
        }

        [Fact]
        public void StoragePaths_EnsureInitialized_WithUnitTestOverrideFalse_CoversProductionBranches()
        {
            // 1. Force Assembly Probe branch (UnitTestOverride = null, TestForceAssemblyProbe = true)
            try
            {
                StoragePaths.TestForceAssemblyProbe = true;
                StoragePaths.UnitTestOverride = null;
                StoragePaths.ResetInitializedForTesting();
                StoragePaths.EnsureInitialized();
                Assert.Contains("Shiro-UnitTests", StoragePaths.DataDirectory);
            }
            finally
            {
                StoragePaths.TestForceAssemblyProbe = false;
                StoragePaths.UnitTestOverride = null;
                StoragePaths.ResetInitializedForTesting();
                StoragePaths.EnsureInitialized();
            }

            // 2. Production default AppData branch (UnitTestOverride = false, TestForceAssemblyProbe = false)
            try
            {
                StoragePaths.TestForceAssemblyProbe = false;
                StoragePaths.UnitTestOverride = false;
                StoragePaths.ResetInitializedForTesting();
                StoragePaths.EnsureInitialized();
                Assert.False(string.IsNullOrEmpty(StoragePaths.DataDirectory));
                Assert.True(Directory.Exists(StoragePaths.DataDirectory));
            }
            finally
            {
                StoragePaths.TestForceAssemblyProbe = false;
                StoragePaths.UnitTestOverride = null;
                StoragePaths.ResetInitializedForTesting();
                StoragePaths.EnsureInitialized();
            }
        }
    }
}

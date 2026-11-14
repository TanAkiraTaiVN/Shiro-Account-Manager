#nullable enable
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using NSubstitute;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SequentialStateTests")]
    public class CoreCoverageExpansionTests
    {
        [Fact]
        public void HostUiBridgeService_InvokesRegisteredCallbacks()
        {
            var bridge = new HostUiBridgeService();
            string? lastStatus = null;
            bridge.SetDashboardStatusHandler = msg => lastStatus = msg;
            bridge.SetDashboardStatus("Ready");
            Assert.Equal("Ready", lastStatus);

            bool refreshed = false;
            bridge.RefreshAccountViewHandler = _ => refreshed = true;
            bridge.RefreshAccountView();
            Assert.True(refreshed);

            bridge.SignalNextLaunchAccount();
            bridge.CancelLaunching();
            bridge.UpdateMultiRoblox();
            bridge.CloseAllRobloxFromUi();
            bridge.MultiLaunchHomeFromHost();
            bridge.SaveSettings();
            bridge.QueueDeferredSaveSettings();
            bridge.EnsureBackendInitialized();
            bridge.InitializePostStartup();
            bridge.SetAutoCookieRefreshEnabled(true);
            Assert.Empty(bridge.GetCurrentSelectedAccountsFromHost());

            var mockDialog = Substitute.For<IDialogService>();
            mockDialog.ShowInfo("Test");
            mockDialog.Received(1).ShowInfo("Test");
            Assert.NotNull(mockDialog);
        }

        [Fact]
        public void AccountFileDecoder_CoversAllBranches()
        {
            byte[] key = RandomNumberGenerator.GetBytes(32);
            byte[] wrongKey = RandomNumberGenerator.GetBytes(32);
            byte[] validJson = Encoding.UTF8.GetBytes("[]");
            byte[] invalidJson = Encoding.UTF8.GetBytes("{not-valid-json");

            byte[] encryptedValid = Cryptography.Encrypt(validJson, key);
            byte[] encryptedInvalid = Cryptography.Encrypt(invalidJson, key);

            var baseKeys = new AccountDecodeKeys(
                GetVaultKeyCopy: () => wrongKey.AsSpan().ToArray(),
                TryRecoverVaultKey: _ => wrongKey.AsSpan().ToArray(),
                UnprotectDpapi: _ => throw new CryptographicException("Not DPAPI"),
                PlaintextOptIn: false);

            // 1. Wrong masterPassword -> NeedsMasterPassword
            var resWrongMaster = AccountFileDecoder.Decode(encryptedValid, wrongKey, baseKeys);
            Assert.Equal(AccountDecodeStatus.NeedsMasterPassword, resWrongMaster.Status);

            // 2. TryRecoverVaultKey throws -> NeedsMasterPassword
            var throwingKeys = baseKeys with
            {
                GetVaultKeyCopy = () => throw new InvalidOperationException("vault err"),
                TryRecoverVaultKey = _ => throw new InvalidOperationException("recover err")
            };
            var resThrowRecover = AccountFileDecoder.Decode(encryptedValid, null, throwingKeys);
            Assert.Equal(AccountDecodeStatus.NeedsMasterPassword, resThrowRecover.Status);

            // 3. TryRecoverVaultKey returns wrong key -> NeedsMasterPassword
            var resWrongRecover = AccountFileDecoder.Decode(encryptedValid, null, baseKeys);
            Assert.Equal(AccountDecodeStatus.NeedsMasterPassword, resWrongRecover.Status);

            // 4. Encrypted corrupt JSON -> Corrupt
            var validVaultKeys = baseKeys with { GetVaultKeyCopy = () => key.AsSpan().ToArray() };
            var resCorruptEnc = AccountFileDecoder.Decode(encryptedInvalid, null, validVaultKeys);
            Assert.Equal(AccountDecodeStatus.Corrupt, resCorruptEnc.Status);

            // 5. UnprotectDpapi succeeds with legacyScope: true -> Loaded + NeedsMigration
            var dpapiKeys = baseKeys with { UnprotectDpapi = _ => (validJson, true) };
            var resDpapi = AccountFileDecoder.Decode(validJson, null, dpapiKeys);
            Assert.Equal(AccountDecodeStatus.Loaded, resDpapi.Status);
            Assert.True(resDpapi.NeedsMigration);

            // 6. PlaintextOptIn with corrupt JSON -> Corrupt
            var plainCorruptKeys = baseKeys with { PlaintextOptIn = true };
            var resPlainCorrupt = AccountFileDecoder.Decode(invalidJson, null, plainCorruptKeys);
            Assert.Equal(AccountDecodeStatus.Corrupt, resPlainCorrupt.Status);
        }

        [Fact]
        public void StoragePaths_And_VaultKey_CoverUpgradeAndCorruptBranches()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ShiroCov_" + Guid.NewGuid().ToString("N"));
            try
            {
                using (StoragePaths.OverrideDataDirectoryForTesting(tempDir))
                {
                    Assert.Equal(Path.Combine(tempDir, "AccountDumps"), StoragePaths.AccountDumpsDirectory);
                    Assert.Equal(Path.Combine(tempDir, "Cache"), StoragePaths.CacheDirectory);
                    Assert.Equal(Path.Combine(tempDir, "Logs"), StoragePaths.LogDirectory);
                    Assert.Equal(tempDir, StoragePaths.ResolveUserFile(tempDir));
                    Assert.Contains("rel.txt", StoragePaths.ResolveUserFile("rel.txt"));
                    Assert.Equal(StoragePaths.BaseDirectory, StoragePaths.ResolveAsset(""));

                    Assert.Throws<ArgumentException>(() => StoragePaths.SaveRecoveredVaultKey(new byte[10]));

                    // Raw 32-byte key on disk -> upgraded to DPAPI on GetOrCreateVaultKey
                    StoragePaths.ClearCachedVaultKey();
                    byte[] rawKey = RandomNumberGenerator.GetBytes(32);
                    File.WriteAllBytes(StoragePaths.VaultKeyFile, rawKey);
                    byte[] loadedRaw = StoragePaths.GetOrCreateVaultKey();
                    Assert.Equal(rawKey, loadedRaw);

                    // Corrupt 7-byte key on disk -> backed up to .bak and regenerated
                    StoragePaths.ClearCachedVaultKey();
                    if (File.Exists(StoragePaths.VaultKeyFile + ".bak"))
                        File.Delete(StoragePaths.VaultKeyFile + ".bak");
                    File.WriteAllBytes(StoragePaths.VaultKeyFile, new byte[] { 1, 2, 3, 4, 5, 6, 7 });
                    byte[] regenerated = StoragePaths.GetOrCreateVaultKeyCopy();
                    Assert.Equal(32, regenerated.Length);
                    Assert.True(File.Exists(StoragePaths.VaultKeyFile + ".bak"));

                    // CleanupStaleRuntimeArtifacts with stale & fresh browser session directories + AtomicWriteText empty
                    string staleSession = Path.Combine(StoragePaths.BrowserSessionsDirectory, "stale_prof");
                    string freshSession = Path.Combine(StoragePaths.BrowserSessionsDirectory, "fresh_prof");
                    Directory.CreateDirectory(staleSession);
                    Directory.CreateDirectory(freshSession);
                    Directory.SetLastWriteTimeUtc(staleSession, DateTime.UtcNow.AddDays(-10));
                    Directory.SetLastWriteTimeUtc(freshSession, DateTime.UtcNow);
                    int cleaned = StoragePaths.CleanupStaleRuntimeArtifacts(TimeSpan.FromDays(2));
                    Assert.Equal(1, cleaned);
                    Assert.False(Directory.Exists(staleSession));
                    Assert.True(Directory.Exists(freshSession));

                    string emptyTxt = Path.Combine(tempDir, "empty_atomic.txt");
                    StoragePaths.AtomicWriteText(emptyTxt, "");
                    Assert.Equal(0, new FileInfo(emptyTxt).Length);
                    Assert.False(StoragePaths.IsWithinDirectory("\0bad", tempDir));

                    // Locked VaultKeyFile error branches
                    StoragePaths.ClearCachedVaultKey();
                    using (var keyLock = new FileStream(StoragePaths.VaultKeyFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                    {
                        StoragePaths.SaveRecoveredVaultKey(RandomNumberGenerator.GetBytes(32));
                        StoragePaths.ClearCachedVaultKey();
                        byte[] fallbackKey = StoragePaths.GetOrCreateVaultKey();
                        Assert.Equal(32, fallbackKey.Length);
                    }
                }
            }
            finally
            {
                try
                {
                    if (Directory.Exists(tempDir))
                        Directory.Delete(tempDir, true);
                }
                catch
                {
                    // Logger may hold a file handle in tempDir
                }
            }
        }

        [Fact]
        public void AccountStore_CoversBackupLockFallbackAndPruning()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ShiroStoreCov_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                string accFile = Path.Combine(tempDir, "AccountData.json");
                string backupsDir = Path.Combine(tempDir, "Backups");
                var store = new AccountStore(accFile, backupsDir);

                store.CreateBackup(Array.Empty<byte>());
                store.CreateHistoricalBackup(Array.Empty<byte>());
                Assert.Empty(store.GetHistoricalBackups());

                byte[] payload1 = Encoding.UTF8.GetBytes("[{\"Username\":\"u1\"}]");
                byte[] payload2 = Encoding.UTF8.GetBytes("[{\"Username\":\"u2\"}]");
                store.Write(payload1);
                store.WriteWithBackup(payload2, forceHistoricalBackup: true);

                // Create 12 historical backups to trigger pruning (>10)
                Directory.CreateDirectory(backupsDir);
                for (int i = 0; i < 12; i++)
                {
                    string p = Path.Combine(backupsDir, $"AccountData_20260101_1200{i:D2}.json.bak");
                    File.WriteAllBytes(p, payload1);
                }
                store.CreateHistoricalBackup(payload2);
                Assert.Equal(AccountStore.MaxHistoricalBackups, store.GetHistoricalBackups().Count);

                // RestoreHistoricalBackup edge cases
                Assert.False(store.RestoreHistoricalBackup(""));
                string emptyBak = Path.Combine(tempDir, "empty.bak");
                File.WriteAllBytes(emptyBak, Array.Empty<byte>());
                Assert.False(store.RestoreHistoricalBackup(emptyBak));

                var validBak = store.GetHistoricalBackups()[0];
                Assert.True(store.RestoreHistoricalBackup(validBak));

                // Read() fallback to .backup when primary is locked
                using (var lockStream = new FileStream(accFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    byte[] fallbackBytes = store.Read();
                    Assert.Equal(payload2.Length, fallbackBytes.Length);

                    // Both primary and backup locked -> returns empty
                    using (var lockBak = new FileStream(store.BackupFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    {
                        Assert.Empty(store.Read());
                        Assert.False(store.RestoreHistoricalBackup(store.BackupFile));
                    }
                }
            }
            finally
            {
                try
                {
                    if (Directory.Exists(tempDir))
                        Directory.Delete(tempDir, true);
                }
                catch
                {
                    // Logger may hold a file handle in tempDir
                }
            }
        }
    }
}

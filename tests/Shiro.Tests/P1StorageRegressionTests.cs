#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public class P1StorageRegressionTests
    {
        private const string OkJsonLiteral = "{\"ok\":1}";

        private static string CreateTempDir(string prefix)
        {
            string dir = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void SafeDeleteDir(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
            catch (IOException) { }
        }

        private static string FindRepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "Shiro.sln")))
                    return dir.FullName;
                dir = dir.Parent;
            }
            throw new DirectoryNotFoundException("Could not find repository root.");
        }

        [Fact]
        public void HighA4_Program_LogUnhandled_RedactsAndDoesNotDumpRawExceptionObject()
        {
            string programSrc = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Shiro", "Program.cs"));
            int logUnhandledIdx = programSrc.IndexOf("private static void LogUnhandled", StringComparison.Ordinal);
            Assert.True(logUnhandledIdx > 0);
            string methodBody = programSrc.Substring(logUnhandledIdx, 1400);
            Assert.DoesNotContain("stack trace: {exception}", methodBody, StringComparison.Ordinal);
            Assert.Contains("LogRedactor.Redact", methodBody, StringComparison.Ordinal);
        }

        [Fact]
        public void HighC1_StoragePaths_LoggerResolvesDynamicallyFromAppServicesOverride()
        {
            StoragePaths.Logger = null;
            var fakeLogger = Substitute.For<IAppLogger>();
            using (AppServices.Override<IAppLogger>(fakeLogger))
            {
                Assert.Same(fakeLogger, StoragePaths.Logger);
            }
        }

        [Fact]
        public void HighC2_AccountFileDecoder_ValidDecryptionWithCorruptJson_ReturnsCorruptNotNeedsMasterPassword()
        {
            byte[] masterHash = SHA512.HashData(Encoding.UTF8.GetBytes("ValidMasterPass1!"));
            byte[] invalidJsonEncrypted = Cryptography.Encrypt(Encoding.UTF8.GetBytes("<<<NOT_VALID_JSON>>>"), masterHash);

            var result = AccountFileDecoder.Decode(
                invalidJsonEncrypted,
                masterHash,
                new AccountDecodeKeys(() => Array.Empty<byte>(), null, _ => (Array.Empty<byte>(), false), PlaintextOptIn: false));

            Assert.Equal(AccountDecodeStatus.Corrupt, result.Status);
        }

        [Fact]
        public void HighC4_PersistenceRecovery_UsesGetOrCreateVaultKeyCopy_AndHandlesRecoveredKeys()
        {
            string recoverySrc = File.ReadAllText(Path.Combine(
                FindRepoRoot(), "src", "Shiro", "Services", "Storage", "AccountPersistenceService.Recovery.cs"));

            Assert.DoesNotContain("StoragePaths.GetOrCreateVaultKey()", recoverySrc, StringComparison.Ordinal);
            Assert.Contains("GetOrCreateVaultKeyCopy", recoverySrc, StringComparison.Ordinal);
        }

        [Fact]
        public void HighC5_CryptoHelper_And_AccountExport_PreventDpapiBypassAndCsvNewlineFormulaInjection()
        {
            string trickyPlaintext = "dpapi:NotActuallyProtected123";
            string encrypted = CryptoHelper.ProtectSecretString(trickyPlaintext);

            Assert.NotEqual(trickyPlaintext, encrypted);
            Assert.StartsWith("dpapi:", encrypted, StringComparison.Ordinal);
            Assert.Equal(trickyPlaintext, CryptoHelper.UnprotectSecretString(encrypted));

            var acc = new Account
            {
                Username = "csvuser",
                Alias = "\n=cmd|'/C calc'!A0",
                Group = " =1+1"
            };
            string csv = AccountExportService.ExportAccounts(new[] { acc }, AccountExportFormat.Csv);
            Assert.Contains("\"'\n=cmd|'/C calc'!A0\"", csv, StringComparison.Ordinal);
            Assert.Contains("\"' =1+1\"", csv, StringComparison.Ordinal);
        }

        [Fact]
        public void HighC6_RuntimeStateStore_IsolatedOverride_And_AccountRepository_IsolatedPersistence()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ShiroHighC6_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                string fileA = Path.Combine(tempDir, "stateA.json");
                string fileB = Path.Combine(tempDir, "stateB.json");
                AccountRuntimeStateStore.ResetInMemoryCacheForTests();

                var accA = new Account { Username = "UserStateA", UserID = 111 };
                AccountRuntimeStateStore.RecordLastUse(accA, new DateTime(2025, 1, 2, 3, 4, 5), filePathOverride: fileA);
                Assert.True(AccountRuntimeStateStore.Flush(filePathOverride: fileA));

                // Hydrating a clean file B with different path must NOT receive UserStateA from file A's cache!
                var accB = new Account { Username = "UserStateA", UserID = 111, LastUse = DateTime.MinValue };
                AccountRuntimeStateStore.HydrateAccounts(new[] { accB }, filePathOverride: fileB);
                Assert.Equal(DateTime.MinValue, accB.LastUse);

                // Standalone AccountRepository must not mutate AccountRepository.Default
                var isolatedRepo = new AccountRepository();
                isolatedRepo.AddThreadSafe(new Account { Username = "IsolatedRepoUser", UserID = 909 });
                Assert.Null(AccountRepository.Default.GetByUsername("IsolatedRepoUser"));
                var regService = new AccountRegistrationService(isolatedRepo, new AccountPersistenceService(isolatedRepo));
                Parallel.For(0, 8, _ =>
                {
                    regService.RegisterValidatedAccount(
                        new Account { Username = "AtomicRegUser", UserID = 777111, Valid = true },
                        "tok",
                        "pass",
                        saveImmediate: false);
                });
                Assert.Single(isolatedRepo.GetAll(), a => a.Username == "AtomicRegUser");
            }
            finally
            {
                AccountRuntimeStateStore.ResetInMemoryCacheForTests();
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); }
                catch (IOException) { }
            }
        }

        [Fact]
        public void HighC7_LogRedactor_RedactsTelegramBearerBase64JsonAndPreservesAmpersand_And_AppLoggerDrainsOnDispose()
        {
            string url = "https://host.local/path?a=1&token=supersecret&b=2";
            string redactedUrl = LogRedactor.Redact(url);
            Assert.Contains("&token=[REDACTED]", redactedUrl, StringComparison.Ordinal);
            Assert.DoesNotContain("supersecret", redactedUrl, StringComparison.Ordinal);

            string tg = "Request to https://api.telegram.org/bot123456:ABC-DEF1234/sendMessage failed";
            string redactedTg = LogRedactor.Redact(tg);
            Assert.DoesNotContain("123456:ABC-DEF1234", redactedTg, StringComparison.Ordinal);

            string bearer = "Authorization: Bearer abc+def/123==";
            string redactedBearer = LogRedactor.Redact(bearer);
            Assert.Equal("Authorization: Bearer [REDACTED_TOKEN]", redactedBearer);

            string csrfJson = "{\"CSRFToken\":\"csrf_secret_99\",\"authenticationTicket\":\"ticket_123\"}";
            string redactedJson = LogRedactor.Redact(csrfJson);
            Assert.DoesNotContain("csrf_secret_99", redactedJson, StringComparison.Ordinal);
            Assert.DoesNotContain("ticket_123", redactedJson, StringComparison.Ordinal);

            string socks5h = "socks5h://proxyUser:proxyPass@10.0.0.1:1080";
            Assert.DoesNotContain("proxyPass", LogRedactor.Redact(socks5h), StringComparison.Ordinal);

            string logDir = Path.Combine(Path.GetTempPath(), "ShiroAppLoggerDrain_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(logDir);
            try
            {
                using (var logger = new AppLogger(logDir, "DrainTest"))
                {
                    for (int i = 0; i < 200; i++)
                        logger.Info($"DrainEntry_{i:D3}");
                }

                string logContent = File.ReadAllText(Path.Combine(logDir, "log.txt"));
                Assert.Contains("DrainEntry_199", logContent, StringComparison.Ordinal);
            }
            finally
            {
                try { if (Directory.Exists(logDir)) Directory.Delete(logDir, recursive: true); }
                catch (IOException) { }
            }
        }

        [Fact]
        public void Task1_SaveRecoveredVaultKey_WhenWriteFails_DoesNotUpdateInMemoryCache()
        {
            string tempDir = CreateTempDir("ShiroT1KeyFail_");
            try
            {
                using var dirScope = StoragePaths.OverrideDataDirectoryForTesting(tempDir);
                StoragePaths.ReloadVaultKeyCache();
                byte[] originalKey = StoragePaths.GetOrCreateVaultKeyCopy();
                byte[] replacement = new byte[32];
                Array.Fill(replacement, (byte)0x5A);

                using (var lockHandle = new FileStream(StoragePaths.VaultKeyFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    Exception? ex = Record.Exception(() => StoragePaths.SaveRecoveredVaultKey(replacement));
                    Assert.NotNull(ex);
                    Assert.True(ex is IOException or UnauthorizedAccessException);
                }

                Assert.Equal(originalKey, StoragePaths.GetOrCreateVaultKeyCopy());
                StoragePaths.ReloadVaultKeyCache();
                Assert.Equal(originalKey, StoragePaths.GetOrCreateVaultKeyCopy());
            }
            finally
            {
                StoragePaths.ReloadVaultKeyCache();
                SafeDeleteDir(tempDir);
            }
        }

        [Fact]
        public void Task1_PromoteBackupToPrimary_WhenBackupMissing_LeavesPrimaryIntact()
        {
            string tempDir = CreateTempDir("ShiroT1Promote_");
            try
            {
                string primary = Path.Combine(tempDir, "AccountData.json");
                File.WriteAllText(primary, OkJsonLiteral);
                var svc = new AccountPersistenceService(new AccountRepository());

                string preserved = svc.PromoteBackupToPrimary(primary, DateTime.UtcNow);
                Assert.Equal(string.Empty, preserved);
                Assert.True(File.Exists(primary));
                Assert.Equal(OkJsonLiteral, File.ReadAllText(primary));
            }
            finally
            {
                SafeDeleteDir(tempDir);
            }
        }

        [Fact]
        public void Task1_TryRestoreReadableBackup_RecoversKeyAndHydratesRuntimeState()
        {
            string tempDir = CreateTempDir("ShiroT1Restore_");
            try
            {
                using var dirScope = StoragePaths.OverrideDataDirectoryForTesting(tempDir);
                StoragePaths.ReloadVaultKeyCache();
                AccountRuntimeStateStore.ResetInMemoryCacheForTests();

                byte[] realKey = RandomNumberGenerator.GetBytes(32);
                StoragePaths.SaveRecoveredVaultKey(realKey);
                byte[] wrongKey = RandomNumberGenerator.GetBytes(32);

                var srcAcc = new Account { Username = "RecoveredBackupUser", UserID = 4242, Valid = true, LastUse = DateTime.MinValue };
                byte[] encryptedBackup = Cryptography.Encrypt(AccountDataSerializer.Serialize(new[] { srcAcc }, true), realKey);
                File.WriteAllBytes(StoragePaths.AccountDataFile + ".backup", encryptedBackup);
                File.WriteAllText(StoragePaths.AccountDataFile, "corrupt-primary");

                DateTime expectedLastUse = new DateTime(2026, 5, 6, 7, 8, 9, DateTimeKind.Utc);
                AccountRuntimeStateStore.RecordLastUse(srcAcc, expectedLastUse);
                AccountRuntimeStateStore.RecordWindowBounds(srcAcc, 100, 120, 900, 700);
                AccountRuntimeStateStore.Flush();

                StoragePaths.SaveRecoveredVaultKey(wrongKey);
                // Re-write realKey into .bak only so primary vault.key has wrongKey and .bak has realKey
                File.WriteAllBytes(StoragePaths.VaultKeyFile + ".bak", realKey);
                StoragePaths.ReloadVaultKeyCache();

                var repo = new AccountRepository();
                var svc = new AccountPersistenceService(repo) { Store = new AccountStore(StoragePaths.AccountDataFile, StoragePaths.BackupsDirectory) };
                Assert.True(svc.TryRestoreReadableBackup());

                var restored = Assert.Single(repo.GetAll());
                Assert.Equal("RecoveredBackupUser", restored.Username);
                Assert.Equal(expectedLastUse, restored.LastUse);
                Assert.Equal("900", restored.GetField("Window_Width"));
            }
            finally
            {
                AccountRuntimeStateStore.ResetInMemoryCacheForTests();
                StoragePaths.ReloadVaultKeyCache();
                SafeDeleteDir(tempDir);
            }
        }

        [Fact]
        public void Task1_AccountFileDecoder_CurrentUserDpapi_SetsNeedsMigrationTrue()
        {
            byte[] plain = AccountDataSerializer.Serialize(new[] { new Account { Username = "DpapiUser", UserID = 1001 } }, true);
            var keys = new AccountDecodeKeys(() => Array.Empty<byte>(), null, _ => (plain, false), PlaintextOptIn: false);

            var result = AccountFileDecoder.Decode(Encoding.UTF8.GetBytes("dpapi-blob"), null, keys);
            Assert.Equal(AccountDecodeStatus.Loaded, result.Status);
            Assert.True(result.NeedsMigration);
            Assert.False(result.LegacyPlaintext);
            Assert.Single(result.Accounts);
        }

        [Fact]
        public void Task1_Account_WindowFieldsExcluded_NullGroupNotDirty_AndOversizedDebugLogged()
        {
            var fakeLogger = Substitute.For<IAppLogger>();
            using var logScope = AppServices.Override<IAppLogger>(fakeLogger);

            var acc = new Account { Username = "u", Group = "Default" };
            acc.IsDirty = false;
            acc.Group = null!;
            Assert.Equal("Default", acc.Group);
            Assert.False(acc.IsDirty);

            acc.SetWindowBounds(10, 20, 800, 600);
            var snapshot = acc.GetSerializedFieldsSnapshot();
            Assert.DoesNotContain("Window_Position_X", snapshot.Keys);
            Assert.DoesNotContain("Window_Position_Y", snapshot.Keys);
            Assert.DoesNotContain("Window_Width", snapshot.Keys);
            Assert.DoesNotContain("Window_Height", snapshot.Keys);

            acc.Alias = new string('A', 51);
            acc.Description = new string('D', 5001);
            acc.Password = new string('P', 5001);
            acc.SetField("CustomOversized", new string('F', 5001));
            Assert.Equal(string.Empty, acc.Alias);
            Assert.Equal(string.Empty, acc.Description);
            Assert.Equal(string.Empty, acc.Password);
            Assert.Equal(string.Empty, acc.GetField("CustomOversized"));
            fakeLogger.Received().Debug(Arg.Is<string>(s => s.Contains("Alias", StringComparison.Ordinal)));
            fakeLogger.Received().Debug(Arg.Is<string>(s => s.Contains("Description", StringComparison.Ordinal)));
            fakeLogger.Received().Debug(Arg.Is<string>(s => s.Contains("Password", StringComparison.Ordinal) && !s.Contains("PPPP", StringComparison.Ordinal)));
            fakeLogger.Received().Debug(Arg.Is<string>(s => s.Contains("CustomOversized", StringComparison.Ordinal)));
        }

        [Fact]
        public void Task2_CryptoHelper_PortableMode_UsesVaultAesGcmEnvelope_AndBackwardCompatDpapi()
        {
            string tempDir = CreateTempDir("ShiroT2Portable_");
            string portableMarker = Path.Combine(AppContext.BaseDirectory, "portable.mode");
            bool hadMarkerBefore = File.Exists(portableMarker);
            try
            {
                string legacyDpapi = CryptoHelper.ProtectSecretString("legacy-dpapi-secret");
                Assert.StartsWith("dpapi:", legacyDpapi, StringComparison.Ordinal);

                using var dirScope = StoragePaths.OverrideDataDirectoryForTesting(tempDir);
                File.WriteAllText(portableMarker, "portable");
                StoragePaths.ResetInitializedForTesting();
                StoragePaths.EnsureInitialized();
                StoragePaths.ReloadVaultKeyCache();
                Assert.True(StoragePaths.IsPortable);

                string encrypted = CryptoHelper.ProtectSecretString("portable-secret-123");
                Assert.StartsWith("vault:", encrypted, StringComparison.Ordinal);
                Assert.DoesNotContain("portable-secret-123", encrypted, StringComparison.Ordinal);
                Assert.Equal("portable-secret-123", CryptoHelper.UnprotectSecretString(encrypted));
                Assert.Equal("legacy-dpapi-secret", CryptoHelper.UnprotectSecretString(legacyDpapi));

                byte[] rawPayload = Convert.FromBase64String(encrypted.Substring("vault:".Length));
                rawPayload[^1] ^= 0xFF;
                string tampered = "vault:" + Convert.ToBase64String(rawPayload);
                Assert.Equal(string.Empty, CryptoHelper.UnprotectSecretString(tampered));
                Assert.Equal(string.Empty, CryptoHelper.UnprotectSecretString("vault:not-valid-base64!!!"));
                Assert.Equal(string.Empty, CryptoHelper.UnprotectSecretString("vault:AAAA"));
            }
            finally
            {
                if (!hadMarkerBefore && File.Exists(portableMarker))
                {
                    try { File.Delete(portableMarker); }
                    catch (IOException) { }
                }
                StoragePaths.ResetInitializedForTesting();
                StoragePaths.EnsureInitialized();
                StoragePaths.ReloadVaultKeyCache();
                SafeDeleteDir(tempDir);
            }
        }

        [Fact]
        public void Task2_CryptoHelper_FixedTimeEqualsSecret_MatchesAndRejectsShortAndLongSecrets()
        {
            Assert.True(CryptoHelper.FixedTimeEqualsSecret("short-secret", "short-secret"));
            Assert.False(CryptoHelper.FixedTimeEqualsSecret("short-secret", "short-secrex"));
            Assert.False(CryptoHelper.FixedTimeEqualsSecret("short-secret", "short-secret-2"));
            Assert.False(CryptoHelper.FixedTimeEqualsSecret(null!, "short-secret"));
            Assert.False(CryptoHelper.FixedTimeEqualsSecret("short-secret", null!));

            string longA = new string('K', 400);
            string longB = new string('K', 399) + "X";
            Assert.True(CryptoHelper.FixedTimeEqualsSecret(longA, new string('K', 400)));
            Assert.False(CryptoHelper.FixedTimeEqualsSecret(longA, longB));
        }

        [Fact]
        public async Task Task2_SettingsSideEffectsService_ExportAndImportVaultAsync_OffloadPbkdf2Work()
        {
            string tempDir = CreateTempDir("ShiroT2AsyncVault_");
            string exportPath = Path.Combine(tempDir, "nested", "offloaded.shirovault");
            var prevPicker = SettingsSideEffectsService.TestPickExportPathOverride;
            var prevCtx = SynchronizationContext.Current;
            var syncCtx = new TrackingSyncContext();
            SynchronizationContext.SetSynchronizationContext(syncCtx);
            ToastService.ClearAll();
            try
            {
                using var dirScope = StoragePaths.OverrideDataDirectoryForTesting(tempDir);
                var stored = new List<Account>
                {
                    new() { Username = "OffloadVaultUser", UserID = 7007, SecurityToken = "tok-offload", Valid = true }
                };
                var repo = Substitute.For<IAccountRepository>();
                repo.GetAll().Returns(_ => stored.ToList());
                repo.When(r => r.AddThreadSafe(Arg.Any<Account>())).Do(ci => stored.Add(ci.Arg<Account>()));
                repo.SaveAsync(Arg.Any<bool>()).Returns(Task.CompletedTask);

                var dialogs = Substitute.For<IDialogService>();
                dialogs.ShowSecretPromptAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
                    .Returns(Task.FromResult("OffloadPass#123"));
                var host = Substitute.For<IHostUiBridgeService>();

                using var repoScope = AppServices.Override<IAccountRepository>(repo);
                using var dialogScope = AppServices.Override<IDialogService>(dialogs);
                using var hostScope = AppServices.Override<IHostUiBridgeService>(host);

                SettingsSideEffectsService.TestPickExportPathOverride =
                    _ => Task.FromResult<(bool Confirmed, string? Path)>((true, exportPath));

                bool exported = await SettingsSideEffectsService.Default.ExportVaultAsync();
                Assert.True(exported);
                Assert.True(File.Exists(exportPath));
                Assert.True(syncCtx.PostCount >= 1);

                syncCtx.Reset();
                stored.Clear();

                bool imported = await SettingsSideEffectsService.Default.ImportVaultFromPathAsync(exportPath);
                Assert.True(imported);
                Assert.True(syncCtx.PostCount >= 1);
                Assert.Single(stored);
                Assert.Equal("OffloadVaultUser", stored[0].Username);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(prevCtx);
                SettingsSideEffectsService.TestPickExportPathOverride = prevPicker;
                ToastService.ClearAll();
                SafeDeleteDir(tempDir);
            }
        }

        private sealed class TrackingSyncContext : SynchronizationContext
        {
            private int _postCount;
            public int PostCount => Volatile.Read(ref _postCount);
            public void Reset() => Interlocked.Exchange(ref _postCount, 0);

            public override void Post(SendOrPostCallback d, object? state)
            {
                Interlocked.Increment(ref _postCount);
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    var prev = Current;
                    SetSynchronizationContext(this);
                    try { d(state); }
                    finally { SetSynchronizationContext(prev); }
                });
            }
        }
    }
}

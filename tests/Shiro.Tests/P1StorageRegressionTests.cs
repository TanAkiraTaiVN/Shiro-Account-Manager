#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using NSubstitute;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public class P1StorageRegressionTests
    {
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
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Shiro.Classes;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    public class VaultExportTests
    {
        [Fact]
        public void ExportAndImportVault_WithCorrectPassphrase_RestoresAccountsMatchingOriginal()
        {
            var originalAccounts = new List<Account>
            {
                new Account("UserAlpha", "Pass1", "AliasA") { SecurityToken = "_|WARNING:1" },
                new Account("UserBeta", "Pass2", "AliasB") { SecurityToken = "_|WARNING:2" }
            };

            using var ms = new MemoryStream();
            VaultExportService.ExportVault(originalAccounts, "SecurePassphrase123!", ms);

            ms.Position = 0;
            var restoredAccounts = VaultExportService.ImportVault(ms, "SecurePassphrase123!");

            Assert.Equal(2, restoredAccounts.Count);
            Assert.Equal("UserAlpha", restoredAccounts[0].Username);
            Assert.Equal("_|WARNING:1", restoredAccounts[0].SecurityToken);
        }

        [Fact]
        public void ImportVault_WithIncorrectPassphrase_ThrowsCryptographicException()
        {
            var originalAccounts = new List<Account> { new Account("UserAlpha", "Pass1", "AliasA") };
            using var ms = new MemoryStream();
            VaultExportService.ExportVault(originalAccounts, "CorrectPass123!", ms);

            ms.Position = 0;
            Assert.Throws<CryptographicException>(() =>
            {
                VaultExportService.ImportVault(ms, "WrongPass!");
            });
        }

        [Fact]
        public void ImportVault_WithCorruptedHeader_ThrowsInvalidDataException()
        {
            byte[] invalidData = new byte[64];
            RandomNumberGenerator.Fill(invalidData); // random data, bad magic

            using var ms = new MemoryStream(invalidData);
            Assert.Throws<InvalidDataException>(() =>
            {
                VaultExportService.ImportVault(ms, "AnyPass123!");
            });
        }

        [Fact]
        public void ImportVault_WithTamperedPayload_ThrowsCryptographicException()
        {
            var originalAccounts = new List<Account> { new Account("UserAlpha", "Pass1", "AliasA") };
            using var ms = new MemoryStream();
            VaultExportService.ExportVault(originalAccounts, "Pass123!", ms);

            byte[] vaultBytes = ms.ToArray();
            // Tamper with the last byte (ciphertext)
            vaultBytes[^1] ^= 0xFF;

            using var tamperedMs = new MemoryStream(vaultBytes);
            Assert.Throws<CryptographicException>(() =>
            {
                VaultExportService.ImportVault(tamperedMs, "Pass123!");
            });
        }

        [Fact]
        public void ExportVault_WithNullArguments_ThrowsArgumentExceptions()
        {
            using var ms = new MemoryStream();
            Assert.Throws<ArgumentNullException>(() => VaultExportService.ExportVault(null!, "pass", ms));
            Assert.Throws<ArgumentException>(() => VaultExportService.ExportVault([], "", ms));
            Assert.Throws<ArgumentNullException>(() => VaultExportService.ExportVault([], "pass", null!));
        }

        [Fact]
        public void ImportVault_WithNullArguments_ThrowsArgumentExceptions()
        {
            using var ms = new MemoryStream();
            Assert.Throws<ArgumentNullException>(() => VaultExportService.ImportVault(null!, "pass"));
            Assert.Throws<ArgumentException>(() => VaultExportService.ImportVault(ms, ""));
        }

        [Fact]
        public void AccountStore_ExportAndImportVault_RoundtripsSuccessfully()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ShiroVaultTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                string vaultPath = Path.Combine(tempDir, "exported.vault");
                var store = new AccountStore(Path.Combine(tempDir, "AccountData.json"), tempDir);
                var accounts = new List<Account>
                {
                    new Account("CrossDeviceUser", "P@ssw0rd!", "Alt1") { SecurityToken = "_|COOKIE123" }
                };

                store.ExportVault(accounts, "VaultKey#999", vaultPath);
                Assert.True(File.Exists(vaultPath));

                var restored = store.ImportVault(vaultPath, "VaultKey#999");
                Assert.Single(restored);
                Assert.Equal("CrossDeviceUser", restored[0].Username);
                Assert.Equal("_|COOKIE123", restored[0].SecurityToken);
                Assert.Equal("P@ssw0rd!", restored[0].Password);
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, recursive: true); } catch { }
                }
            }
        }
    }
}

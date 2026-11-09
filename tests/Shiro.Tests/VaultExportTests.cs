using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
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

        [Fact]
        public void AccountStore_ExportVault_WhenPassphraseEmpty_DoesNotCorruptExistingDestination()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ShiroVaultAtomicTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                string vaultPath = Path.Combine(tempDir, "existing.vault");
                File.WriteAllText(vaultPath, "OriginalUncorruptedContent");

                var store = new AccountStore(Path.Combine(tempDir, "AccountData.json"), tempDir);
                var accounts = new List<Account>
                {
                    new Account("User1", "Pass1", "Alt1")
                };

                Assert.Throws<ArgumentException>(() => store.ExportVault(accounts, "", vaultPath));

                Assert.Equal("OriginalUncorruptedContent", File.ReadAllText(vaultPath));

                var tempFiles = Directory.GetFiles(tempDir, "*.tmp.*");
                Assert.Empty(tempFiles);
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, recursive: true); } catch { }
                }
            }
        }

        [Fact]
        public void ImportVault_WithLegacyV1Format_RestoresAccountsSuccessfully()
        {
            var originalAccounts = new List<Account>
            {
                new Account("LegacyExportUser", "PassV1", "AliasV1") { SecurityToken = "_|WARNING:V1" }
            };

            string passphrase = "LegacyPassphrase300K!";
            byte[] plaintext = AccountDataSerializer.Serialize(originalAccounts, includePasswords: true);
            byte[] salt = new byte[16];
            byte[] nonce = new byte[12];
            byte[] tag = new byte[16];
            byte[] ciphertext = new byte[plaintext.Length];
            RandomNumberGenerator.Fill(salt);
            RandomNumberGenerator.Fill(nonce);

            byte[] key = new byte[32];
            Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, key, VaultExportService.LegacyV1Pbkdf2Iterations, HashAlgorithmName.SHA256);
            using (var aesGcm = new AesGcm(key, 16))
            {
                aesGcm.Encrypt(nonce, plaintext, ciphertext, tag);
            }

            using var ms = new MemoryStream();
            ms.Write("SHIRO_VAULT_V1"u8);
            ms.Write(salt);
            ms.Write(nonce);
            ms.Write(tag);
            ms.Write(ciphertext);
            ms.Position = 0;

            var restored = VaultExportService.ImportVault(ms, passphrase);
            Assert.Single(restored);
            Assert.Equal("LegacyExportUser", restored[0].Username);
            Assert.Equal("_|WARNING:V1", restored[0].SecurityToken);
        }
    }
}

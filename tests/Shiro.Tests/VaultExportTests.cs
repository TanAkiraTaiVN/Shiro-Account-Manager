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

        [Fact]
        public void VaultExportService_NonSeekableOversizedStream_ThrowsInvalidDataException()
        {
            byte[] validHeader = BuildValidV2HeaderBytes();
            using var stream = new OversizedNonSeekableReadStream(
                validHeader,
                payloadBytes: (32L * 1024 * 1024) + 1024,
                maxChunkBytes: 64 * 1024);

            Assert.Throws<InvalidDataException>(() => VaultExportService.ImportVault(stream, "StrongPass123!"));
            Assert.InRange(stream.PayloadBytesDelivered, (32L * 1024 * 1024) + 1, (32L * 1024 * 1024) + (64L * 1024));
        }

        [Fact]
        public void VaultExportService_SeekableOversizedStream_ThrowsInvalidDataExceptionWithoutReadingPayload()
        {
            byte[] validHeader = BuildValidV2HeaderBytes();
            using var stream = new OversizedSeekableHeaderStream(validHeader, payloadBytes: (32L * 1024 * 1024) + 1);

            Assert.Throws<InvalidDataException>(() => VaultExportService.ImportVault(stream, "StrongPass123!"));
            Assert.False(stream.ReadPastHeaderAttempted);
        }

        [Fact]
        public void VaultExportService_NonSeekableChunkedValidVault_ImportsSuccessfully()
        {
            var original = new List<Account>
            {
                new Account("ChunkedUser", "ChunkPass!", "AliasC") { SecurityToken = "_|WARNING:CHUNK" }
            };
            using var exportMs = new MemoryStream();
            VaultExportService.ExportVault(original, "ChunkPassphrase123!", exportMs);

            using var chunked = new ChunkedNonSeekableReadStream(exportMs.ToArray(), maxChunkBytes: 17);
            var restored = VaultExportService.ImportVault(chunked, "ChunkPassphrase123!");

            Assert.Single(restored);
            Assert.Equal("ChunkedUser", restored[0].Username);
            Assert.Equal("ChunkPass!", restored[0].Password);
            Assert.Equal("_|WARNING:CHUNK", restored[0].SecurityToken);
        }

        private static byte[] BuildValidV2HeaderBytes()
        {
            byte[] header = new byte[58];
            "SHIRO_VAULT_V2"u8.CopyTo(header);
            RandomNumberGenerator.Fill(header.AsSpan(14));
            return header;
        }

        private sealed class OversizedNonSeekableReadStream(byte[] header, long payloadBytes, int maxChunkBytes) : Stream
        {
            private int _headerOffset;
            public long PayloadBytesDelivered { get; private set; }
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_headerOffset < header.Length)
                {
                    int toCopy = Math.Min(count, header.Length - _headerOffset);
                    Array.Copy(header, _headerOffset, buffer, offset, toCopy);
                    _headerOffset += toCopy;
                    return toCopy;
                }

                long remaining = payloadBytes - PayloadBytesDelivered;
                if (remaining <= 0) return 0;

                int toEmit = (int)Math.Min(Math.Min(count, maxChunkBytes), remaining);
                Array.Fill(buffer, (byte)0xA5, offset, toEmit);
                PayloadBytesDelivered += toEmit;
                return toEmit;
            }
        }

        private sealed class OversizedSeekableHeaderStream(byte[] header, long payloadBytes) : Stream
        {
            private long _position;
            public bool ReadPastHeaderAttempted { get; private set; }
            public override bool CanRead => true;
            public override bool CanSeek => true;
            public override bool CanWrite => false;
            public override long Length => header.Length + payloadBytes;
            public override long Position { get => _position; set => _position = value; }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_position < header.Length)
                {
                    int toCopy = (int)Math.Min(count, header.Length - _position);
                    Array.Copy(header, (int)_position, buffer, offset, toCopy);
                    _position += toCopy;
                    return toCopy;
                }

                ReadPastHeaderAttempted = true;
                throw new InvalidOperationException("Oversized seekable payload should not be read.");
            }
        }

        private sealed class ChunkedNonSeekableReadStream(byte[] data, int maxChunkBytes) : Stream
        {
            private int _offset;
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_offset >= data.Length) return 0;
                int toCopy = Math.Min(Math.Min(count, maxChunkBytes), data.Length - _offset);
                Array.Copy(data, _offset, buffer, offset, toCopy);
                _offset += toCopy;
                return toCopy;
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Shiro.Classes;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    public class AccountFileDecoderTests
    {
        private static byte[] Create32ByteKey(byte fill)
        {
            var key = new byte[32];
            Array.Fill(key, fill);
            return key;
        }

        private static AccountDecodeKeys CreateDummyKeys(
            Func<byte[]>? getVaultKey = null,
            Func<byte[], byte[]?>? tryRecover = null,
            Func<byte[], (byte[] Plain, bool LegacyScope)>? unprotectDpapi = null,
            bool plaintextOptIn = false)
        {
            return new AccountDecodeKeys(
                GetVaultKeyCopy: getVaultKey ?? (() => Create32ByteKey(1)),
                TryRecoverVaultKey: tryRecover,
                UnprotectDpapi: unprotectDpapi ?? (_ => throw new CryptographicException("DPAPI disabled in test")),
                PlaintextOptIn: plaintextOptIn);
        }

        [Fact]
        public void Decode_Empty_ReturnsEmpty()
        {
            var keys = CreateDummyKeys();

            var result = AccountFileDecoder.Decode(Array.Empty<byte>(), null, keys);

            Assert.Equal(AccountDecodeStatus.Empty, result.Status);
            Assert.Empty(result.Accounts);
            Assert.False(result.NeedsMigration);
            Assert.False(result.LegacyPlaintext);
            Assert.Null(result.Error);
        }

        [Fact]
        public void Decode_VaultEncrypted_Loads()
        {
            byte[] key = Create32ByteKey(0xAA);
            var accounts = new List<Account>
            {
                new Account { Username = "VaultUser", UserID = 12345 }
            };
            byte[] serialized = AccountDataSerializer.Serialize(accounts, includePasswords: true);
            byte[] encrypted = Cryptography.Encrypt(serialized, key);

            var keys = CreateDummyKeys(getVaultKey: () => (byte[])key.Clone());

            var result = AccountFileDecoder.Decode(encrypted, null, keys);

            Assert.Equal(AccountDecodeStatus.Loaded, result.Status);
            Assert.Single(result.Accounts);
            Assert.Equal("VaultUser", result.Accounts[0].Username);
            Assert.False(result.LegacyPlaintext);
            Assert.Null(result.Error);
        }

        [Fact]
        public void Decode_WrongVaultNoRecovery_NeedsMasterPassword()
        {
            byte[] realKey = Create32ByteKey(0x11);
            byte[] wrongKey = Create32ByteKey(0x22);
            var accounts = new List<Account>
            {
                new Account { Username = "SecretUser", UserID = 999 }
            };
            byte[] serialized = AccountDataSerializer.Serialize(accounts, includePasswords: true);
            byte[] encrypted = Cryptography.Encrypt(serialized, realKey);

            var keys = CreateDummyKeys(
                getVaultKey: () => (byte[])wrongKey.Clone(),
                tryRecover: null);

            var result = AccountFileDecoder.Decode(encrypted, null, keys);

            Assert.Equal(AccountDecodeStatus.NeedsMasterPassword, result.Status);
            Assert.Empty(result.Accounts);
        }

        [Fact]
        public void Decode_RecoveryKeyUsedWhenVaultFails()
        {
            byte[] realKey = Create32ByteKey(0x55);
            byte[] wrongKey = Create32ByteKey(0x66);
            var accounts = new List<Account>
            {
                new Account { Username = "RecoveredUser", UserID = 888 }
            };
            byte[] serialized = AccountDataSerializer.Serialize(accounts, includePasswords: true);
            byte[] encrypted = Cryptography.Encrypt(serialized, realKey);

            var keys = CreateDummyKeys(
                getVaultKey: () => (byte[])wrongKey.Clone(),
                tryRecover: _ => (byte[])realKey.Clone());

            var result = AccountFileDecoder.Decode(encrypted, null, keys);

            Assert.Equal(AccountDecodeStatus.Loaded, result.Status);
            Assert.Single(result.Accounts);
            Assert.Equal("RecoveredUser", result.Accounts[0].Username);
        }

        [Fact]
        public void Decode_MasterPassword_Loads()
        {
            byte[] masterPassword = Encoding.UTF8.GetBytes("MySecureMasterPassword123!");
            var accounts = new List<Account>
            {
                new Account { Username = "MasterUser", UserID = 777 }
            };
            byte[] serialized = AccountDataSerializer.Serialize(accounts, includePasswords: true);
            byte[] encrypted = Cryptography.Encrypt(serialized, masterPassword);

            // Vault key is intentionally wrong; master password must be used
            var keys = CreateDummyKeys(getVaultKey: () => Create32ByteKey(0x99));

            var result = AccountFileDecoder.Decode(encrypted, masterPassword, keys);

            Assert.Equal(AccountDecodeStatus.Loaded, result.Status);
            Assert.Single(result.Accounts);
            Assert.Equal("MasterUser", result.Accounts[0].Username);
            Assert.False(result.LegacyPlaintext);
        }

        [Fact]
        public void Decode_GarbageWithoutOptIn_IsCorruptWithError()
        {
            byte[] garbage = Encoding.UTF8.GetBytes("NON_ENCRYPTED_GARBAGE_PAYLOAD");
            var keys = CreateDummyKeys(
                unprotectDpapi: _ => throw new CryptographicException("DPAPI decryption failure"),
                plaintextOptIn: false);

            var result = AccountFileDecoder.Decode(garbage, null, keys);

            Assert.Equal(AccountDecodeStatus.Corrupt, result.Status);
            Assert.Empty(result.Accounts);
            Assert.NotNull(result.Error);
        }

        [Fact]
        public void Decode_PlaintextWithOptIn_LegacyPlaintext()
        {
            var accounts = new List<Account>
            {
                new Account { Username = "PlaintextUser", UserID = 555 }
            };
            byte[] plaintextJson = AccountDataSerializer.Serialize(accounts, includePasswords: true);

            var keys = CreateDummyKeys(
                unprotectDpapi: _ => throw new CryptographicException("DPAPI failed"),
                plaintextOptIn: true);

            var result = AccountFileDecoder.Decode(plaintextJson, null, keys);

            Assert.Equal(AccountDecodeStatus.Loaded, result.Status);
            Assert.Single(result.Accounts);
            Assert.Equal("PlaintextUser", result.Accounts[0].Username);
            Assert.True(result.LegacyPlaintext);
            Assert.True(result.NeedsMigration);
        }

        [Fact]
        public void Decode_GetVaultKeyThrows_NeedsMasterPasswordAndDoesNotThrow()
        {
            byte[] key = Create32ByteKey(0x33);
            var accounts = new List<Account> { new Account { Username = "VaultKeyCrashUser", UserID = 101 } };
            byte[] serialized = AccountDataSerializer.Serialize(accounts, includePasswords: true);
            byte[] encrypted = Cryptography.Encrypt(serialized, key);

            var keys = CreateDummyKeys(
                getVaultKey: () => throw new InvalidOperationException("Vault key storage is unreadable!"),
                tryRecover: null);

            var result = AccountFileDecoder.Decode(encrypted, null, keys);

            Assert.Equal(AccountDecodeStatus.NeedsMasterPassword, result.Status);
            Assert.Empty(result.Accounts);
            Assert.NotNull(result.Error);
            Assert.IsType<InvalidOperationException>(result.Error);
        }

        [Fact]
        public void Decode_GetVaultKeyThrows_FallsBackToRecoveryKey()
        {
            byte[] realKey = Create32ByteKey(0x77);
            var accounts = new List<Account> { new Account { Username = "FallbackRecoveryUser", UserID = 202 } };
            byte[] serialized = AccountDataSerializer.Serialize(accounts, includePasswords: true);
            byte[] encrypted = Cryptography.Encrypt(serialized, realKey);

            var keys = CreateDummyKeys(
                getVaultKey: () => throw new InvalidOperationException("Vault key corrupted!"),
                tryRecover: _ => (byte[])realKey.Clone());

            var result = AccountFileDecoder.Decode(encrypted, null, keys);

            Assert.Equal(AccountDecodeStatus.Loaded, result.Status);
            Assert.Single(result.Accounts);
            Assert.Equal("FallbackRecoveryUser", result.Accounts[0].Username);
            Assert.Null(result.Error);
        }
    }
}

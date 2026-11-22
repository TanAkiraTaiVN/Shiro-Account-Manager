using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    public class GoldenCompatibilityTests
    {
        private static readonly byte[] KnownTestVaultKey = Encoding.UTF8.GetBytes("ShiroGoldenTestKey2026!32ByteLen"); // exactly 32 bytes

        private static readonly List<Account> ExpectedGoldenAccounts = new()
        {
            new Account
            {
                Username = "GoldenMasterUser",
                UserID = 1122334455,
                SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.--Sharing-this-will-allow-someone-to-log-in-as-you-and-to-steal-your-ROBLX-and-items.|_GOLDEN_TOKEN_1",
                Password = "GoldenPassword123!",
                Alias = "Main Farmer",
                Group = "VIP_Group",
                RobuxBalance = 9999,
                Description = "Account for backwards compatibility validation",
                Fields = new Dictionary<string, string>
                {
                    { "CustomNotes", "Important Account" },
                    { "Level", "150" }
                }
            },
            new Account
            {
                Username = "GoldenSecondaryUser",
                UserID = 9988776655,
                SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.--Sharing-this-will-allow-someone-to-log-in-as-you-and-to-steal-your-ROBLX-and-items.|_GOLDEN_TOKEN_2",
                Password = "GoldenPassword456!",
                Alias = "Alt Trader",
                Group = "Default",
                RobuxBalance = 250
            }
        };

        [Fact]
        public void ReadShiroVaultV1_CompatibilityTest()
        {
            // Serialize and encrypt with SHIRO_SECURE_VAULT_V1 header
            byte[] serialized = AccountDataSerializer.Serialize(ExpectedGoldenAccounts, includePasswords: true);
            byte[] encrypted = Cryptography.Encrypt(serialized.AsSpan(), KnownTestVaultKey);

            Assert.True(Cryptography.IsEncrypted(encrypted));
            Assert.StartsWith(Encoding.UTF8.GetString(Cryptography.ShiroVaultHeader), Encoding.UTF8.GetString(encrypted), StringComparison.Ordinal);

            byte[] decrypted = Cryptography.Decrypt(encrypted, KnownTestVaultKey);
            var loaded = AccountDataSerializer.Deserialize(decrypted, out bool requiresMigration);

            Assert.False(requiresMigration);
            Assert.Equal(2, loaded.Count);
            Assert.Equal("GoldenMasterUser", loaded[0].Username);
            Assert.Equal(1122334455, loaded[0].UserID);
            Assert.Equal("GoldenPassword123!", loaded[0].Password);
            Assert.Equal(9999, loaded[0].RobuxBalance);
            Assert.Equal("VIP_Group", loaded[0].Group);
            Assert.Equal("Important Account", loaded[0].Fields["CustomNotes"]);

            Assert.Equal("GoldenSecondaryUser", loaded[1].Username);
            Assert.Equal("GoldenPassword456!", loaded[1].Password);
        }

        [Fact]
        public void ReadRAMHeaderV2_CompatibilityTest()
        {
            // Manually construct legacy RAM_SECURE_VAULT_V2 format payload
            byte[] serialized = AccountDataSerializer.Serialize(ExpectedGoldenAccounts, includePasswords: true);

            byte[] salt = new byte[16];
            byte[] nonce = new byte[12];
            RandomNumberGenerator.Fill(salt);
            RandomNumberGenerator.Fill(nonce);

            byte[] derivedKey = Rfc2898DeriveBytes.Pbkdf2(KnownTestVaultKey, salt, 210000, HashAlgorithmName.SHA256, 32);
            byte[] ciphertext = new byte[serialized.Length];
            byte[] tag = new byte[16];

            using (var aesGcm = new AesGcm(derivedKey, 16))
            {
                aesGcm.Encrypt(nonce, serialized, ciphertext, tag);
            }

            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            writer.Write(Cryptography.RAMHeaderV2);
            writer.Write(salt);
            writer.Write(nonce);
            writer.Write(tag);
            writer.Write(ciphertext);
            byte[] v2Payload = ms.ToArray();

            Assert.True(Cryptography.IsEncrypted(v2Payload));
            byte[] decrypted = Cryptography.Decrypt(v2Payload, KnownTestVaultKey);
            var loaded = AccountDataSerializer.Deserialize(decrypted, out bool requiresMigration);

            Assert.False(requiresMigration);
            Assert.Equal(2, loaded.Count);
            Assert.Equal("GoldenMasterUser", loaded[0].Username);
            Assert.Equal("GoldenSecondaryUser", loaded[1].Username);
        }

        [Fact]
        public void ReadLegacyUnversionedJsonArray_CompatibilityTest()
        {
            string legacyJson = @"[
                {
                    ""Username"": ""OldVersionUser"",
                    ""UserID"": 555444,
                    ""SecurityToken"": ""old_token_123"",
                    ""Password"": ""LegacyPass!"",
                    ""Alias"": ""Old Alt"",
                    ""Group"": ""LegacyGroup""
                }
            ]";

            byte[] legacyBytes = Encoding.UTF8.GetBytes(legacyJson);
            var loaded = AccountDataSerializer.Deserialize(legacyBytes, out bool requiresMigration);

            Assert.True(requiresMigration);
            Assert.Single(loaded);
            Assert.Equal("OldVersionUser", loaded[0].Username);
            Assert.Equal(555444, loaded[0].UserID);
            Assert.Equal("LegacyPass!", loaded[0].Password);
        }

        [Fact]
        public void ReadRAMSettingsJson_CompatibilityTest()
        {
            string sampleSettingsJson = @"{
                ""General"": {
                    ""SavePasswords"": true,
                    ""AccountJoinDelay"": 8,
                    ""PresenceUpdateRate"": 60,
                    ""Language"": ""vi"",
                    ""ShuffleJobId"": false
                },
                ""Theme"": {
                    ""AccentColor"": ""#7B68EE"",
                    ""DarkMode"": true
                }
            }";

            using var doc = System.Text.Json.JsonDocument.Parse(sampleSettingsJson);
            Assert.True(doc.RootElement.TryGetProperty("General", out var gen));
            Assert.True(gen.GetProperty("SavePasswords").GetBoolean());
            Assert.Equal(8, gen.GetProperty("AccountJoinDelay").GetInt32());
            Assert.Equal("vi", gen.GetProperty("Language").GetString());
        }
    }
}

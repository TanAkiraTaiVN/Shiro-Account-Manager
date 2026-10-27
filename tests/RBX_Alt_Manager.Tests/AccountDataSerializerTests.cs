using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using RBX_Alt_Manager.Services;
using Xunit;

namespace RBX_Alt_Manager.Tests
{
    public class AccountDataSerializerTests
    {
        [Fact]
        public void Roundtrip_MaintainsAccountIntegrity()
        {
            var original = new List<Account>
            {
                new Account
                {
                    Username = "ShiroUser1",
                    SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS|--test-cookie-token",
                    Alias = "Primary Alt",
                    Password = "SecretPassword123!",
                    Group = "MainGroup",
                    UserID = 123456789,
                    RobuxBalance = 1500,
                    Fields = new Dictionary<string, string> { { "CustomNote", "Active Farmer" } }
                },
                new Account
                {
                    Username = "ShiroUser2",
                    SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS|--test-cookie-token-2",
                    Alias = "Secondary Alt",
                    Password = "AnotherPassword456!",
                    Group = "AltGroup",
                    UserID = 987654321,
                    RobuxBalance = 250
                }
            };

            byte[] serialized = AccountDataSerializer.Serialize(original, includePasswords: true);
            Assert.NotNull(serialized);
            Assert.True(serialized.Length > 0);

            var deserialized = AccountDataSerializer.Deserialize(serialized, out bool requiresMigration);
            Assert.False(requiresMigration);
            Assert.Equal(2, deserialized.Count);

            Assert.Equal("ShiroUser1", deserialized[0].Username);
            Assert.Equal("_|WARNING:-DO-NOT-SHARE-THIS|--test-cookie-token", deserialized[0].SecurityToken);
            Assert.Equal("Primary Alt", deserialized[0].Alias);
            Assert.Equal("SecretPassword123!", deserialized[0].Password);
            Assert.Equal("MainGroup", deserialized[0].Group);
            Assert.Equal(123456789, deserialized[0].UserID);
            Assert.Equal(1500, deserialized[0].RobuxBalance);
            Assert.Equal("Active Farmer", deserialized[0].Fields["CustomNote"]);

            Assert.Equal("ShiroUser2", deserialized[1].Username);
            Assert.Equal("Secondary Alt", deserialized[1].Alias);
            Assert.Equal("AnotherPassword456!", deserialized[1].Password);
        }

        [Fact]
        public void ExcludePasswords_OmitsPasswordProperty()
        {
            var original = new List<Account>
            {
                new Account
                {
                    Username = "PrivacyUser",
                    Password = "SensitivePassword!"
                }
            };

            byte[] serialized = AccountDataSerializer.Serialize(original, includePasswords: false);
            string json = Encoding.UTF8.GetString(serialized);

            Assert.DoesNotContain("SensitivePassword!", json);

            var deserialized = AccountDataSerializer.Deserialize(serialized, out _);
            Assert.Single(deserialized);
            Assert.Equal("PrivacyUser", deserialized[0].Username);
            Assert.True(string.IsNullOrEmpty(deserialized[0].Password));
        }

        [Fact]
        public void Deserialize_LegacyArrayFormat_TriggersMigration()
        {
            string legacyJson = @"[
                {
                    ""Username"": ""LegacyUser"",
                    ""SecurityToken"": ""legacy-token"",
                    ""Alias"": ""Old Format"",
                    ""Group"": ""Default"",
                    ""UserID"": 555
                }
            ]";

            byte[] bytes = Encoding.UTF8.GetBytes(legacyJson);
            var deserialized = AccountDataSerializer.Deserialize(bytes, out bool requiresMigration);

            Assert.True(requiresMigration);
            Assert.Single(deserialized);
            Assert.Equal("LegacyUser", deserialized[0].Username);
            Assert.Equal("legacy-token", deserialized[0].SecurityToken);
            Assert.Equal(555, deserialized[0].UserID);
        }

        [Fact]
        public void Deserialize_EmptyOrNullData_ReturnsEmptyList()
        {
            var emptyResult = AccountDataSerializer.Deserialize(Array.Empty<byte>(), out bool req1);
            Assert.False(req1);
            Assert.Empty(emptyResult);

            var nullResult = AccountDataSerializer.Deserialize(null, out bool req2);
            Assert.False(req2);
            Assert.Empty(nullResult);
        }

        [Fact]
        public void Deserialize_FutureSchemaVersion_ThrowsInvalidDataException()
        {
            string futureJson = @"{
                ""schemaVersion"": 999,
                ""accounts"": []
            }";

            byte[] bytes = Encoding.UTF8.GetBytes(futureJson);
            Assert.Throws<InvalidDataException>(() => AccountDataSerializer.Deserialize(bytes, out _));
        }
    }
}

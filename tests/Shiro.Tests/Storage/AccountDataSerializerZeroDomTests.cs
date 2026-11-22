using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    public class AccountDataSerializerZeroDomTests
    {
        [Fact]
        public void Serialize_ExcludePasswords_UsesZeroDomContractAndPreservesLiveAccountState()
        {
            var acc1 = new Account
            {
                Username = "FarmerAlpha",
                Password = "SuperSecretVaultPassword#1",
                SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS|--alpha-token",
                Alias = "Main",
                UserID = 10001,
                RobuxBalance = 500,
                Fields = new Dictionary<string, string> { ["Server"] = "VIP-1" }
            };
            var acc2 = new Account
            {
                Username = "FarmerBeta",
                Password = "SuperSecretVaultPassword#2",
                SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS|--beta-token",
                Alias = "Sub",
                UserID = 10002,
                RobuxBalance = 900
            };

            byte[] utf8Bytes = AccountDataSerializer.Serialize(new[] { acc1, acc2 }, includePasswords: false);
            string json = Encoding.UTF8.GetString(utf8Bytes);

            // Live instances must NOT be mutated
            Assert.Equal("SuperSecretVaultPassword#1", acc1.Password);
            Assert.Equal("SuperSecretVaultPassword#2", acc2.Password);

            // Serialized output must not contain the Password property or secret values
            Assert.DoesNotContain("\"Password\"", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("SuperSecretVaultPassword#1", json, StringComparison.Ordinal);
            Assert.DoesNotContain("SuperSecretVaultPassword#2", json, StringComparison.Ordinal);

            // Roundtrip deserialization restores all other fields accurately
            var restored = AccountDataSerializer.Deserialize(utf8Bytes, out bool requiresMigration);
            Assert.False(requiresMigration);
            Assert.Equal(2, restored.Count);
            Assert.Equal("FarmerAlpha", restored[0].Username);
            Assert.Equal(string.Empty, restored[0].Password);
            Assert.Equal("VIP-1", restored[0].Fields["Server"]);
            Assert.Equal("FarmerBeta", restored[1].Username);
            Assert.Equal(900, restored[1].RobuxBalance);
        }

        [Fact]
        public void NoPasswordEnvelopeTypeInfo_IsCachedSingleton()
        {
            Assert.NotNull(AccountDataSerializer.NoPasswordEnvelopeTypeInfo);
            Assert.Same(AccountDataSerializer.NoPasswordEnvelopeTypeInfo, AccountDataSerializer.NoPasswordEnvelopeTypeInfo);
        }
    }
}

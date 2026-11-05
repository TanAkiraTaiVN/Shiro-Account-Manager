using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Shiro.Classes;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    public class CriticalStorageAndVaultBugTests
    {
        [Fact]
        public void TryUnprotectOrRecoverKey_WhenPrimaryCorrupt_RestoresFromBakInsteadOfOverwriting()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ShiroVaultKeyTest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                string keyFile = Path.Combine(tempDir, "vault.key");
                string bakFile = keyFile + ".bak";
                byte[] originalKey = RandomNumberGenerator.GetBytes(32);
                byte[] validPayload = StoragePaths.BuildProtectedVaultKeyPayload(originalKey);
                File.WriteAllBytes(bakFile, validPayload);
                File.WriteAllBytes(keyFile, new byte[] { 0x00, 0x01, 0x02 }); // corrupt primary

                byte[] resolved = StoragePaths.LoadOrCreateVaultKeyAtPath(keyFile);

                Assert.Equal(originalKey, resolved);
                Assert.Equal(validPayload, File.ReadAllBytes(bakFile));
                Assert.True(File.Exists(keyFile));
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        [Fact]
        public void AccountDataSerializer_Deserialize_DoesNotTriggerDeferredSaveOrDirtyFlags()
        {
            byte[] key = RandomNumberGenerator.GetBytes(32);
            var source = new List<Account>
            {
                new Account
                {
                    Username = "TestUser",
                    UserID = 12345,
                    Group = "Main",
                    Alias = "MyAlias",
                    Description = "Desc",
                    Password = "Secret",
                    Valid = true
                }
            };
            byte[] serialized = AccountDataSerializer.Serialize(source, includePasswords: true);
            byte[] encrypted = Cryptography.EncryptWithRandomKey(serialized, key);
            var decoded = AccountDataSerializer.Deserialize(
                Cryptography.Decrypt(encrypted, key),
                out _);

            Assert.NotNull(decoded);
            Assert.Single(decoded!);
            Assert.False(decoded[0].IsDirty);
        }

        [Fact]
        public async Task AccountFields_ConcurrentProtectedAndStandardFieldAccess_DoesNotThrowDuringSerialize()
        {
            var acc = new Account { Username = "RaceUser", UserID = 999 };
            byte[] key = RandomNumberGenerator.GetBytes(32);
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromMilliseconds(400));

            Task writer1 = Task.Run(() =>
            {
                int i = 0;
                while (!cts.IsCancellationRequested)
                {
                    acc.SetField("Key_" + (i++ % 16), "Val");
                    acc.AccountStatusIssue = (i % 2 == 0) ? "LOCKED" : string.Empty;
                }
            });

            Task writer2 = Task.Run(() =>
            {
                int i = 0;
                while (!cts.IsCancellationRequested)
                {
                    acc.HbaTemplate = "tpl_" + (i++ % 8);
                    _ = acc.HbaTemplate;
                }
            });

            Task serializer = Task.Run(() =>
            {
                var list = new List<Account> { acc };
                while (!cts.IsCancellationRequested)
                {
                    byte[] bytes = AccountDataSerializer.Serialize(list, includePasswords: true);
                    Assert.NotEmpty(bytes);
                }
            });

            await Task.WhenAll(writer1, writer2, serializer);
            Assert.Equal("RaceUser", acc.Username);
        }
    }
}

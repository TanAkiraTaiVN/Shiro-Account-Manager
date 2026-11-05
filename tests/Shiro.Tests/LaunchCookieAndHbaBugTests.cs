using System;
using System.IO;
using System.Security.Cryptography;
using Shiro.Classes;
using Shiro.Http;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    public class LaunchCookieAndHbaBugTests
    {
        [Fact]
        public void GetPersistedKeyInjectionScript_WhenPublicKeyEmpty_DerivesSpkiPublicKeyFromPrivateKey()
        {
            using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            string privB64 = Convert.ToBase64String(ecdsa.ExportPkcs8PrivateKey());
            string expectedPubB64 = Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo());

            string script = RobloxHbaService.GetPersistedKeyInjectionScript(privB64, publicKeySpkiBase64: "", tokenTemplate: null);

            Assert.Contains(expectedPubB64, script);
        }

        [Fact]
        public void CookieGuard_WhenNoInitialCookiesDat_SecondInjectionDoesNotBackUpFirstAccountsCookie()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ShiroCookieGuardTest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                bool first = RobloxDirectLauncher.InjectAccountCookieToLocalStorage(
                    "_|WARNING:-DO-NOT-SHARE-THIS.--tokenA",
                    browserTrackerId: "11111111111",
                    expiryUtc: null,
                    localStorageDirectoryOverride: tempDir,
                    username: "UserA");
                Assert.True(first);

                bool second = RobloxDirectLauncher.InjectAccountCookieToLocalStorage(
                    "_|WARNING:-DO-NOT-SHARE-THIS.--tokenB",
                    browserTrackerId: "22222222222",
                    expiryUtc: null,
                    localStorageDirectoryOverride: tempDir,
                    username: "UserB");
                Assert.True(second);

                string backupPath = Path.Combine(tempDir, "RobloxCookies.dat.shiro-orig");
                Assert.False(File.Exists(backupPath));

                bool restored = RobloxDirectLauncher.RestoreOriginalCookies(tempDir);
                Assert.True(restored);
                Assert.False(File.Exists(Path.Combine(tempDir, "RobloxCookies.dat")));
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        [Fact]
        public void AutoReconnect_RegisterSession_SuppressesReplacedSession()
        {
            var svc = AutoReconnectService.Instance;
            var acc = new Account { Username = "ReconnectUser", UserID = 42 };
            svc.RegisterSession(acc, 100, "job-1", processId: 1111);
            Assert.True(svc.TryGetSession("ReconnectUser", out var firstSession));

            svc.RegisterSession(acc, 100, "job-2", processId: 2222);
            Assert.True(firstSession!.IsSuppressed);
            svc.UnregisterSession("ReconnectUser");
        }
    }
}

#nullable enable
using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public sealed class Phase1SecretHygieneTests : IDisposable
    {
        public Phase1SecretHygieneTests()
        {
            RobloxHbaService.ClearKeyCache();
            ProxyBindingService.ClearClients();
        }

        public void Dispose()
        {
            RobloxHbaService.ClearKeyCache();
            ProxyBindingService.ClearClients();
        }

        [Fact]
        public void RobloxHbaService_KeyCache_DoesNotStorePlaintextPrivateKeyAsDictionaryKey()
        {
            using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            string pkcs8 = Convert.ToBase64String(ecdsa.ExportPkcs8PrivateKey());

            bool signed = RobloxHbaService.TryBuildBoundAuthToken(
                pkcs8,
                "https://apis.roblox.com/v1/test",
                "POST",
                "{}",
                null,
                1700000000,
                out string token);

            Assert.True(signed);
            Assert.NotEmpty(token);

            var field = typeof(RobloxHbaService).GetField(
                "CachedEcParameters",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(field);
            var cache = Assert.IsAssignableFrom<IDictionary>(field!.GetValue(null));

            Assert.Single(cache);
            string storedKey = cache.Keys.Cast<object>().Single().ToString()!;
            Assert.Equal(64, storedKey.Length);
            Assert.DoesNotContain(pkcs8, storedKey, StringComparison.Ordinal);

            Assert.True(RobloxHbaService.ClearCachedKey(pkcs8));
            Assert.Empty(cache);
        }

        [Fact]
        public void ProxyBindingService_ClientCache_DoesNotStorePlaintextProxyCredentialsAsKey()
        {
            const string rawProxy = "http://secretuser:secretpass@127.0.0.1:9999";
            ISettingsService prevSettings = ProxyBindingService.Settings;
            var isoSection = new IniFile()["General"];
            isoSection.Set(AppSettingsKeys.General.UseProxies, "true");
            ProxyBindingService.Settings = isoSection;

            try
            {
                var client = ProxyBindingService.GetClient(rawProxy);
                var noRedirClient = ProxyBindingService.GetNoRedirectClient(new Account { Proxy = rawProxy });
                Assert.NotNull(client);
                Assert.NotNull(noRedirClient);

                var field = typeof(ProxyBindingService).GetField(
                    "_proxyClients",
                    BindingFlags.NonPublic | BindingFlags.Static);
                Assert.NotNull(field);
                var cache = Assert.IsAssignableFrom<IDictionary>(field!.GetValue(null));

                Assert.Equal(2, cache.Count);
                foreach (object keyObj in cache.Keys)
                {
                    string key = keyObj.ToString()!;
                    Assert.DoesNotContain("secretuser", key, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain("secretpass", key, StringComparison.OrdinalIgnoreCase);
                }

                ProxyBindingService.PruneClients(new[] { rawProxy });
                Assert.Equal(2, cache.Count);

                ProxyBindingService.PruneClients(Array.Empty<string>());
                Assert.Empty(cache);
            }
            finally
            {
                ProxyBindingService.Settings = prevSettings;
            }
        }

        [Fact]
        public void RobloxDirectLauncher_CookieGuard_DoesNotHoldPlaintextStringFieldForLastInjectedToken()
        {
            var fields = typeof(RobloxDirectLauncher).GetFields(BindingFlags.NonPublic | BindingFlags.Static);
            Assert.DoesNotContain(fields, f => f.Name == "_lastInjectedToken");

            FieldInfo? protectedField = Array.Find(fields, f => f.Name == "_protectedLastInjectedToken");
            Assert.NotNull(protectedField);
            Assert.Equal(typeof(byte[]), protectedField!.FieldType);
        }

        [Fact]
        public void HbaBrowserScripts_BuildCollectionScript_DoesNotAssignPrivateKeyOntoWindowGlobal()
        {
            string script = HbaBrowserScripts.BuildCollectionScript();
            Assert.Contains("window.__shiroHbaState", script, StringComparison.Ordinal);
            Assert.DoesNotContain("state.privateKeyPkcs8", script, StringComparison.Ordinal);
            Assert.DoesNotContain("__shiroHbaState.privateKeyPkcs8", script, StringComparison.Ordinal);

            int stateInitStart = script.IndexOf("window.__shiroHbaState =", StringComparison.Ordinal);
            Assert.True(stateInitStart >= 0);
            int stateInitEnd = script.IndexOf("};", stateInitStart, StringComparison.Ordinal);
            Assert.True(stateInitEnd > stateInitStart);
            string stateLiteral = script.Substring(stateInitStart, stateInitEnd - stateInitStart);
            Assert.DoesNotContain("privateKeyPkcs8", stateLiteral, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("roblox-player:1+launchmode:play+gameinfo:eyJhbGci_secretTicket123+launchtime:12345", "secretTicket123")]
        [InlineData("GET /Accounts HTTP/1.1 X-Shiro-Auth: MySuperSecret99", "MySuperSecret99")]
        [InlineData("GET /Accounts HTTP/1.1 X-RAM-Auth: LegacyPass42", "LegacyPass42")]
        [InlineData("CONNECT api.roblox.com:443 Proxy-Authorization: Basic dXNlcjpwYXNz", "dXNlcjpwYXNz")]
        [InlineData("https://localhost:7963/SetProxy?Account=u&Proxy=http%3A%2F%2Fbobsecret%3Apw123secret%401.2.3.4%3A8080", "pw123secret")]
        [InlineData("https://localhost:7963/SetProxy?Account=u&Proxy=http://bobsecret:pw123secret@1.2.3.4:8080", "pw123secret")]
        [InlineData("Using proxy 1.2.3.4:8080:bobsecret:pw123secret for launch", "pw123secret")]
        [InlineData("{\"HbaPrivateKeyPkcs8\":\"MIGHAgEAMBMGByqGSM49secret\"}", "MIGHAgEAMBMGByqGSM49secret")]
        public void LogRedactor_RedactsAllFiveSecretLeakPatterns(string rawLine, string secret)
        {
            string redacted = LogRedactor.Redact(rawLine);
            Assert.DoesNotContain(secret, redacted, StringComparison.Ordinal);
            Assert.NotEqual(rawLine, redacted);
        }

        [Fact]
        public void LogRedactor_DoesNotFalsePositiveOnTimestampsOrSafeLogMessages()
        {
            const string safeLine = "Connected cleanly at 12:34:56 via relay node alpha-1";
            Assert.Equal(safeLine, LogRedactor.Redact(safeLine));

            string gameInfo = LogRedactor.Redact("launch gameinfo:SecretTicketXYZ+launchtime:999");
            Assert.Contains("gameinfo:[REDACTED]", gameInfo, StringComparison.Ordinal);
            Assert.Contains("+launchtime:999", gameInfo, StringComparison.Ordinal);
        }
    }
}

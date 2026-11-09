#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Shiro;
using Shiro.Services;
using Shiro.Tests.TestInfrastructure;
using Xunit;

namespace Shiro.Tests
{
    public partial class RobloxHbaAndIpPersistenceTests
    {
        [Fact]
        public void AccountLaunchService_CreateTicketRequest_AttachesHbaHeader()
        {
            var account = new Account("LaunchTicketUser", "pass", "alias");
            using var ecdsa = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
            account.HbaPrivateKeyPkcs8 = Convert.ToBase64String(ecdsa.ExportPkcs8PrivateKey());
            account.HbaPublicKeySpki = Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo());

            using var request = AccountLaunchService.CreateTicketRequest(account, "test-csrf-token");

            Assert.NotNull(request);
            Assert.True(request.Headers.Contains(RobloxHbaService.BoundAuthHeaderName));
            Assert.True(request.Headers.Contains("X-CSRF-TOKEN"));
            Assert.Equal("test-csrf-token", request.Headers.GetValues("X-CSRF-TOKEN").First());
            var headerValues = request.Headers.GetValues(RobloxHbaService.BoundAuthHeaderName);
            Assert.Single(headerValues);
            string headerValue = headerValues.First();
            Assert.StartsWith("v1|", headerValue);
        }

        [Fact]
        public async Task TryBuildBoundAuthToken_ConcurrentWithClearKeyCache_NeverFails()
        {
            var pkcs8CacheField = typeof(RobloxHbaService).GetField(
                "CachedPkcs8Keys",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.Null(pkcs8CacheField);

            var (privB64, _) = GenerateP256KeyPair();
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(600));
            var signTasks = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
            {
                while (!cts.IsCancellationRequested)
                {
                    bool ok = RobloxHbaService.TryBuildBoundAuthToken(
                        privB64,
                        "https://auth.roblox.com/v1/authentication-ticket",
                        "POST",
                        "{}",
                        RobloxHbaService.DefaultTemplate,
                        1700000000L,
                        out string token);
                    Assert.True(ok);
                    Assert.False(string.IsNullOrWhiteSpace(token));
                }
            }));
            var clearTask = Task.Run(() =>
            {
                while (!cts.IsCancellationRequested)
                {
                    RobloxHbaService.ClearKeyCache();
                }
            });
            await Task.WhenAll(signTasks.Append(clearTask));
        }

        [Fact]
        public void ApplySessionFingerprintAndHbaToRestRequest_FormParametersWithSpaces_MatchesFormUrlEncodedHash()
        {
            var (privB64, pubB64) = GenerateP256KeyPair();
            var acc = new Account("RestFormUser", "pass", "alias")
            {
                UserID = 123456,
                HbaPrivateKeyPkcs8 = privB64,
                HbaPublicKeySpki = pubB64,
                BrowserTrackerID = "555666"
            };

            var req = new Shiro.Http.RestRequest("/v1/test", Shiro.Http.Method.Post);
            req.Headers["Cookie"] = ".ROBLOSECURITY=test";
            req.AddParameter("note", "hello world");

            acc.ApplySessionFingerprintAndHbaToRestRequest(req, "https://www.roblox.com/");

            Assert.True(req.Headers.TryGetValue(RobloxHbaService.BoundAuthHeaderName, out string? batHeader));
            Assert.NotNull(batHeader);
            string[] segments = batHeader.Split('|');
            Assert.True(segments.Length >= 4);
            string expectedHash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes("note=hello+world")));
            Assert.Equal(expectedHash, segments[1]);

            Assert.True(req.Headers.TryGetValue("Cookie", out string? cookieHeader));
            Assert.NotNull(cookieHeader);
            Assert.DoesNotContain("01/01/2026 00:00:00", cookieHeader);
            Assert.Contains("RBXEventTrackerV2=CreateDate=", cookieHeader);
        }

        [Fact]
        public void GetBrowserCaptureScript_RequiresAllSignaturesBeforeUpdatingTemplate()
        {
            string script = RobloxHbaService.GetBrowserCaptureScript();
            Assert.Contains("matched.length === sigCount", script);
            Assert.Contains("data-hba-indexed-db-name", script);
            Assert.Contains("data-hba-indexed-db-obj-store-name", script);
            Assert.Contains("data-hba-indexed-db-key-name", script);
            Assert.Contains("saiBound", script);
            Assert.DoesNotContain("inspectBoundHeader(String(value), '', '')", script);
        }

        [Theory]
        [InlineData("v1|b64|HB,TS,URL,M|,TS,PATH,M")]
        [InlineData("v1|b64|HB,TS,,M|,TS,,M")]
        [InlineData("v1|b64|,TS,,|,TS,,")]
        public void TryBuildBoundAuthToken_HealsCorruptedAndRegressedTemplates_ToFullUrlDualSignatures(string brokenTemplate)
        {
            var (privB64, pubB64) = GenerateP256KeyPair();
            string url = "https://auth.roblox.com/v1/authentication-ticket";
            string body = "{}";
            long ts = 1762483200L;

            bool ok = RobloxHbaService.TryBuildBoundAuthToken(
                privB64,
                url,
                "POST",
                body,
                brokenTemplate,
                ts,
                out string token);

            Assert.True(ok);
            string[] parts = token.Split('|');
            Assert.Equal(5, parts.Length);

            using ECDsa verifier = ECDsa.Create();
            verifier.ImportSubjectPublicKeyInfo(Convert.FromBase64String(pubB64), out _);

            string expectedHash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(body)));
            string expectedPayload1 = $"{expectedHash}|1762483200|{url}|POST";
            string expectedPayload2 = $"|1762483200|{url}|POST";

            Assert.True(verifier.VerifyData(
                Encoding.UTF8.GetBytes(expectedPayload1),
                Convert.FromBase64String(parts[3]),
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
            Assert.True(verifier.VerifyData(
                Encoding.UTF8.GetBytes(expectedPayload2),
                Convert.FromBase64String(parts[4]),
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        }

        [Fact]
        public void MergeCapturePayloadIntoSnapshot_PreservesSaiBoundKey_OverSubsequentPostLoginKeys()
        {
            var (saiPriv, saiPub) = GenerateP256KeyPair();
            var (homePriv, homePub) = GenerateP256KeyPair();
            var snapshot = new RobloxHbaService.CaptureSnapshot();

            using var loginDoc = System.Text.Json.JsonDocument.Parse(
                $"{{\"privateKeyPkcs8\":\"{saiPriv}\",\"publicKeySpki\":\"{saiPub}\",\"authoritativeKey\":true,\"saiBound\":true,\"userAgent\":\"Mozilla/5.0 Login\"}}");
            RobloxHbaService.MergeCapturePayloadIntoSnapshot(snapshot, loginDoc.RootElement);

            using var homeSpeculativeDoc = System.Text.Json.JsonDocument.Parse(
                $"{{\"privateKeyPkcs8\":\"{homePriv}\",\"publicKeySpki\":\"{homePub}\",\"authoritativeKey\":true,\"saiBound\":false,\"tokenTemplate\":\"v1|b64|HB,TS,URL,M|,TS,URL,M\"}}");
            RobloxHbaService.MergeCapturePayloadIntoSnapshot(snapshot, homeSpeculativeDoc.RootElement);

            Assert.Equal(saiPriv, snapshot.PrivateKeyPkcs8Base64);
            Assert.Equal(saiPub, snapshot.PublicKeySpkiBase64);
            Assert.True(snapshot.IsSaiBound);
            Assert.Equal("v1|b64|HB,TS,URL,M|,TS,URL,M", snapshot.TokenTemplate);
        }

        [Fact]
        public async Task RefreshStaleCookiesAsync_WhenVpnTunnelActive_SkipsCrossNetworkBoundAccounts()
        {
            var mgr = AccountManager.Instance ?? new AccountManager();
            int httpCalls = 0;
            Account.TestHttpClientFactory = _ => new HttpClient(new FakeHttpHandler((_, _) =>
            {
                Interlocked.Increment(ref httpCalls);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            }));
            RobloxHbaService.TestIsVpnTunnelActiveOverride = () => true;

            var vnBoundAcc = new Account
            {
                Username = "VnBoundAccount",
                UserID = 88001,
                SecurityToken = SampleCookie,
                Valid = true,
                LastAttemptedRefresh = DateTime.MinValue,
                LastBoundIpSignature = "vietnam_residential_isp_sig"
            };

            try
            {
                AccountManager.ReplaceAccountsList(new System.Collections.Generic.List<Account> { vnBoundAcc });
                await mgr.RefreshStaleCookiesAsync(CancellationToken.None);

                Assert.Equal(0, httpCalls);
                Assert.True(vnBoundAcc.Valid);
                Assert.Equal(DateTime.MinValue, vnBoundAcc.LastAttemptedRefresh);
            }
            finally
            {
                AccountManager.ReplaceAccountsList(new System.Collections.Generic.List<Account>());
            }
        }

        [Theory]
        [InlineData("https://www.roblox.com/login", true)]
        [InlineData("https://roblox.com/", true)]
        [InlineData("https://auth.roblox.com/v2/login", true)]
        [InlineData("HTTPS://WWW.ROBLOX.COM/login", true)]
        [InlineData("http://www.roblox.com/login", false)]
        [InlineData("https://roblox.com.evil.example/login", false)]
        [InlineData("https://notroblox.com/login", false)]
        [InlineData("https://.roblox.com/login", false)]
        [InlineData("https://client-api.arkoselabs.com/fc/gc/", false)]
        [InlineData("about:blank", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void IsTrustedRobloxOrigin_ValidatesHttpsAndExactDomainBoundary(string? url, bool expected)
        {
            Assert.Equal(expected, WebAuthPolicy.IsTrustedRobloxOrigin(url));
        }

        [Fact]
        public void GetPersistedKeyInjectionScript_EnforcesOriginGuardAndNonExtractableImport()
        {
            var (privB64, pubB64) = GenerateP256KeyPair();
            string script = RobloxHbaService.GetPersistedKeyInjectionScript(privB64, pubB64, RobloxHbaService.DefaultTemplate);

            Assert.Contains("location.protocol !== 'https:'", script);
            Assert.Contains(".endsWith('.roblox.com')", script);
            Assert.DoesNotContain("privateKeyPkcs8 =", script);
            Assert.Contains("importKey('pkcs8', fromB64(privB64), alg, false, ['sign'])", script);

            string captureScript = RobloxHbaService.GetBrowserCaptureScript();
            Assert.Contains("location.protocol !== 'https:'", captureScript);
            Assert.Contains(".endsWith('.roblox.com')", captureScript);

            string automationPath = System.IO.Path.Combine(
                ArchitectureTests.RepoRoot,
                "src",
                "Shiro",
                "AvaloniaUI",
                "Views",
                "FastLoginDialog.Automation.cs");
            string automationSource = System.IO.File.ReadAllText(automationPath);
            int methodIdx = automationSource.IndexOf("void OnWebMessageReceived", StringComparison.Ordinal);
            Assert.InRange(methodIdx, 0, automationSource.Length - 1);
            int guardIdx = automationSource.IndexOf("WebAuthPolicy.IsTrustedRobloxOrigin(e.Source)", methodIdx, StringComparison.Ordinal);
            int readStringIdx = automationSource.IndexOf("TryGetWebMessageAsString", methodIdx, StringComparison.Ordinal);
            int readJsonIdx = automationSource.IndexOf("WebMessageAsJson", methodIdx, StringComparison.Ordinal);
            Assert.InRange(guardIdx, methodIdx, readStringIdx - 1);
            Assert.InRange(guardIdx, methodIdx, readJsonIdx - 1);
        }

        private static string FindRepoRoot() => ArchitectureTests.RepoRoot;

        [Fact]
        public void GetNetworkSignatureForAccount_IncludesProxyHostPort_StripsCredentials_AndPreservesDirectSignature()
        {
            string directBase = RobloxHbaService.GetLocalNetworkSignature();
            var directAcc = new Account { Username = "DirectUser", Proxy = "" };
            Assert.Equal(directBase, RobloxHbaService.GetNetworkSignatureForAccount(directAcc));
            Assert.Equal(directBase, RobloxHbaService.GetNetworkSignatureForAccount(null));

            if (directBase is not ("offline" or "unknown"))
            {
                var proxyUriAcc1 = new Account { Username = "P1", Proxy = "http://alice:secret1@203.0.113.10:8080" };
                var proxyUriAcc2 = new Account { Username = "P2", Proxy = "203.0.113.10:8080:bob:secret2" };
                var proxyOtherHost = new Account { Username = "P3", Proxy = "http://203.0.113.99:8080" };

                string sig1 = RobloxHbaService.GetNetworkSignatureForAccount(proxyUriAcc1);
                string sig2 = RobloxHbaService.GetNetworkSignatureForAccount(proxyUriAcc2);
                string sig3 = RobloxHbaService.GetNetworkSignatureForAccount(proxyOtherHost);

                Assert.Equal(24, sig1.Length);
                Assert.NotEqual(directBase, sig1);
                Assert.Equal(sig1, sig2); // Same host:port with different credentials produces identical endpoint signature
                Assert.NotEqual(sig1, sig3);
            }

            Assert.Equal("http://203.0.113.10:8080", NetworkSignatureHelper.NormalizeAccountProxyEndpoint("http://u:p@203.0.113.10:8080"));
            Assert.Equal("http://203.0.113.10:8080", NetworkSignatureHelper.NormalizeAccountProxyEndpoint("203.0.113.10:8080:u:p"));
            Assert.Equal("socks5://203.0.113.10:1080", NetworkSignatureHelper.NormalizeAccountProxyEndpoint("socks5://u:p@203.0.113.10:1080"));
            Assert.Null(NetworkSignatureHelper.NormalizeAccountProxyEndpoint("   "));
        }

        [Fact]
        public void FastLoginAutomation_And_AccountFieldsDialog_EnforceHttpsResourceFilterAndProtectedFieldGuard()
        {
            string repoRoot = FindRepoRoot();
            string fastLogin = File.ReadAllText(Path.Combine(repoRoot, "src", "Shiro", "AvaloniaUI", "Views", "FastLoginDialog.Automation.cs"));
            Assert.Contains("AddWebResourceRequestedFilter(\"https://*.roblox.com/*\"", fastLogin, StringComparison.Ordinal);
            Assert.Contains("!WebAuthPolicy.IsTrustedRobloxOrigin(uri)", fastLogin, StringComparison.Ordinal);

            string fieldsDialog = File.ReadAllText(Path.Combine(repoRoot, "src", "Shiro", "AvaloniaUI", "Views", "AccountFieldsDialog.axaml.cs"));
            Assert.Contains("GetPublicFieldsSnapshot()", fieldsDialog, StringComparison.Ordinal);
            Assert.Contains("Account.IsProtectedInternalField", fieldsDialog, StringComparison.Ordinal);
        }

        [Fact]
        public async Task RefreshStaleCookiesAsync_WhenVpnTunnelActive_ProtectsMismatchedProxyAndAllowsMatchingProxySignature()
        {
            var mgr = AccountManager.Instance ?? new AccountManager();
            int httpCalls = 0;
            Account.TestHttpClientFactory = _ => new HttpClient(new FakeHttpHandler((_, _) =>
            {
                Interlocked.Increment(ref httpCalls);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"id\":88002,\"name\":\"ProxyMatchedAcc\"}")
                });
            }));
            RobloxHbaService.TestIsVpnTunnelActiveOverride = () => true;

            var proxyMismatchAcc = new Account
            {
                Username = "ProxyMismatchAcc",
                UserID = 88003,
                SecurityToken = SampleCookie,
                Proxy = "http://203.0.113.10:8080",
                Valid = true,
                LastAttemptedRefresh = DateTime.MinValue,
                LastBoundIpSignature = "stale_previous_proxy_sig"
            };

            string matchedSig = RobloxHbaService.GetNetworkSignatureForAccount(
                new Account { Username = "Probe", Proxy = "203.0.113.20:8080:bob:pass" });
            var proxyMatchAcc = new Account
            {
                Username = "ProxyMatchedAcc",
                UserID = 88002,
                SecurityToken = SampleCookie,
                Proxy = "http://alice:other@203.0.113.20:8080",
                Valid = true,
                LastAttemptedRefresh = DateTime.MinValue,
                LastBoundIpSignature = matchedSig
            };

            try
            {
                AccountManager.ReplaceAccountsList(new System.Collections.Generic.List<Account> { proxyMismatchAcc, proxyMatchAcc });
                await mgr.RefreshStaleCookiesAsync(CancellationToken.None);

                string localSig = RobloxHbaService.GetLocalNetworkSignature();
                if (localSig is not ("offline" or "unknown"))
                {
                    Assert.Equal(DateTime.MinValue, proxyMismatchAcc.LastAttemptedRefresh);
                    Assert.NotEqual(DateTime.MinValue, proxyMatchAcc.LastAttemptedRefresh);
                }
                else
                {
                    Assert.NotEqual(DateTime.MinValue, proxyMatchAcc.LastAttemptedRefresh);
                }
            }
            finally
            {
                AccountManager.ReplaceAccountsList(new System.Collections.Generic.List<Account>());
            }
        }
    }
}

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
    [Collection("SerialGlobalState")]
    public partial class RobloxHbaAndIpPersistenceTests : IDisposable
    {
        private const string SampleCookie =
            "_|WARNING:-DO-NOT-SHARE-THIS.--Sharing-this-will-allow-someone-to-log-in-as-you-and-to-steal-your-ROBUX-and-items.|_" +
            "H." + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" + ".S";

        public RobloxHbaAndIpPersistenceTests() => RobloxRateLimitCoordinator.Instance.ResetBackoff();

        public void Dispose()
        {
            RobloxAccountSessionClient.TestHttpClientFactory = null;
            RobloxHbaService.TestIsVpnTunnelActiveOverride = null;
            RobloxRateLimitCoordinator.Instance.ResetBackoff();
        }

        private static (string Pkcs8Base64, string SpkiBase64) GenerateP256KeyPair()
        {
            using ECDsa ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            string pkcs8 = Convert.ToBase64String(ecdsa.ExportPkcs8PrivateKey());
            string spki = Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo());
            return (pkcs8, spki);
        }

        [Fact]
        public void TryBuildBoundAuthToken_ProducesValidIeeeP1363DualEcdsaSignatures()
        {
            var (privB64, pubB64) = GenerateP256KeyPair();
            string url = "https://auth.roblox.com/v1/authentication-ticket/";
            string body = "{}";
            long ts = 1760000000L;

            bool ok = RobloxHbaService.TryBuildBoundAuthToken(
                privB64,
                url,
                "POST",
                body,
                RobloxHbaService.DefaultTemplate,
                ts,
                out string token);

            Assert.True(ok);
            string[] parts = token.Split('|');
            Assert.Equal(5, parts.Length);
            Assert.Equal("v1", parts[0]);

            string expectedHash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(body)));
            Assert.Equal(expectedHash, parts[1]);
            Assert.Equal("1760000000", parts[2]);

            byte[] sig1 = Convert.FromBase64String(parts[3]);
            byte[] sig2 = Convert.FromBase64String(parts[4]);
            Assert.Equal(64, sig1.Length); // P-256 IEEE P1363 r||s = 64 bytes
            Assert.Equal(64, sig2.Length);

            using ECDsa verifier = ECDsa.Create();
            verifier.ImportSubjectPublicKeyInfo(Convert.FromBase64String(pubB64), out _);

            string expectedPath = new Uri(url).AbsolutePath;
            string expectedPayload1 = $"{expectedHash}|1760000000|{url}|POST";
            string expectedPayload2 = $"|1760000000|{expectedPath}|POST";

            Assert.True(verifier.VerifyData(Encoding.UTF8.GetBytes(expectedPayload1), sig1, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
            Assert.True(verifier.VerifyData(Encoding.UTF8.GetBytes(expectedPayload2), sig2, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        }

        [Fact]
        public void TryBuildBoundAuthToken_SupportsSelfCalibratedCustomTemplate()
        {
            var (privB64, pubB64) = GenerateP256KeyPair();
            string url = "https://users.roblox.com/v1/users/authenticated?foo=1";
            long ts = 1760000123L;

            bool ok = RobloxHbaService.TryBuildBoundAuthToken(
                privB64,
                url,
                "get",
                bodyContent: null,
                tokenTemplate: "v2|hex|M,PATH,HB,TS",
                unixTimestampOverride: ts,
                out string token);

            Assert.True(ok);
            string[] parts = token.Split('|');
            Assert.Equal(4, parts.Length);
            Assert.Equal("v2", parts[0]);

            string emptyHex = Convert.ToHexStringLower(SHA256.HashData(Array.Empty<byte>()));
            Assert.Equal(emptyHex, parts[1]);
            Assert.Equal("1760000123", parts[2]);

            using ECDsa verifier = ECDsa.Create();
            verifier.ImportSubjectPublicKeyInfo(Convert.FromBase64String(pubB64), out _);

            string expectedSigned = $"GET|/v1/users/authenticated|{emptyHex}|1760000123";
            Assert.True(verifier.VerifyData(
                Encoding.UTF8.GetBytes(expectedSigned),
                Convert.FromBase64String(parts[3]),
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        }

        [Fact]
        public async Task Account_WithHbaCapture_AttachesBoundTokenUserAgentAndTrackerCookie()
        {
            var (privB64, pubB64) = GenerateP256KeyPair();
            string customUa = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/136.0.0.0 Safari/537.36 Edg/136.0.0.0";
            string? trackerId = RobloxHbaService.ExtractBrowserTrackerIdFromCookie("CreateDate=10/30/2026 10:00:00&rbxid=77&browserid=168899001122;");
            Assert.Equal("168899001122", trackerId);

            string? capturedBat = null;
            string? capturedUa = null;
            string? capturedCookie = null;

            RobloxAccountSessionClient.TestHttpClientFactory = _ => new HttpClient(new FakeHttpHandler((req, _) =>
            {
                if (req.RequestUri!.AbsoluteUri.Contains("authentication-ticket"))
                {
                    capturedBat = req.Headers.TryGetValues(RobloxHbaService.BoundAuthHeaderName, out var b) ? b.FirstOrDefault() : null;
                    capturedUa = req.Headers.UserAgent.ToString();
                    capturedCookie = req.Headers.TryGetValues("Cookie", out var c) ? string.Join("; ", c) : null;

                    var resp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
                    resp.Headers.TryAddWithoutValidation("rbx-authentication-ticket", "TICKET_VN_OK");
                    return Task.FromResult(resp);
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
            }));

            var acc = new Account { Username = "HbaUser", UserID = 77, SecurityToken = SampleCookie, Valid = true };
            acc.ApplyHbaCapture(new RobloxHbaService.CaptureSnapshot
            {
                PrivateKeyPkcs8Base64 = privB64,
                PublicKeySpkiBase64 = pubB64,
                UserAgent = customUa,
                BrowserTrackerId = trackerId
            });

            var (success, ticket) = await acc.GetAuthTicketAsync();
            Assert.True(success);
            Assert.Equal("TICKET_VN_OK", ticket);
            Assert.False(string.IsNullOrWhiteSpace(capturedBat));
            Assert.Equal(customUa, capturedUa);
            Assert.Contains("browserid=168899001122", capturedCookie);

            string[] batParts = capturedBat!.Split('|');
            Assert.Equal(5, batParts.Length);
            using ECDsa verifier = ECDsa.Create();
            verifier.ImportSubjectPublicKeyInfo(Convert.FromBase64String(pubB64), out _);
            string expectedPayload1 = $"{batParts[1]}|{batParts[2]}|https://auth.roblox.com/v1/authentication-ticket|POST";
            Assert.True(verifier.VerifyData(
                Encoding.UTF8.GetBytes(expectedPayload1),
                Convert.FromBase64String(batParts[3]),
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        }

        [Fact]
        public async Task TryRebindSessionToCurrentNetworkAsync_WhenNetworkChanged_RedeemsTicketWithHbaAndRotatesCookie()
        {
            var (privB64, pubB64) = GenerateP256KeyPair();
            string reboundCookie =
                "_|WARNING:-DO-NOT-SHARE-THIS.--Sharing-this-will-allow-someone-to-log-in-as-you-and-to-steal-your-ROBUX-and-items.|_" +
                "H." + new string('V', 939) + ".S";

            bool redeemCalledWithHba = false;
            RobloxAccountSessionClient.TestHttpClientFactory = _ => new HttpClient(new FakeHttpHandler(async (req, ct) =>
            {
                string uri = req.RequestUri!.AbsoluteUri;
                if (uri.Contains("/v1/authentication-ticket", StringComparison.OrdinalIgnoreCase) &&
                    !uri.Contains("/redeem", StringComparison.OrdinalIgnoreCase))
                {
                    var ticketResp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
                    ticketResp.Headers.TryAddWithoutValidation("x-csrf-token", "csrf-vn");
                    ticketResp.Headers.TryAddWithoutValidation("rbx-authentication-ticket", "REBIND_TICKET_123");
                    return ticketResp;
                }

                if (uri.EndsWith("/v1/authentication-ticket/redeem", StringComparison.OrdinalIgnoreCase))
                {
                    string body = await req.Content!.ReadAsStringAsync(ct);
                    Assert.Contains("REBIND_TICKET_123", body);
                    redeemCalledWithHba = req.Headers.Contains(RobloxHbaService.BoundAuthHeaderName) &&
                                          req.Headers.Contains("rbxauthenticationnegotiation");

                    var redeemResp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
                    redeemResp.Headers.TryAddWithoutValidation("Set-Cookie", $".ROBLOSECURITY={reboundCookie}; Path=/; Domain=.roblox.com; HttpOnly");
                    return redeemResp;
                }

                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
            }));

            var acc = new Account { Username = "VpnToVnAcc", UserID = 101, SecurityToken = SampleCookie, Valid = true };
            acc.ApplyHbaCapture(new RobloxHbaService.CaptureSnapshot
            {
                PrivateKeyPkcs8Base64 = privB64,
                PublicKeySpkiBase64 = pubB64
            });
            // Simulate login having occurred on a foreign VPN network interface
            acc.LastBoundIpSignature = "foreign_vpn_adapter_sig";

            bool rebound = await acc.TryRebindSessionToCurrentNetworkAsync(force: true);
            Assert.True(rebound);
            Assert.True(redeemCalledWithHba);
            Assert.Equal(reboundCookie, acc.SecurityToken);
        }

        [Fact]
        public async Task ValidateSessionAsync_WhenIspRedirectsToNonRobloxOrReturnsHtmlBlock_PreservesValidSession()
        {
            // Case 1: ISP returns 200 OK HTML block page on users.roblox.com
            RobloxAccountSessionClient.TestHttpClientFactory = _ => new HttpClient(new FakeHttpHandler((_, _) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("<html><body>ISP Access Restricted</body></html>")
                })));

            var acc = new Account { Username = "VnPlayer", UserID = 55, SecurityToken = SampleCookie, Valid = true };
            bool resultHtml = await acc.ValidateSessionAsync();
            Assert.True(resultHtml);
            Assert.True(acc.Valid);

            // Case 2: ISP redirects users.roblox.com to external VNG/ISP sinkhole domain
            RobloxAccountSessionClient.TestHttpClientFactory = _ => new HttpClient(new FakeHttpHandler((_, _) =>
            {
                var redirectedReq = new HttpRequestMessage(HttpMethod.Get, "https://roblox.vnggames.com/redirect");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = redirectedReq,
                    Content = new StringContent("{\"status\":\"redirected\"}")
                });
            }));

            bool resultIspRedirect = await acc.ValidateSessionAsync();
            Assert.True(resultIspRedirect);
            Assert.True(acc.Valid);

            // Case 3: Genuine Roblox redirect to www.roblox.com/NewLogin MUST still mark expired
            RobloxAccountSessionClient.TestHttpClientFactory = _ => new HttpClient(new FakeHttpHandler((_, _) =>
            {
                var loginRedirect = new HttpRequestMessage(HttpMethod.Get, "https://www.roblox.com/NewLogin?ReturnUrl=%2f");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = loginRedirect,
                    Content = new StringContent("<html>Login</html>")
                });
            }));

            bool resultRobloxLogin = await acc.ValidateSessionAsync();
            Assert.False(resultRobloxLogin);
            Assert.False(acc.Valid);
        }

        [Fact]
        public void VaultSerialization_ReloginCopy_InteractiveScope_AndLogRedactor_ProtectHbaSecrets()
        {
            var (privB64, pubB64) = GenerateP256KeyPair();
            var source = new Account { Username = "VaultUser", UserID = 888, SecurityToken = SampleCookie, Valid = true };
            source.ApplyHbaCapture(new RobloxHbaService.CaptureSnapshot
            {
                PrivateKeyPkcs8Base64 = privB64,
                PublicKeySpkiBase64 = pubB64,
                TokenTemplate = RobloxHbaService.DefaultTemplate,
                UserAgent = "Mozilla/5.0 CustomUA",
                BrowserTrackerId = "9988776655"
            });

            // 1. Vault round-trip serialization
            byte[] serialized = AccountDataSerializer.Serialize(new[] { source }, includePasswords: true);
            var restoredList = AccountDataSerializer.Deserialize(serialized, out _);
            Assert.Single(restoredList);
            Assert.Equal(privB64, restoredList[0].HbaPrivateKey);
            Assert.Equal("Mozilla/5.0 CustomUA", restoredList[0].SessionUserAgent);
            Assert.Equal("9988776655", restoredList[0].BrowserTrackerID);

            // 2. Relogin copy onto existing Account instance
            var existing = new Account { Username = "VaultUser", UserID = 888 };
            source.CopyHbaAndFingerprintTo(existing);
            Assert.Equal(privB64, existing.HbaPrivateKey);
            Assert.Equal("9988776655", existing.BrowserTrackerID);

            // 3. Interactive login scope freeze guard
            Assert.False(RobloxHbaService.IsInteractiveLoginActive);
            using (RobloxHbaService.BeginInteractiveLoginScope())
            {
                Assert.True(RobloxHbaService.IsInteractiveLoginActive);
            }
            Assert.False(RobloxHbaService.IsInteractiveLoginActive);

            // 4. Log redaction of HBA private keys and BAT headers
            string sensitiveLog = $"{{\"HbaPrivateKey\":\"{privB64}\"}} Header x-bound-auth-token: v1|hash|123|sig1|sig2";
            string redacted = LogRedactor.Redact(sensitiveLog);
            Assert.DoesNotContain(privB64, redacted);
            Assert.DoesNotContain("sig1|sig2", redacted);
            Assert.Contains("[REDACTED_BAT]", redacted);
        }

        [Fact]
        public void GenerateBoundAuthToken_WithValidHbaKey_ReturnsSignedHeaderValue()
        {
            var account = new Account("TestHbaUser", "pass", "alias");
            using var ecdsa = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
            account.HbaPrivateKeyPkcs8 = Convert.ToBase64String(ecdsa.ExportPkcs8PrivateKey());
            account.HbaPublicKeySpki = Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo());

            string? token = RobloxHbaService.GenerateBoundAuthToken(account, "POST", "https://auth.roblox.com/v1/authentication-ticket");

            Assert.NotNull(token);
            Assert.StartsWith("v1|", token);
            Assert.Contains("|", token);
        }

        [Fact]
        public void TryAttachHbaHeader_WhenAccountHasNoHbaKey_DoesNotAttachHeader()
        {
            var account = new Account("NoHbaUser", "pass", "alias");
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://auth.roblox.com/v1/authentication-ticket");

            bool attached = RobloxHbaService.TryAttachHbaHeader(request, account);

            Assert.False(attached);
            Assert.False(request.Headers.Contains(RobloxHbaService.BoundAuthHeaderName));
        }

        [Fact]
        public void TryAttachHbaHeader_WhenAccountHasValidHbaKey_AttachesHeader()
        {
            var account = new Account("TestHbaUser", "pass", "alias");
            using var ecdsa = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
            account.HbaPrivateKeyPkcs8 = Convert.ToBase64String(ecdsa.ExportPkcs8PrivateKey());
            account.HbaPublicKeySpki = Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo());

            using var request = new HttpRequestMessage(HttpMethod.Post, "https://auth.roblox.com/v1/authentication-ticket");

            bool attached = RobloxHbaService.TryAttachHbaHeader(request, account);

            Assert.True(attached);
            Assert.True(request.Headers.Contains(RobloxHbaService.BoundAuthHeaderName));
            var headerValues = request.Headers.GetValues(RobloxHbaService.BoundAuthHeaderName);
            Assert.Single(headerValues);
            string headerValue = headerValues.First();
            Assert.StartsWith("v1|", headerValue);
        }

        [Fact]
        public async Task TryAttachHbaHeaderAsync_WhenAccountHasValidHbaKey_AttachesHeader()
        {
            var account = new Account("TestHbaUserAsync", "pass", "alias");
            using var ecdsa = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
            account.HbaPrivateKeyPkcs8 = Convert.ToBase64String(ecdsa.ExportPkcs8PrivateKey());
            account.HbaPublicKeySpki = Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo());

            using var request = new HttpRequestMessage(HttpMethod.Post, "https://auth.roblox.com/v1/authentication-ticket")
            {
                Content = new StringContent("{\"async\":true}", Encoding.UTF8, "application/json")
            };

            bool attached = await RobloxHbaService.TryAttachHbaHeaderAsync(request, account);

            Assert.True(attached);
            Assert.True(request.Headers.Contains(RobloxHbaService.BoundAuthHeaderName));
            var headerValues = request.Headers.GetValues(RobloxHbaService.BoundAuthHeaderName);
            Assert.Single(headerValues);
            string headerValue = headerValues.First();
            Assert.StartsWith("v1|", headerValue);
        }

        [Fact]
        public async Task GetAuthTicketAsync_RetriesClientAssertionOnceOnTransientSocketFailure()
        {
            var (privB64, pubB64) = GenerateP256KeyPair();
            string expectedSig = RobloxHbaService.GetLocalNetworkSignature();
            var acc = new Account
            {
                Username = "AssertionRetryUser",
                UserID = 99002,
                SecurityToken = SampleCookie,
                Valid = true,
                HbaPrivateKeyPkcs8 = privB64,
                HbaPublicKeySpki = pubB64,
                HbaTemplate = RobloxHbaService.DefaultTemplate,
                LastBoundIpSignature = expectedSig
            };

            const string recoveredAssertionJson = "{\"clientAssertion\":\"eyJ-recovered-after-vpn-flap\"}";
            int assertionAttempts = 0;
            string? capturedTicketBody = null;

            var prevFactory = RobloxAccountSessionClient.TestHttpClientFactory;
            RobloxAccountSessionClient.TestHttpClientFactory = _ => new HttpClient(new FakeHttpHandler(async (req, _) =>
            {
                string uri = req.RequestUri?.AbsoluteUri ?? string.Empty;
                if (req.Method == HttpMethod.Get && uri.Contains("/v1/client-assertion", StringComparison.OrdinalIgnoreCase))
                {
                    assertionAttempts++;
                    if (assertionAttempts == 1)
                        throw new HttpRequestException("An error occurred while sending the request.");

                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(recoveredAssertionJson, Encoding.UTF8, "application/json")
                    };
                }

                if (req.Method == HttpMethod.Post && uri.Contains("/v1/authentication-ticket", StringComparison.OrdinalIgnoreCase))
                {
                    capturedTicketBody = req.Content != null ? await req.Content.ReadAsStringAsync() : null;
                    var resp = new HttpResponseMessage(HttpStatusCode.OK);
                    resp.Headers.TryAddWithoutValidation("rbx-authentication-ticket", "ticket-after-assertion-retry");
                    return resp;
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }));

            try
            {
                var (ok, ticket) = await acc.GetAuthTicketAsync();

                Assert.True(ok);
                Assert.Equal("ticket-after-assertion-retry", ticket);
                Assert.Equal(2, assertionAttempts);
                Assert.Equal(recoveredAssertionJson, capturedTicketBody);
            }
            finally
            {
                RobloxAccountSessionClient.TestHttpClientFactory = prevFactory;
            }
        }
    }
}

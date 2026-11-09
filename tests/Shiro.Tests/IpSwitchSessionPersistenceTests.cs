#nullable enable
using System;
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
    [Collection("StaticSeams")]
    public partial class IpSwitchSessionPersistenceTests : IDisposable
    {
        private static readonly string VpnInitialCookie =
            "_|WARNING:-DO-NOT-SHARE-THIS.--Sharing-this-will-allow-someone-to-log-in-as-you-and-to-steal-your-ROBUX-and-items.|_" +
            "H." + new string('V', 939) + ".S";

        private static readonly string VietnamRotatedCookie =
            "_|WARNING:-DO-NOT-SHARE-THIS.--Sharing-this-will-allow-someone-to-log-in-as-you-and-to-steal-your-ROBUX-and-items.|_" +
            "H." + new string('N', 939) + ".S";

        public IpSwitchSessionPersistenceTests() => RobloxRateLimitCoordinator.Instance.ResetBackoff();

        public void Dispose()
        {
            Account.TestHttpClientFactory = null;
            AccountLaunchService.TestGetAuthTicketOverride = null;
            AccountLaunchService.TestStartNativeGameOverride = null;
            RobloxRateLimitCoordinator.Instance.ResetBackoff();
        }

        private static (string Pkcs8Base64, string SpkiBase64) GenerateP256KeyPair()
        {
            using ECDsa ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            return (
                Convert.ToBase64String(ecdsa.ExportPkcs8PrivateKey()),
                Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo()));
        }

        private static string GetCookieHeader(HttpRequestMessage req) =>
            req.Headers.TryGetValues("Cookie", out var vals) ? string.Join("; ", vals) : string.Empty;

        [Fact]
        public async Task TryRebindSessionToCurrentNetworkAsync_WhenTicketRejectsPreRotatedCookieWith401_SynchronizesViaUsersEndpointAndAllowsTicket()
        {
            var (privB64, pubB64) = GenerateP256KeyPair();

            Account.TestHttpClientFactory = _ => new HttpClient(new FakeHttpHandler((req, _) =>
            {
                string uri = req.RequestUri!.AbsoluteUri;
                string cookie = GetCookieHeader(req);
                bool hasBat = req.Headers.Contains(RobloxHbaService.BoundAuthHeaderName);

                // On normal Vietnam IP after turning off VPN:
                // users.roblox.com/v1/users/authenticated accepts HBA-signed request and rotates .ROBLOSECURITY onto the new IP.
                if (uri.Contains("users.roblox.com/v1/users/authenticated", StringComparison.OrdinalIgnoreCase))
                {
                    if (!hasBat)
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));

                    var userResp = new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"id\":101,\"name\":\"MckeeDaffodil7\",\"displayName\":\"Mckee\"}")
                    };
                    if (!cookie.Contains(VietnamRotatedCookie, StringComparison.Ordinal))
                    {
                        userResp.Headers.TryAddWithoutValidation(
                            "Set-Cookie",
                            $".ROBLOSECURITY={VietnamRotatedCookie}; Path=/; Domain=.roblox.com; Secure; HttpOnly");
                    }
                    return Task.FromResult(userResp);
                }

                // auth.roblox.com/v1/authentication-ticket/ rejects the un-rotated VPN cookie with 401,
                // and only issues a launch ticket once the cookie has been rotated onto the Vietnam IP.
                if (uri.Contains("/v1/authentication-ticket", StringComparison.OrdinalIgnoreCase) &&
                    !uri.Contains("/redeem", StringComparison.OrdinalIgnoreCase))
                {
                    if (!cookie.Contains(VietnamRotatedCookie, StringComparison.Ordinal))
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));

                    var ticketResp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
                    ticketResp.Headers.TryAddWithoutValidation("x-csrf-token", "csrf-vn-ok");
                    ticketResp.Headers.TryAddWithoutValidation("rbx-authentication-ticket", "VN_LAUNCH_TICKET_OK");
                    return Task.FromResult(ticketResp);
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
            }));

            var acc = new Account
            {
                Username = "MckeeDaffodil7",
                UserID = 101,
                SecurityToken = VpnInitialCookie,
                Valid = true
            };
            acc.ApplyHbaCapture(new RobloxHbaService.CaptureSnapshot
            {
                PrivateKeyPkcs8Base64 = privB64,
                PublicKeySpkiBase64 = pubB64,
                BrowserTrackerId = "1793883083896002"
            });
            acc.LastBoundIpSignature = "old_warp_vpn_signature";

            bool rebound = await acc.TryRebindSessionToCurrentNetworkAsync(force: true);
            Assert.True(rebound);
            Assert.Equal(VietnamRotatedCookie, acc.SecurityToken);

            var (ticketSuccess, ticket) = await acc.GetAuthTicketAsync();
            Assert.True(ticketSuccess);
            Assert.Equal("VN_LAUNCH_TICKET_OK", ticket);
            Assert.True(acc.Valid);
        }

        [Fact]
        public async Task TryRebindSessionToCurrentNetworkAsync_WhenUsersReturns200WithoutRotation_RedeemsTicketAndSucceeds()
        {
            var (privB64, pubB64) = GenerateP256KeyPair();
            bool redeemCalled = false;

            Account.TestHttpClientFactory = _ => new HttpClient(new FakeHttpHandler(async (req, ct) =>
            {
                string uri = req.RequestUri!.AbsoluteUri;
                string cookie = GetCookieHeader(req);

                // users/authenticated returns 200 without rotating cookie (standard Roblox behavior)
                if (uri.Contains("users.roblox.com/v1/users/authenticated", StringComparison.OrdinalIgnoreCase))
                {
                    var userResp = new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"id\":105,\"name\":\"Real_KnightFool\"}")
                    };
                    return userResp;
                }

                // Stage 2 calls GetAuthTicketCoreAsync to obtain ticket
                if (uri.Contains("/v1/authentication-ticket", StringComparison.OrdinalIgnoreCase) &&
                    !uri.Contains("/redeem", StringComparison.OrdinalIgnoreCase))
                {
                    var ticketResp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
                    ticketResp.Headers.TryAddWithoutValidation("x-csrf-token", "csrf-vn-ok");
                    ticketResp.Headers.TryAddWithoutValidation("rbx-authentication-ticket", "TICKET_FOR_REDEEM");
                    return ticketResp;
                }

                // Stage 2 then redeems ticket at v1/authentication-ticket/redeem to rotate cookie
                if (uri.Contains("/v1/authentication-ticket/redeem", StringComparison.OrdinalIgnoreCase))
                {
                    redeemCalled = true;
                    string body = await req.Content!.ReadAsStringAsync(ct);
                    Assert.Contains("TICKET_FOR_REDEEM", body);

                    var redeemResp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
                    redeemResp.Headers.TryAddWithoutValidation(
                        "Set-Cookie",
                        $".ROBLOSECURITY={VietnamRotatedCookie}; Path=/; Domain=.roblox.com; Secure; HttpOnly");
                    return redeemResp;
                }

                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
            }));

            var acc = new Account
            {
                Username = "Real_KnightFool",
                UserID = 105,
                SecurityToken = VpnInitialCookie,
                Valid = true
            };
            acc.ApplyHbaCapture(new RobloxHbaService.CaptureSnapshot
            {
                PrivateKeyPkcs8Base64 = privB64,
                PublicKeySpkiBase64 = pubB64,
                BrowserTrackerId = "1793883083896005"
            });
            acc.LastBoundIpSignature = "japan_vpn_ip_sig";

            bool rebound = await acc.TryRebindSessionToCurrentNetworkAsync(force: true);
            Assert.True(rebound);
            Assert.True(redeemCalled);
            Assert.Equal(VietnamRotatedCookie, acc.SecurityToken);

            var (ticketOk, ticket) = await acc.GetAuthTicketAsync();
            Assert.True(ticketOk);
            Assert.Equal("TICKET_FOR_REDEEM", ticket);
        }

        [Fact]
        public async Task GetAuthTicketAsync_WhenExternalIpChangedWithoutNicChange_SelfHealsOn401ViaUsersEndpointRotation()
        {
            var (privB64, pubB64) = GenerateP256KeyPair();
            int ticketCalls = 0;

            Account.TestHttpClientFactory = _ => new HttpClient(new FakeHttpHandler((req, _) =>
            {
                string uri = req.RequestUri!.AbsoluteUri;
                string cookie = GetCookieHeader(req);

                if (uri.Contains("/v1/authentication-ticket", StringComparison.OrdinalIgnoreCase) &&
                    !uri.Contains("/redeem", StringComparison.OrdinalIgnoreCase))
                {
                    Interlocked.Increment(ref ticketCalls);
                    if (!cookie.Contains(VietnamRotatedCookie, StringComparison.Ordinal))
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));

                    if (!req.Headers.Contains("X-CSRF-TOKEN"))
                    {
                        var csrfChallenge = new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{}") };
                        csrfChallenge.Headers.TryAddWithoutValidation("x-csrf-token", "csrf-after-rotate");
                        return Task.FromResult(csrfChallenge);
                    }

                    var okResp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
                    okResp.Headers.TryAddWithoutValidation("rbx-authentication-ticket", "SELF_HEALED_TICKET");
                    return Task.FromResult(okResp);
                }

                if (uri.Contains("users.roblox.com/v1/users/authenticated", StringComparison.OrdinalIgnoreCase))
                {
                    var userResp = new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"id\":202,\"name\":\"NormalIpPlayer\"}")
                    };
                    userResp.Headers.TryAddWithoutValidation(
                        "Set-Cookie",
                        $".ROBLOSECURITY={VietnamRotatedCookie}; Path=/; Domain=.roblox.com; Secure; HttpOnly");
                    return Task.FromResult(userResp);
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
            }));

            var acc = new Account
            {
                Username = "NormalIpPlayer",
                UserID = 202,
                SecurityToken = VpnInitialCookie,
                Valid = true
            };
            acc.ApplyHbaCapture(new RobloxHbaService.CaptureSnapshot
            {
                PrivateKeyPkcs8Base64 = privB64,
                PublicKeySpkiBase64 = pubB64
            });

            var (success, ticket) = await acc.GetAuthTicketAsync();

            Assert.True(success);
            Assert.Equal("SELF_HEALED_TICKET", ticket);
            Assert.Equal(VietnamRotatedCookie, acc.SecurityToken);
            Assert.True(ticketCalls <= 3, $"Expected self-healing without 4 blind 401 retries, got {ticketCalls} ticket calls.");
        }

        [Fact]
        public async Task GetAuthTicketAsync_WhenInitialAttemptFailsWithHba401_RetriesWithoutHbaAndSucceeds()
        {
            var (privB64, pubB64) = GenerateP256KeyPair();
            bool hbaAttemptSeen = false;
            bool noHbaAttemptSeen = false;

            Account.TestHttpClientFactory = _ => new HttpClient(new FakeHttpHandler((req, _) =>
            {
                string uri = req.RequestUri!.AbsoluteUri;
                if (uri.Contains("auth.roblox.com/v1/authentication-ticket", StringComparison.OrdinalIgnoreCase))
                {
                    if (req.Headers.Contains(RobloxHbaService.BoundAuthHeaderName))
                    {
                        hbaAttemptSeen = true;
                        // Simulates Roblox rejecting an HBA signature because server does not have this key (e.g. Quick Sign In)
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
                    }

                    noHbaAttemptSeen = true;
                    var ticketResp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
                    ticketResp.Headers.TryAddWithoutValidation("x-csrf-token", "csrf-ticket");
                    ticketResp.Headers.TryAddWithoutValidation("rbx-authentication-ticket", "FALLBACK_TICKET_OK");
                    return Task.FromResult(ticketResp);
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
            }));

            var acc = new Account
            {
                Username = "QuickSignInUser",
                UserID = 303,
                SecurityToken = VpnInitialCookie,
                Valid = true
            };
            acc.ApplyHbaCapture(new RobloxHbaService.CaptureSnapshot
            {
                PrivateKeyPkcs8Base64 = privB64,
                PublicKeySpkiBase64 = pubB64
            });

            var (success, ticket) = await acc.GetAuthTicketAsync();

            Assert.True(success);
            Assert.True(hbaAttemptSeen);
            Assert.True(noHbaAttemptSeen);
            Assert.Equal("FALLBACK_TICKET_OK", ticket);
        }
    }
}

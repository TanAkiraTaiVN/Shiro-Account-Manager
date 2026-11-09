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
    public class IpSwitchSessionPersistenceTests : IDisposable
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

        [Fact]
        public async Task CreateAsync_WhenUsersEndpointReturns403WithRotatedSetCookie_RetriesWithRotatedCookieAndResolvesAccount()
        {
            Account.TestHttpClientFactory = _ => new HttpClient(new FakeHttpHandler((req, _) =>
            {
                string uri = req.RequestUri!.AbsoluteUri;
                string cookie = GetCookieHeader(req);

                if (uri.Contains("users.roblox.com/v1/users/authenticated", StringComparison.OrdinalIgnoreCase))
                {
                    if (!cookie.Contains(VietnamRotatedCookie, StringComparison.Ordinal))
                    {
                        var forbiddenRotate = new HttpResponseMessage(HttpStatusCode.Forbidden)
                        {
                            Content = new StringContent("{\"errors\":[{\"code\":0,\"message\":\"Token Validation Failed\"}]}")
                        };
                        forbiddenRotate.Headers.TryAddWithoutValidation(
                            "Set-Cookie",
                            $".ROBLOSECURITY={VietnamRotatedCookie}; Path=/; Domain=.roblox.com; Secure; HttpOnly");
                        return Task.FromResult(forbiddenRotate);
                    }

                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"id\":777,\"name\":\"Real_KnightFool\",\"displayName\":\"Knight\"}")
                    });
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
            }));

            Account created = await Account.CreateAsync(VpnInitialCookie);

            Assert.True(created.Valid);
            Assert.Equal(777, created.UserID);
            Assert.Equal("Real_KnightFool", created.Username);
            Assert.Equal(VietnamRotatedCookie, created.SecurityToken);
        }

        [Fact]
        public async Task LaunchCoordinator_WhenTicketFailsOnceButValidationRotatesCookie_RetriesLaunchAndSucceeds()
        {
            Account.TestHttpClientFactory = _ => new HttpClient(new FakeHttpHandler((req, _) =>
            {
                string uri = req.RequestUri!.AbsoluteUri;
                if (uri.Contains("users.roblox.com/v1/users/authenticated", StringComparison.OrdinalIgnoreCase))
                {
                    var resp = new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"id\":505,\"name\":\"RecoverablePlayer\"}")
                    };
                    resp.Headers.TryAddWithoutValidation(
                        "Set-Cookie",
                        $".ROBLOSECURITY={VietnamRotatedCookie}; Path=/; Domain=.roblox.com; Secure; HttpOnly");
                    return Task.FromResult(resp);
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
            }));

            var acc = new Account
            {
                Username = "RecoverablePlayer",
                UserID = 505,
                SecurityToken = VpnInitialCookie,
                Valid = true
            };

            int launchAttempts = 0;
            using var coordinator = new LaunchCoordinator((req, _) =>
            {
                launchAttempts++;
                if (req.Account.SecurityToken == VietnamRotatedCookie)
                    return Task.FromResult(LaunchResult.Success());
                return Task.FromResult(LaunchResult.Fail(LaunchOutcome.TicketError, "Ticket 401 before cookie rotation"));
            });

            var batch = await coordinator.EnqueueAsync(
                new[] { new LaunchRequest(acc, 189707, string.Empty, false, false) },
                delaySeconds: 1,
                asyncJoin: false,
                progress: null);

            Assert.Equal(2, launchAttempts);
            Assert.Single(batch.Results);
            Assert.Equal(LaunchOutcome.Success, batch.Results[0].Status);
            Assert.True(acc.Valid);
            Assert.Equal(VietnamRotatedCookie, acc.SecurityToken);
        }

        [Fact]
        public async Task LaunchCoordinator_WhenTicketFailsTemporarilyWhileSessionStillValid_PreservesAccountValidState()
        {
            Account.TestHttpClientFactory = _ => new HttpClient(new FakeHttpHandler((req, _) =>
            {
                if (req.RequestUri!.Host.Equals("users.roblox.com", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"id\":606,\"name\":\"ValidButTicketDown\"}")
                    });
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
            }));

            var acc = new Account
            {
                Username = "ValidButTicketDown",
                UserID = 606,
                SecurityToken = VpnInitialCookie,
                Valid = true
            };

            using var coordinator = new LaunchCoordinator((_, _) =>
                Task.FromResult(LaunchResult.Fail(LaunchOutcome.TicketError, "Transient auth.roblox.com outage")));

            var batch = await coordinator.EnqueueAsync(
                new[] { new LaunchRequest(acc, 189707, string.Empty, false, false) },
                delaySeconds: 1,
                asyncJoin: false,
                progress: null);

            Assert.Single(batch.Results);
            Assert.True(acc.Valid);
        }

        [Fact]
        public void GetBrowserCaptureScript_ReadsHbaDbObjectStoreAndHooksHeadersSet()
        {
            string script = RobloxHbaService.GetBrowserCaptureScript();

            Assert.Contains("hbaDB", script);
            Assert.Contains("hbaObjectStore", script);
            Assert.Contains("hba_keys", script);
            Assert.Contains("Headers.prototype", script);
        }

        [Fact]
        public void CanSyncRotatedCookieForTarget_WhenAccountHasBrowserHbaBinding_ReturnsFalse()
        {
            var (privB64, pubB64) = GenerateP256KeyPair();
            var acc = new Account
            {
                Username = "HbaBoundAcc",
                UserID = 909,
                SecurityToken = VpnInitialCookie,
                BrowserTrackerID = "1793883083896002",
                Valid = true
            };
            acc.ApplyHbaCapture(new RobloxHbaService.CaptureSnapshot
            {
                PrivateKeyPkcs8Base64 = privB64,
                PublicKeySpkiBase64 = pubB64,
                BrowserTrackerId = "1793883083896002"
            });

            bool canSyncFromDesktopCookiesDat = RobloxDirectLauncher.CanSyncRotatedCookieForTarget(
                acc,
                trackerUserId: 909,
                trackerBrowserId: "1793883083896002",
                expectedBrowserId: "1793883083896002");

            Assert.False(canSyncFromDesktopCookiesDat);
        }

        [Fact]
        public void ComputeNetworkSignature_DetectsGatewaylessWireGuardWarpAdapterAndSystemProxyChanges()
        {
            var baseWifi = new RobloxHbaService.NetworkAdapterSnapshot(
                Id: "{WIFI-NIC}",
                InterfaceType: System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211,
                HasDefaultGateway: true,
                UnicastAddresses: new[] { "192.168.1.15" },
                GatewayAddresses: new[] { "192.168.1.1" });

            // Cloudflare WARP / WireGuard wintun adapter on Windows reports PropVirtual (53) and empty GatewayAddresses
            var warpAdapter = new RobloxHbaService.NetworkAdapterSnapshot(
                Id: "{WARP-WINTUN}",
                InterfaceType: (System.Net.NetworkInformation.NetworkInterfaceType)53,
                HasDefaultGateway: false,
                UnicastAddresses: new[] { "172.16.0.2", "2606:4700:110:8a12::2" },
                GatewayAddresses: Array.Empty<string>());

            var apipaNoise = new RobloxHbaService.NetworkAdapterSnapshot(
                Id: "{APIPA-NIC}",
                InterfaceType: System.Net.NetworkInformation.NetworkInterfaceType.Ethernet,
                HasDefaultGateway: false,
                UnicastAddresses: new[] { "169.254.10.20", "fe80::1234" },
                GatewayAddresses: Array.Empty<string>());

            string normalVietnamSig = RobloxHbaService.ComputeNetworkSignature(new[] { baseWifi });
            string withApipaSig = RobloxHbaService.ComputeNetworkSignature(new[] { baseWifi, apipaNoise });
            string withWarpSig = RobloxHbaService.ComputeNetworkSignature(new[] { baseWifi, warpAdapter });
            string withSystemProxySig = RobloxHbaService.ComputeNetworkSignature(new[] { baseWifi }, "http://127.0.0.1:7890");
            string differentRouterSig = RobloxHbaService.ComputeNetworkSignature(new[]
            {
                baseWifi with { GatewayAddresses = new[] { "192.168.1.254" } }
            });

            Assert.Equal(normalVietnamSig, withApipaSig);
            Assert.NotEqual(normalVietnamSig, withWarpSig);
            Assert.NotEqual(normalVietnamSig, withSystemProxySig);
            Assert.NotEqual(normalVietnamSig, differentRouterSig);
        }

        [Fact]
        public async Task JoinServerResultAsync_WhenNetworkChangedAndStage2TicketFailsOnce_DoesNotRunRebindTwice()
        {
            var (privB64, pubB64) = GenerateP256KeyPair();
            int usersAuthCalls = 0;
            int ticketCalls = 0;

            AccountLaunchService.TestGetAuthTicketOverride = null;
            AccountLaunchService.TestStartNativeGameOverride = (_, _, _, _, _, _, _) => Task.FromResult(4242);

            Account.TestHttpClientFactory = _ => new HttpClient(new FakeHttpHandler((req, _) =>
            {
                string uri = req.RequestUri!.AbsoluteUri;
                if (uri.Contains("users.roblox.com/v1/users/authenticated", StringComparison.OrdinalIgnoreCase))
                {
                    Interlocked.Increment(ref usersAuthCalls);
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"id\":707,\"name\":\"NoDupRebindUser\"}")
                    });
                }

                if (uri.Contains("/v1/authentication-ticket", StringComparison.OrdinalIgnoreCase) &&
                    !uri.Contains("/redeem", StringComparison.OrdinalIgnoreCase))
                {
                    int callNum = Interlocked.Increment(ref ticketCalls);
                    if (callNum == 1)
                    {
                        // Stage 2 ticket probe fails transiently without ticket header -> falls through to Stage 3
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
                    }

                    var ticketResp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
                    ticketResp.Headers.TryAddWithoutValidation("x-csrf-token", "csrf-launch");
                    ticketResp.Headers.TryAddWithoutValidation("rbx-authentication-ticket", "LAUNCH_TICKET_OK");
                    return Task.FromResult(ticketResp);
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
            }));

            var acc = new Account
            {
                Username = "NoDupRebindUser",
                UserID = 707,
                SecurityToken = VpnInitialCookie,
                BrowserTrackerID = "1234567890",
                Valid = true
            };
            acc.ApplyHbaCapture(new RobloxHbaService.CaptureSnapshot
            {
                PrivateKeyPkcs8Base64 = privB64,
                PublicKeySpkiBase64 = pubB64,
                BrowserTrackerId = "1234567890"
            });
            acc.LastBoundIpSignature = "old_vpn_sig";

            var result = await AccountLaunchService.Instance.JoinServerResultAsync(acc, 189707, "job-123");

            Assert.Equal(LaunchOutcome.Success, result.Outcome);
            Assert.Equal(1, usersAuthCalls);
        }
    }
}

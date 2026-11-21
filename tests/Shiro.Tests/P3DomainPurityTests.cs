#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using NetArchTest.Rules;
using Shiro.Http;
using Shiro.Services;
using Shiro.Tests.TestInfrastructure;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public sealed class P3DomainPurityTests : IDisposable
    {
        private static string RepoRoot => ArchitectureTests.RepoRoot;
        private readonly Func<Account, HttpClient>? _origSessionClientFactory = RobloxAccountSessionClient.TestHttpClientFactory;

        public void Dispose()
        {
            RobloxAccountSessionClient.TestHttpClientFactory = _origSessionClientFactory;
            RobloxRateLimitCoordinator.Instance.ResetBackoff();
        }

        [Fact]
        public void DomainAccount_DoesNotImportHttpClientOrRestInternalsDirectly()
        {
            var domainFiles = Directory.EnumerateFiles(Path.Combine(RepoRoot, "src", "Shiro", "Domain"), "*.cs", SearchOption.AllDirectories).ToList();
            Assert.NotEmpty(domainFiles);

            foreach (string file in domainFiles)
            {
                string content = File.ReadAllText(file);
                Assert.DoesNotContain("System.Net.Http", content, StringComparison.Ordinal);
                Assert.DoesNotContain("Shiro.Http", content, StringComparison.Ordinal);
                Assert.DoesNotContain("HttpClient", content, StringComparison.Ordinal);
                Assert.DoesNotContain("RestClient", content, StringComparison.Ordinal);
                Assert.DoesNotContain("RestRequest", content, StringComparison.Ordinal);
                Assert.DoesNotContain("RestResponse", content, StringComparison.Ordinal);
            }

            var archResult = Types.InAssembly(typeof(Account).Assembly)
                .That()
                .ResideInNamespace("Shiro")
                .And()
                .HaveName("Account")
                .ShouldNot()
                .HaveDependencyOnAny("System.Net.Http", "Shiro.Http")
                .GetResult();
            Assert.True(archResult.IsSuccessful, "Account should have zero IL dependency on System.Net.Http or Shiro.Http.");

            Assert.False(typeof(RobloxAccountSessionClient).IsAbstract);
            Assert.False(typeof(RobloxAccountApiService).IsAbstract);
            Assert.False(string.IsNullOrWhiteSpace(HbaBrowserScripts.BuildCollectionScript()));
        }

        [Fact]
        public void AccountPartials_TotalLines_IsStrictlyBelow950_AndWithinGlobalBudget()
        {
            string domainDir = Path.Combine(RepoRoot, "src", "Shiro", "Domain");
            var accountFiles = Directory.EnumerateFiles(domainDir, "Account*.cs", SearchOption.TopDirectoryOnly)
                .Where(f => !Path.GetFileName(f).Equals("AccountJson.cs", StringComparison.OrdinalIgnoreCase) &&
                            !Path.GetFileName(f).Equals("AccountSearchHelper.cs", StringComparison.OrdinalIgnoreCase))
                .ToList();

            Assert.NotEmpty(accountFiles);
            int totalLines = 0;
            foreach (string file in accountFiles)
            {
                int lines = File.ReadAllLines(file).Length;
                Assert.True(lines <= 485, $"{Path.GetFileName(file)} has {lines} lines (max 485).");
                totalLines += lines;
            }

            Assert.True(totalLines < 950, $"Account partial total is {totalLines} lines (expected < 950).");
        }

        [Fact]
        public void HbaBrowserScripts_ContainsCollectionAndModerationScripts_AndShrinksRobloxHbaService()
        {
            string authDir = Path.Combine(RepoRoot, "src", "Shiro", "Services", "Auth");
            var hbaFiles = Directory.EnumerateFiles(authDir, "RobloxHbaService*.cs", SearchOption.TopDirectoryOnly).ToList();
            int hbaTotalLines = hbaFiles.Sum(f => File.ReadAllLines(f).Length);
            Assert.True(hbaTotalLines < 950, $"RobloxHbaService total lines {hbaTotalLines} should be well below 1150.");

            int hooksLines = File.ReadAllLines(Path.Combine(authDir, "RobloxHbaService.BrowserHooks.cs")).Length;
            Assert.True(hooksLines <= 40, $"RobloxHbaService.BrowserHooks.cs should be a thin wrapper (actual {hooksLines} lines).");

            string collection = HbaBrowserScripts.BuildCollectionScript();
            Assert.Equal(collection, RobloxHbaService.GetBrowserCaptureScript());
            Assert.Contains("SubtleCrypto", collection, StringComparison.Ordinal);
            Assert.Contains("hbaDB", collection, StringComparison.Ordinal);
            Assert.Contains("shiro_hba_captured", collection, StringComparison.Ordinal);

            Assert.Equal(string.Empty, HbaBrowserScripts.BuildRestoreModerationScript(null, null, null));
            Assert.Equal(string.Empty, HbaBrowserScripts.BuildRestoreModerationScript("   ", "", ""));

            using ECDsa ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            string privB64 = Convert.ToBase64String(ecdsa.ExportPkcs8PrivateKey());
            string expectedSpki = Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo());
            string restoreScript = HbaBrowserScripts.BuildRestoreModerationScript(privB64, null, null);
            Assert.Equal(restoreScript, RobloxHbaService.GetPersistedKeyInjectionScript(privB64, null, null));
            Assert.Contains(privB64, restoreScript, StringComparison.Ordinal);
            Assert.Contains(expectedSpki, restoreScript, StringComparison.Ordinal);
            Assert.Contains(RobloxHbaService.DefaultTemplate, restoreScript, StringComparison.Ordinal);
        }

        [Fact]
        public async Task RobloxAccountSessionClient_IsRegisteredInCompositionRoot_AndHandlesSessionAndTicketFlows()
        {
            await using var bootstrap = new AppBootstrap();
            await bootstrap.InitializeAsync();
            var sessionClient = bootstrap.Services.GetRequiredService<RobloxAccountSessionClient>();
            var apiService = bootstrap.Services.GetRequiredService<RobloxAccountApiService>();

            Assert.NotNull(sessionClient);
            Assert.NotNull(apiService);
            Assert.Same(RobloxAccountSessionClient.Default, sessionClient);
            Assert.Same(RobloxAccountApiService.Default, apiService);
            Assert.Same(RobloxAccountSessionClient.Default, AppServices.AccountSessionClient);
            Assert.Same(RobloxAccountApiService.Default, AppServices.AccountApiService);

            string rotatedCookie = "_|WARNING:-DO-NOT-SHARE-THIS.--" + new string('R', 120);
            var account = new Account
            {
                Username = "PuritySessionUser",
                UserID = 771122,
                SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.--" + new string('A', 120),
                Valid = false
            };

            int ticketCalls = 0;
            RobloxAccountSessionClient.TestHttpClientFactory = _ => new HttpClient(new FakeHttpHandler(req =>
            {
                string url = req.RequestUri!.AbsoluteUri;
                if (url.Contains("users.roblox.com/v1/users/authenticated", StringComparison.OrdinalIgnoreCase))
                {
                    var resp = new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"id\":771122,\"name\":\"PuritySessionUser\"}")
                    };
                    resp.Headers.TryAddWithoutValidation("Set-Cookie", $".ROBLOSECURITY={rotatedCookie}; Path=/; Domain=.roblox.com; HttpOnly");
                    return resp;
                }
                if (url.Contains("accountsettings.roblox.com/v1/email", StringComparison.OrdinalIgnoreCase))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"emailAddress\":\"a@b.com\"}") };
                }
                if (url.Contains("auth.roblox.com/v1/client-assertion", StringComparison.OrdinalIgnoreCase))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"assertion\":\"ok\"}") };
                }
                if (url.Contains("auth.roblox.com/v1/authentication-ticket", StringComparison.OrdinalIgnoreCase))
                {
                    ticketCalls++;
                    if (ticketCalls == 1)
                    {
                        var challenge = new HttpResponseMessage(HttpStatusCode.Forbidden);
                        challenge.Headers.TryAddWithoutValidation("x-csrf-token", "csrf-purity-1");
                        return challenge;
                    }

                    var ticketOk = new HttpResponseMessage(HttpStatusCode.OK);
                    ticketOk.Headers.TryAddWithoutValidation("rbx-authentication-ticket", "TICKET-PURITY-99");
                    return ticketOk;
                }
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }));

            bool valid = await sessionClient.ValidateSessionAsync(account);
            Assert.True(valid);
            Assert.True(account.Valid);
            Assert.Equal(rotatedCookie, account.SecurityToken);

            var (ticketSuccess, ticket) = await sessionClient.GetAuthTicketAsync(account);
            Assert.True(ticketSuccess);
            Assert.Equal("TICKET-PURITY-99", ticket);
            Assert.Equal(2, ticketCalls);
        }

        [Fact]
        public async Task RobloxAccountApiService_MakeRequestApplyFingerprintAndExecuteRest_SignsAndRotatesCookie()
        {
            string rotatedCookie = "_|WARNING:-DO-NOT-SHARE-THIS.--" + new string('Z', 120);
            var account = new Account
            {
                Username = "PurityApiUser",
                UserID = 882233,
                BrowserTrackerID = "55667788",
                SessionUserAgent = "PurityAgent/2.0",
                SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.--" + new string('B', 120)
            };

            string? capturedUa = null;
            string? capturedCookie = null;
            var apiService = new RobloxAccountApiService();

            RobloxAccountSessionClient.TestHttpClientFactory = _ => new HttpClient(new FakeHttpHandler(req =>
            {
                capturedUa = req.Headers.TryGetValues("User-Agent", out var uas) ? string.Join(" ", uas) : null;
                capturedCookie = req.Headers.TryGetValues("Cookie", out var cks) ? string.Join("; ", cks) : null;
                var resp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true}") };
                resp.Headers.TryAddWithoutValidation("Set-Cookie", $".ROBLOSECURITY={rotatedCookie}; Path=/");
                return resp;
            }));

            RestRequest req = apiService.MakeRequest(account, "v1/test-endpoint", Method.Get);
            RestResponse? restResp = await apiService.ExecuteAccountRestAsync(account, null, "https://users.roblox.com/", req);

            Assert.NotNull(restResp);
            Assert.True(restResp!.IsSuccessful);
            Assert.Contains("PurityAgent/2.0", capturedUa);
            Assert.Contains("RBXEventTrackerV2=", capturedCookie);
            Assert.Equal(rotatedCookie, account.SecurityToken);
        }

        [Fact]
        public async Task RobloxAccountSessionClient_TryRebindSessionToCurrentNetworkAsync_SynchronizesRotatedCookie()
        {
            string rotatedCookie = "_|WARNING:-DO-NOT-SHARE-THIS.--" + new string('N', 120);
            var account = new Account
            {
                Username = "RebindPurityUser",
                UserID = 993344,
                SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.--" + new string('O', 120),
                LastBoundIpSignature = "old-network-sig",
                Valid = true
            };

            RobloxAccountSessionClient.TestHttpClientFactory = _ => new HttpClient(new FakeHttpHandler(req =>
            {
                if (req.RequestUri!.AbsoluteUri.Contains("users.roblox.com/v1/users/authenticated", StringComparison.OrdinalIgnoreCase))
                {
                    var resp = new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"id\":993344,\"name\":\"RebindPurityUser\"}")
                    };
                    resp.Headers.TryAddWithoutValidation("Set-Cookie", $".ROBLOSECURITY={rotatedCookie}; Path=/");
                    return resp;
                }
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }));

            var sessionClient = new RobloxAccountSessionClient();
            bool rebound = await sessionClient.TryRebindSessionToCurrentNetworkAsync(account, force: true);

            Assert.True(rebound);
            Assert.Equal(rotatedCookie, account.SecurityToken);
        }
    }
}

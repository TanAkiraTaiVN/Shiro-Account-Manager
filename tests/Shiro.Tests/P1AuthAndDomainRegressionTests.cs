#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Http;
using Shiro.Services;
using Shiro.Tests.TestInfrastructure;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public class P1AuthAndDomainRegressionTests : IDisposable
    {
        private readonly string _oldHbaKey;
        private readonly List<Account> _oldAccounts;
        private readonly Account? _oldLastValid;

        public P1AuthAndDomainRegressionTests()
        {
            Account.TestHttpClientFactory = null;
            RobloxRateLimitCoordinator.Instance.ResetBackoff();
            _oldHbaKey = string.Empty;
            _oldAccounts = AppServices.AccountRepository.GetAll().ToList();
            _oldLastValid = AppServices.SelectionState.LastValidAccount;
        }

        public void Dispose()
        {
            RobloxRateLimitCoordinator.Instance.ResetBackoff();
            Account.TestHttpClientFactory = null;
            AccountManager.ResetAccountsForTests(_oldAccounts);
            AppServices.SelectionState.LastValidAccount = _oldLastValid;
            _ = _oldHbaKey;
        }

        private static string CreateValidTestPkcs8()
        {
            using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            return Convert.ToBase64String(ec.ExportPkcs8PrivateKey());
        }

        [Fact]
        public void HighB1_HbaPrivateKey_IsProtectedOutOfFields_BlockedFromGetSetField_AndClearedInMemory()
        {
            string pkcs8 = CreateValidTestPkcs8();
            var acc = new Account { Username = "HbaRamUser", UserID = 1001 };
            acc.HbaPrivateKey = pkcs8;
            acc.HbaPublicKey = "test-spki";
            acc.HbaTemplate = "HB,TS,U,M";
            acc.LastBoundIpSignature = "net-sig-1";

            Assert.DoesNotContain("HbaPrivateKey", acc.Fields.Keys);
            Assert.Equal(pkcs8, acc.HbaPrivateKey);
            Assert.Equal(string.Empty, acc.GetField("HbaPrivateKey"));

            acc.SetField("HbaPrivateKey", "tampered");
            Assert.Equal(pkcs8, acc.HbaPrivateKey);

            acc.ClearSensitiveMemory();
            Assert.Equal(string.Empty, acc.HbaPrivateKey);
            Assert.Equal(string.Empty, acc.HbaPublicKey);
            Assert.Equal(string.Empty, acc.HbaTemplate);
            Assert.Equal(string.Empty, acc.LastBoundIpSignature);
        }

        [Fact]
        public async Task HighB2_GetAuthTicketAsync_EmptyLastBoundIpSignature_DoesNotStripHbaOn401()
        {
            string pkcs8 = CreateValidTestPkcs8();
            var acc = new Account
            {
                Username = "HbaEmptySigUser",
                UserID = 2002,
                SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.--" + new string('A', 920),
                HbaPrivateKey = pkcs8,
                LastBoundIpSignature = string.Empty
            };

            var observedHasBat = new List<bool>();
            Account.TestHttpClientFactory = _ => new HttpClient(new FakeHttpHandler(req =>
            {
                string url = req.RequestUri?.ToString() ?? "";
                if (url.Contains("v1/authentication-ticket", StringComparison.OrdinalIgnoreCase))
                {
                    observedHasBat.Add(req.Headers.Contains("x-bound-auth-token"));
                    return new HttpResponseMessage(HttpStatusCode.Unauthorized);
                }
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
            }));

            var (success, _) = await acc.GetAuthTicketCoreAsync(allowIpSyncRetry: true);
            Assert.False(success);
            Assert.NotEmpty(observedHasBat);
            Assert.All(observedHasBat, hasBat => Assert.True(hasBat));
        }

        [Fact]
        public async Task HighB3_RestClient_And_HbaSigning_UseMatchingBodyPayloadOrder()
        {
            string pkcs8 = CreateValidTestPkcs8();
            var acc = new Account { Username = "HbaBodyOrderUser", UserID = 3003, HbaPrivateKey = pkcs8 };
            var req = new RestRequest("https://apis.roblox.com/test", Method.Post);
            req.AddParameter("a", "1");
            req.AddStringBody("{\"raw\":true}", "application/json");

            acc.ApplySessionFingerprintAndHbaToRestRequest(req, "https://apis.roblox.com/");
            Assert.True(req.Headers.TryGetValue("x-bound-auth-token", out string? signedHeader));

            string? sentBody = null;
            using var http = new HttpClient(new FakeHttpHandler(async (httpReq, _) =>
            {
                sentBody = httpReq.Content != null ? await httpReq.Content.ReadAsStringAsync() : null;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
            }));

            var rc = new RestClient(http, "https://apis.roblox.com/");
            await rc.ExecuteAsync(req);

            Assert.NotNull(sentBody);
            string[] batParts = signedHeader!.Split('|');
            Assert.True(batParts.Length >= 3);
            string signedHash = batParts[1];
            string expectedWireHash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(sentBody!)));
            Assert.Equal(expectedWireHash, signedHash);
        }

        [Fact]
        public async Task HighB4_ValidateSessionAsync_RethrowsOperationCanceledException()
        {
            var acc = new Account { Username = "CancelValidateUser", Valid = true, SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.--tok" };
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAsync<OperationCanceledException>(() => acc.ValidateSessionAsync(cts.Token));
        }

        [Fact]
        public void HighB5_FavoriteGame_DefaultConstructor_DoesNotHaveImageBug_And_ServersInfoCursorsAreNull()
        {
            var info = new ServersInfo();
            Assert.Null(info.previousPageCursor);
            Assert.Null(info.nextPageCursor);

            var fav = new FavoriteGame();
            Assert.Null(fav.Details);
            Assert.Equal(0, fav.PlaceID);
        }

        [Fact]
        public async Task HighB6_RefreshStaleCookies_DetectsRecoveredAccount_And_BulkParserSupportsPipeWithColonPassword()
        {
            var acc = new Account
            {
                Username = "RecoveredUser",
                UserID = 6006,
                Valid = false,
                SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.--" + new string('B', 920),
                LastAttemptedRefresh = DateTime.MinValue
            };

            Account.TestHttpClientFactory = _ => new HttpClient(new FakeHttpHandler(req =>
            {
                string url = req.RequestUri?.ToString() ?? "";
                if (url.Contains("users/authenticated", StringComparison.OrdinalIgnoreCase))
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"id\":6006,\"name\":\"RecoveredUser\"}") };
                if (url.Contains("accountsettings.roblox.com", StringComparison.OrdinalIgnoreCase))
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
            }));

            string tempSavePath = Path.Combine(Path.GetTempPath(), "ShiroRefresh_" + Guid.NewGuid().ToString("N") + ".json");
            string tempBakDir = Path.Combine(Path.GetTempPath(), "ShiroRefreshBak_" + Guid.NewGuid().ToString("N"));
            var prevStore = AccountPersistenceService.Default.Store;
            try
            {
                AccountManager.ResetAccountsForTests(new[] { acc });
                AccountPersistenceService.Default.Store = new AccountStore(tempSavePath, tempBakDir);
                AccountPersistenceService.Default.StartTime = DateTime.Now.AddMinutes(-5);

                using var refreshGate = new SemaphoreSlim(1, 1);
                await AccountSessionMaintenanceService.Default.RefreshStaleCookiesAsync(refreshGate, CancellationToken.None);
                Assert.True(acc.Valid);
                Assert.True(File.Exists(tempSavePath), "SaveAccounts should run when expired account recovers to Valid.");

                var (u, p) = AccountSessionMaintenanceService.ParseUserPassCombo("alice|sec:ret:123");
                Assert.Equal("alice", u);
                Assert.Equal("sec:ret:123", p);
            }
            finally
            {
                AccountPersistenceService.Default.Store = prevStore;
                try { if (File.Exists(tempSavePath)) File.Delete(tempSavePath); } catch (IOException) { }
                try { if (Directory.Exists(tempBakDir)) Directory.Delete(tempBakDir, true); } catch (IOException) { }
            }
        }

        [Fact]
        public async Task HighB7_FastLogin_UsernameMismatch_DoesNotMutateOrOverwriteAccountPassword()
        {
            var capturedAcc = new Account
            {
                Username = "ActualUserB",
                UserID = 7007,
                Valid = true,
                Password = "OriginalPasswordB"
            };

            var res = await FastLoginCoordinator.ProcessCapturedSessionAsync(
                capturedAcc,
                "_|WARNING:-DO-NOT-SHARE-THIS.--tok",
                expectedUsername: "ExpectedUserA",
                password: "NewPasswordForA!",
                alias: "AliasA",
                group: "GroupA",
                queueCount: 1,
                currentQueueAccount: capturedAcc);

            Assert.Equal(FastLoginCaptureStatus.UsernameMismatch, res.Status);
            Assert.Equal("OriginalPasswordB", capturedAcc.Password);
        }

        [Fact]
        public void HighB8_CookieLengthRule_RejectsUnder500CharPlaceholderOverValidCookie()
        {
            string longCookie = "_|WARNING:-DO-NOT-SHARE-THIS.--|_HDR." + new string('X', 910) + ".SIG";
            string shortFallback = "_|WARNING:-DO-NOT-SHARE-THIS.--|_HDR." + new string('Y', 600) + ".SIG";
            string shortPlaceholder = "_|WARNING:-DO-NOT-SHARE-THIS.--abc";

            Assert.False(FastLoginScripts.ShouldReplaceSettledCookie(longCookie, shortPlaceholder));
            Assert.False(FastLoginScripts.ShouldReplaceSettledCookie(longCookie, shortFallback));
            Assert.True(FastLoginScripts.ShouldReplaceSettledCookie(shortPlaceholder, longCookie));
            Assert.True(FastLoginScripts.ShouldReplaceSettledCookie(shortFallback, longCookie));
        }

        [Fact]
        public async Task HighB9_SessionAuditor_Http200HtmlOrNonJson_DoesNotMarkValid()
        {
            var acc = new Account
            {
                Username = "Html200User",
                UserID = 9009,
                Valid = false,
                AccountStatusIssue = "MOD_LOCKED",
                SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.--tok"
            };

            SessionAuditorService.TestHttpClientFactory = _ => new HttpClient(new FakeHttpHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("<html><body>Roblox Login Page</body></html>", Encoding.UTF8, "text/html")
                }));
            try
            {
                var audit = await SessionAuditorService.AuditAccountAsync(acc);
                Assert.False(acc.Valid);
                Assert.NotEqual(AccountSessionStatus.Valid, audit.Status);
                Assert.Equal("MOD_LOCKED", acc.AccountStatusIssue);
            }
            finally
            {
                SessionAuditorService.TestHttpClientFactory = null;
            }
        }

        private sealed class ThrowingSyncContent : HttpContent
        {
            protected override void SerializeToStream(Stream stream, TransportContext? context, CancellationToken cancellationToken)
                => throw new InvalidOperationException("Sync SerializeToStream / CopyTo must not be called from async HBA path!");

            protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
                => stream.WriteAsync("{\"ok\":true}"u8.ToArray(), 0, 11);

            protected override bool TryComputeLength(out long length) { length = 11; return true; }
        }

        [Fact]
        public async Task HighB10_TryAttachHbaHeaderAsync_NeverCallsSyncCopyToFallback()
        {
            var acc = new Account { Username = "HbaAsyncUser", UserID = 1010, HbaPrivateKey = CreateValidTestPkcs8() };
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://auth.roblox.com/v1/test")
            {
                Content = new ThrowingSyncContent()
            };

            bool ok = await RobloxHbaService.TryAttachHbaHeaderAsync(req, acc);
            Assert.True(ok);
            Assert.True(req.Headers.Contains("x-bound-auth-token"));
        }

        [Fact]
        public void HighB11_MergeCapturePayloadIntoSnapshot_HandlesNonStringFields_AndRejectsInvalidPrivateKey()
        {
            var snap = new RobloxHbaService.CaptureSnapshot();
            using var doc = JsonDocument.Parse("{\"privateKeyPkcs8\":12345,\"publicKeySpki\":[\"x\"],\"tokenTemplate\":true,\"userAgent\":{}}");
            RobloxHbaService.MergeCapturePayloadIntoSnapshot(snap, doc.RootElement);
            Assert.False(snap.HasPrivateKey);

            using var docBadKey = JsonDocument.Parse("{\"privateKeyPkcs8\":\"not_valid_base64_key\",\"publicKeySpki\":\"bad_spki\"}");
            RobloxHbaService.MergeCapturePayloadIntoSnapshot(snap, docBadKey.RootElement);
            Assert.False(snap.HasPrivateKey);
        }

        private const string HbaBodyJson = "{\"k\":1}";

        private static string BuildHbaRotateJson(string privKey) =>
            JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["privateKeyPkcs8"] = privKey,
                ["saiBound"] = true
            });

        private static Account MakeAccount(string username, string password) =>
            new Account { Username = username, Password = password };

        private sealed class UnreadableTestStream : Stream
        {
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new IOException("Unreadable");
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        [Fact]
        public void Task3_HbaService_RewindsSeekableStream_FailsClosedOnUnreadable_AndClearsStaleSpkiOnKeyRotation()
        {
            string oldPrivKey = CreateValidTestPkcs8();
            string newPrivKey = CreateValidTestPkcs8();
            using var oldEc = ECDsa.Create();
            oldEc.ImportPkcs8PrivateKey(Convert.FromBase64String(oldPrivKey), out _);
            string oldPubKey = Convert.ToBase64String(oldEc.ExportSubjectPublicKeyInfo());

            var acc = MakeAccount("HbaT3User", string.Empty);
            acc.ApplyHbaCapture(new RobloxHbaService.CaptureSnapshot
            {
                PrivateKeyPkcs8Base64 = oldPrivKey,
                PublicKeySpkiBase64 = oldPubKey
            });
            Assert.Equal(oldPubKey, acc.HbaPublicKey);

            using var ms = new MemoryStream(Encoding.UTF8.GetBytes(HbaBodyJson));
            ms.Position = ms.Length;
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://apis.roblox.com/v1/test") { Content = new StreamContent(ms) };
            Assert.True(RobloxHbaService.TryAttachHbaHeader(req, acc));
            Assert.Equal(0, ms.Position);
            string batHeader = req.Headers.GetValues(RobloxHbaService.BoundAuthHeaderName).Single();
            string expectedHash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(HbaBodyJson)));
            Assert.Equal(expectedHash, batHeader.Split('|')[1]);

            using var badReq = new HttpRequestMessage(HttpMethod.Post, "https://apis.roblox.com/v1/test") { Content = new StreamContent(new UnreadableTestStream()) };
            Assert.False(RobloxHbaService.TryAttachHbaHeader(badReq, acc));
            Assert.False(badReq.Headers.Contains(RobloxHbaService.BoundAuthHeaderName));

            var snap = new RobloxHbaService.CaptureSnapshot
            {
                PrivateKeyPkcs8Base64 = oldPrivKey,
                PublicKeySpkiBase64 = oldPubKey,
                IsAuthoritativeKey = true
            };
            using (var doc = JsonDocument.Parse(BuildHbaRotateJson(newPrivKey)))
            {
                RobloxHbaService.MergeCapturePayloadIntoSnapshot(snap, doc.RootElement);
            }
            Assert.Equal(newPrivKey, snap.PrivateKeyPkcs8Base64);
            Assert.True(string.IsNullOrEmpty(snap.PublicKeySpkiBase64));

            // Mismatched or empty SPKI on rotation must clear stale HbaPublicKey
            acc.ApplyHbaCapture(new RobloxHbaService.CaptureSnapshot
            {
                PrivateKeyPkcs8Base64 = newPrivKey,
                PublicKeySpkiBase64 = oldPubKey
            });
            Assert.Equal(newPrivKey, acc.HbaPrivateKey);
            Assert.Equal(string.Empty, acc.HbaPublicKey);

            // Direct key rotation via property setter also clears stale SPKI
            acc.ApplyHbaCapture(new RobloxHbaService.CaptureSnapshot
            {
                PrivateKeyPkcs8Base64 = oldPrivKey,
                PublicKeySpkiBase64 = oldPubKey
            });
            Assert.Equal(oldPubKey, acc.HbaPublicKey);
            acc.HbaPrivateKey = newPrivKey;
            Assert.Equal(string.Empty, acc.HbaPublicKey);
        }

        [Fact]
        public async Task Task3_BrowserService_SignInGate_SerializesConcurrentSignIns()
        {
            var gateField = typeof(BrowserService).GetField("SignInGate", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(gateField);
            var gate = Assert.IsType<SemaphoreSlim>(gateField!.GetValue(null));
            Assert.True(gate.CurrentCount <= 1, "Expected SignInGate CurrentCount <= 1, got " + gate.CurrentCount);

            var prevOverride = BrowserService.TestSignInOverride;
            int active = 0, maxActive = 0;
            var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                BrowserService.TestSignInOverride = async (user, _, _, _, _) =>
                {
                    int cur = Interlocked.Increment(ref active);
                    if (cur > maxActive) maxActive = cur;
                    if (user == "user1") { firstEntered.TrySetResult(); await releaseFirst.Task; }
                    Interlocked.Decrement(ref active);
                    return MakeAccount(user ?? string.Empty, string.Empty);
                };
                var t1 = BrowserService.Default.SignInAsync("user1", "p1", 0);
                await firstEntered.Task;
                var t2 = BrowserService.Default.SignInAsync("user2", "p2", 1);
                await Task.Delay(40);
                Assert.Equal(1, Volatile.Read(ref active));
                releaseFirst.TrySetResult();
                await Task.WhenAll(t1, t2);
                Assert.Equal(1, maxActive);
            }
            finally
            {
                releaseFirst.TrySetResult();
                BrowserService.TestSignInOverride = prevOverride;
            }
        }
    }
}

#nullable enable
using System;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Http;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public class AccountHbaAndAutoReconnectExtendedTests : IDisposable
    {
        private readonly bool _prevUseProxies;
        private readonly bool _prevEnableAutoReconnect;

        public AccountHbaAndAutoReconnectExtendedTests()
        {
            _prevUseProxies = AppSettings.General.Get<bool>("UseProxies");
            _prevEnableAutoReconnect = AppSettings.General.Get<bool>(AppSettingsKeys.General.AutoReconnect);
        }

        public void Dispose()
        {
            AppSettings.General.Set("UseProxies", _prevUseProxies ? "true" : "false");
            AppSettings.General.Set(AppSettingsKeys.General.AutoReconnect, _prevEnableAutoReconnect ? "true" : "false");
            var service = AutoReconnectService.Instance;
            service.MaxRetries = 5;
            service.InitialBackoff = TimeSpan.FromSeconds(10);
            service.LaunchGracePeriod = TimeSpan.FromSeconds(15);
            service.DisconnectTimeout = TimeSpan.FromSeconds(20);
            service.StableResetWindow = TimeSpan.FromMinutes(10);
            service.IsEnabled = true;
            service.Clear();
        }

        [Fact]
        public void AccountHba_PropertiesAndBindings_OperateCorrectly()
        {
            var account = new Account { Username = "HbaUser" };
            Assert.False(account.HasHbaBinding);
            Assert.Empty(account.HbaPrivateKey);
            Assert.Empty(account.HbaPublicKey);

            using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            string privKey = Convert.ToBase64String(ecdsa.ExportPkcs8PrivateKey());
            string pubKey = Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo());

            account.HbaPrivateKey = privKey;
            account.HbaPublicKey = pubKey;
            account.HbaTemplate = "{\"template\":1}";
            account.SessionUserAgent = "CustomUA/1.0";
            account.LastBoundIpSignature = "127.0.0.1-sig";

            Assert.True(account.HasHbaBinding);
            Assert.Equal(privKey, account.HbaPrivateKey);
            Assert.Equal(privKey, account.HbaPrivateKeyPkcs8);
            Assert.Equal(pubKey, account.HbaPublicKey);
            Assert.Equal(pubKey, account.HbaPublicKeySpki);
            Assert.Equal("{\"template\":1}", account.HbaTemplate);
            Assert.Equal("CustomUA/1.0", account.SessionUserAgent);
            Assert.Equal("127.0.0.1-sig", account.LastBoundIpSignature);
            Assert.Equal("CustomUA/1.0", account.GetEffectiveUserAgent());
        }

        [Fact]
        public void AccountHba_BuildCookieHeaderValue_FormatsCorrectly()
        {
            var account = new Account { Username = "CookieFormatUser" };
            Assert.Empty(account.BuildCookieHeaderValue());

            account.SecurityToken = "SampleSecurityTokenABC";
            Assert.Equal(".ROBLOSECURITY=SampleSecurityTokenABC", account.BuildCookieHeaderValue());

            account.BrowserTrackerID = "123456789";
            account.UserID = 998877;
            string headerVal = account.BuildCookieHeaderValue();
            Assert.Contains(".ROBLOSECURITY=SampleSecurityTokenABC", headerVal);
            Assert.Contains("RBXEventTrackerV2=", headerVal);
            Assert.Contains("browserid=123456789", headerVal);
            Assert.Contains("rbxid=998877", headerVal);
        }

        [Fact]
        public void AccountHba_ApplyHbaCapture_And_CopyHba_PreservesData()
        {
            using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            string privKey = Convert.ToBase64String(ecdsa.ExportPkcs8PrivateKey());
            string pubKey = Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo());

            var snapshot = new RobloxHbaService.CaptureSnapshot
            {
                PrivateKeyPkcs8Base64 = privKey,
                PublicKeySpkiBase64 = pubKey,
                TokenTemplate = "{\"tmpl\":true}",
                UserAgent = "CaptureUA/2.0",
                BrowserTrackerId = "trk-999"
            };

            var source = new Account { Username = "SourceAccount" };
            source.ApplyHbaCapture(null); // null is safe
            source.ApplyHbaCapture(snapshot);

            Assert.Equal(privKey, source.HbaPrivateKey);
            Assert.Equal(pubKey, source.HbaPublicKey);
            Assert.Equal("CaptureUA/2.0", source.SessionUserAgent);
            Assert.Equal("trk-999", source.BrowserTrackerID);

            var dest = new Account { Username = "DestAccount" };
            source.CopyHbaAndFingerprintTo(source); // self-copy safe noop
            source.CopyHbaAndFingerprintTo(null!);  // null-target safe noop
            source.CopyHbaAndFingerprintTo(dest);

            Assert.Equal(source.HbaPrivateKey, dest.HbaPrivateKey);
            Assert.Equal(source.HbaPublicKey, dest.HbaPublicKey);
            Assert.Equal(source.HbaTemplate, dest.HbaTemplate);
            Assert.Equal(source.SessionUserAgent, dest.SessionUserAgent);
            Assert.Equal(source.BrowserTrackerID, dest.BrowserTrackerID);
        }

        [Fact]
        public void AccountHba_ApplySessionFingerprintAndHba_ConfiguresRequest()
        {
            var account = new Account
            {
                Username = "FingerprintUser",
                SecurityToken = "SampleToken",
                SessionUserAgent = "FingerprintUA/1.0"
            };

            using var req = new HttpRequestMessage(HttpMethod.Get, "https://www.roblox.com/home");
            account.ApplySessionFingerprintAndHba(req);

            Assert.True(req.Headers.Contains("Cookie"));
            Assert.True(req.Headers.Contains("User-Agent"));
            Assert.Contains("FingerprintUA/1.0", req.Headers.UserAgent.ToString());
        }

        [Fact]
        public void AccountHba_ApplySessionFingerprintAndHbaToRestRequest_ConfiguresRequest()
        {
            var account = new Account
            {
                Username = "RestFingerprintUser",
                BrowserTrackerID = "trk-888",
                UserID = 12345
            };

            var restReq = new RestRequest("my/settings", Method.Get);
            restReq.AddHeader("Cookie", ".ROBLOSECURITY=SampleTok");

            account.ApplySessionFingerprintAndHbaToRestRequest(restReq, "https://www.roblox.com/");

            Assert.True(restReq.Headers.ContainsKey("User-Agent"));
            Assert.True(restReq.Headers.ContainsKey("Cookie"));
            Assert.Contains("RBXEventTrackerV2=", restReq.Headers["Cookie"]);
        }

        [Fact]
        public void AutoReconnectService_PropertiesAndSettingsSync()
        {
            var service = AutoReconnectService.Instance;
            service.IsEnabled = true;
            Assert.Equal(service.IsEnabled, AppSettings.General.Get<bool>(AppSettingsKeys.General.AutoReconnect));

            service.IsEnabled = false;
            Assert.False(service.IsEnabled);
            Assert.False(AppSettings.General.Get<bool>(AppSettingsKeys.General.AutoReconnect));

            service.MaxRetries = 4;
            Assert.Equal(4, service.MaxRetries);

            service.InitialBackoff = TimeSpan.FromSeconds(8);
            Assert.Equal(TimeSpan.FromSeconds(8), service.InitialBackoff);

            service.LaunchGracePeriod = TimeSpan.FromSeconds(12);
            Assert.Equal(TimeSpan.FromSeconds(12), service.LaunchGracePeriod);

            service.DisconnectTimeout = TimeSpan.FromSeconds(25);
            Assert.Equal(TimeSpan.FromSeconds(25), service.DisconnectTimeout);

            service.StableResetWindow = TimeSpan.FromMinutes(8);
            Assert.Equal(TimeSpan.FromMinutes(8), service.StableResetWindow);
        }

        [Fact]
        public void AutoReconnectService_SessionRegistration_TracksAndPreservesRetries()
        {
            var service = AutoReconnectService.Instance;
            service.Clear();

            var account = new Account { Username = "ReconnectTrackUser", UserID = 777 };
            DateTime baseTime = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

            service.RegisterSession(account, 123456, "job-1", false, false, 9999, baseTime);

            Assert.True(service.TryGetSession("ReconnectTrackUser", out var session));
            Assert.NotNull(session);
            Assert.Equal(123456L, session.PlaceId);
            Assert.Equal("job-1", session.JobId);
            Assert.Equal(9999, session.ProcessId);
            Assert.Equal(0, session.RetryCount);

            // Simulate 2 retries
            session.RetryCount = 2;

            // Re-register within StableResetWindow (5 minutes later) -> preserves RetryCount = 2
            DateTime withinWindow = baseTime.AddMinutes(5);
            service.RegisterSession(account, 123456, "job-1", false, false, 9999, withinWindow);
            service.TryGetSession("ReconnectTrackUser", out var sessionPreserved);
            Assert.NotNull(sessionPreserved);
            Assert.Equal(2, sessionPreserved.RetryCount);

            // Re-register after StableResetWindow (15 minutes later) -> resets RetryCount = 0
            DateTime afterWindow = baseTime.AddMinutes(15);
            service.RegisterSession(account, 123456, "job-1", false, false, 9999, afterWindow);
            service.TryGetSession("ReconnectTrackUser", out var sessionReset);
            Assert.NotNull(sessionReset);
            Assert.Equal(0, sessionReset.RetryCount);
        }

        [Fact]
        public void AutoReconnectService_NotifyDisconnectAndConnect_UpdatesState()
        {
            var service = AutoReconnectService.Instance;
            service.Clear();

            var account = new Account { Username = "DisconnectNotifyUser", UserID = 888 };
            service.RegisterSession(account, 654321, "job-2", false, false, 5555);

            service.NotifyDisconnected(5555, 277);

            Assert.True(service.TryGetSession("DisconnectNotifyUser", out var session));
            Assert.NotNull(session.LastDisconnectUtc);
            Assert.Equal(277, session.LastDisconnectReason);

            service.NotifyConnected(5555);
            Assert.Null(session.LastDisconnectUtc);
            Assert.Equal(0, session.LastDisconnectReason);
        }

        [Fact]
        public void AutoReconnectService_SuppressAndUnregister_ModifiesTracking()
        {
            var service = AutoReconnectService.Instance;
            service.Clear();

            var acc1 = new Account { Username = "SuppressedUser1" };
            var acc2 = new Account { Username = "SuppressedUser2" };
            service.RegisterSession(acc1, 1, processId: 101);
            service.RegisterSession(acc2, 2, processId: 102);

            service.SuppressReconnect("SuppressedUser1");
            service.TryGetSession("SuppressedUser1", out var s1);
            Assert.True(s1?.IsSuppressed);
            Assert.False(s1?.IsActive);

            service.SuppressAll();
            service.TryGetSession("SuppressedUser2", out var s2);
            Assert.True(s2?.IsSuppressed);

            service.UnregisterSession("SuppressedUser1");
            Assert.False(service.TryGetSession("SuppressedUser1", out _));

            service.Clear();
            Assert.Equal(0, service.ActiveSessionsCount);
        }

        [Fact]
        public void AutoReconnectService_NotifyMaxRetriesExceededForTesting_FiresEvent()
        {
            var service = AutoReconnectService.Instance;
            service.Clear();

            var acc = new Account { Username = "MaxRetryUser" };
            service.RegisterSession(acc, 100, processId: 202);

            bool eventFired = false;
            void Handler(AutoReconnectService.ReconnectSession s) { if (s.Account?.Username == "MaxRetryUser") eventFired = true; }

            service.MaxRetriesExceeded += Handler;
            try
            {
                service.NotifyMaxRetriesExceededForTesting("MaxRetryUser");
                Assert.True(eventFired);
            }
            finally
            {
                service.MaxRetriesExceeded -= Handler;
            }
        }
    }
}

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
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using Shiro.AvaloniaUI.Controls;
using Shiro.Http;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public class Phase2WebView2AndBatchProxyTests : IDisposable
    {
        private readonly Account? _previousValidAccount;
        private readonly bool _hadDisableImages;
        private readonly string? _previousDisableImages;
        private readonly bool _hadUseProxies;
        private readonly string? _previousUseProxies;
        private readonly Func<Account, HttpClient>? _previousTestHttpClientFactory;

        public Phase2WebView2AndBatchProxyTests()
        {
            Batch.ResetPendingWorkersForTest();
            _previousValidAccount = AppSettings.LastValidAccount;
            _hadDisableImages = AppSettings.General.Exists("DisableImages");
            _previousDisableImages = AppSettings.General.Get("DisableImages");
            _hadUseProxies = AppSettings.General.Exists(AppSettingsKeys.General.UseProxies);
            _previousUseProxies = AppSettings.General.Get(AppSettingsKeys.General.UseProxies);
            _previousTestHttpClientFactory = RobloxAccountSessionClient.TestHttpClientFactory;
        }

        public void Dispose()
        {
            Batch.ResetPendingWorkersForTest();
            AppSettings.LastValidAccount = _previousValidAccount;
            if (_hadDisableImages) AppSettings.General.Set("DisableImages", _previousDisableImages);
            else AppSettings.General.RemoveProperty("DisableImages");

            if (_hadUseProxies) AppSettings.General.Set(AppSettingsKeys.General.UseProxies, _previousUseProxies);
            else AppSettings.General.RemoveProperty(AppSettingsKeys.General.UseProxies);

            RobloxAccountSessionClient.TestHttpClientFactory = _previousTestHttpClientFactory;
        }

        [Fact]
        public void WebView2Control_TryParseProxyForWebView_StripsCredentialsAndFlagsSocksAuth()
        {
            Assert.True(WebView2Control.TryParseProxyForWebView(
                "http://alice:s3cr3t@10.20.30.40:8080",
                out string? httpArg,
                out string? httpUser,
                out string? httpPass,
                out bool httpSocksAuth));
            Assert.Equal("http://10.20.30.40:8080", httpArg);
            Assert.Equal("alice", httpUser);
            Assert.Equal("s3cr3t", httpPass);
            Assert.False(httpSocksAuth);

            Assert.True(WebView2Control.TryParseProxyForWebView(
                "10.20.30.40:8080:bob:p@ss",
                out string? colonArg,
                out string? colonUser,
                out string? colonPass,
                out bool colonSocksAuth));
            Assert.Equal("http://10.20.30.40:8080", colonArg);
            Assert.Equal("bob", colonUser);
            Assert.Equal("p@ss", colonPass);
            Assert.False(colonSocksAuth);

            Assert.True(WebView2Control.TryParseProxyForWebView(
                "socks5://user:pw@10.20.30.40:1080",
                out string? socksArg,
                out string? socksUser,
                out string? socksPass,
                out bool socksAuth));
            Assert.Equal("socks5://10.20.30.40:1080", socksArg);
            Assert.Equal("user", socksUser);
            Assert.Equal("pw", socksPass);
            Assert.True(socksAuth);

            Assert.False(WebView2Control.TryParseProxyForWebView(
                null,
                out string? nullArg,
                out string? nullUser,
                out string? nullPass,
                out bool nullSocks));
            Assert.Null(nullArg);
            Assert.Null(nullUser);
            Assert.Null(nullPass);
            Assert.False(nullSocks);

            Assert.False(WebView2Control.TryParseProxyForWebView(
                string.Empty,
                out string? emptyArg,
                out string? emptyUser,
                out string? emptyPass,
                out bool emptySocks));
            Assert.Null(emptyArg);
            Assert.Null(emptyUser);
            Assert.Null(emptyPass);
            Assert.False(emptySocks);
        }

        [Fact]
        public void WebView2Control_BuildAdditionalBrowserArguments_AndDataFolder_IsolatePerProxyWithoutCredentialLeak()
        {
            string directArgs = WebView2Control.BuildAdditionalBrowserArguments(null);
            Assert.DoesNotContain("--proxy-server", directArgs, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("--disable-features=", directArgs, StringComparison.Ordinal);

            string proxyArgs = WebView2Control.BuildAdditionalBrowserArguments("http://10.20.30.40:8080");
            Assert.Contains("--proxy-server=http://10.20.30.40:8080", proxyArgs, StringComparison.Ordinal);
            Assert.DoesNotContain("@", proxyArgs, StringComparison.Ordinal);

            string strippedCredsArgs = WebView2Control.BuildAdditionalBrowserArguments("http://alice:s3cr3t@10.20.30.40:8080");
            Assert.Contains("--proxy-server=http://10.20.30.40:8080", strippedCredsArgs, StringComparison.Ordinal);
            Assert.DoesNotContain("alice", strippedCredsArgs, StringComparison.Ordinal);
            Assert.DoesNotContain("s3cr3t", strippedCredsArgs, StringComparison.Ordinal);

            string injectedArgs = WebView2Control.BuildAdditionalBrowserArguments("http://10.20.30.40:8080 --no-sandbox");
            Assert.DoesNotContain("--no-sandbox", injectedArgs, StringComparison.Ordinal);
            Assert.DoesNotContain("--proxy-server", injectedArgs, StringComparison.Ordinal);

            string colonCredsArgs = WebView2Control.BuildAdditionalBrowserArguments("10.20.30.40:8080:bob:pass");
            Assert.Contains("--proxy-server=http://10.20.30.40:8080", colonCredsArgs, StringComparison.Ordinal);
            Assert.DoesNotContain("bob", colonCredsArgs, StringComparison.Ordinal);
            Assert.DoesNotContain("pass", colonCredsArgs, StringComparison.Ordinal);

            string invalidTokenArgs = WebView2Control.BuildAdditionalBrowserArguments("invalid-proxy-token");
            Assert.DoesNotContain("--proxy-server", invalidTokenArgs, StringComparison.Ordinal);

            string tempDir = Path.Combine(Path.GetTempPath(), $"Shiro-Phase2-Wv2-{Guid.NewGuid():N}");
            try
            {
                using var dirScope = StoragePaths.OverrideDataDirectoryForTesting(tempDir);

                string directFolder = WebView2Control.ResolveEnvironmentDataFolder(null);
                Assert.Equal(
                    "EmbeddedBrowser",
                    Path.GetFileName(directFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));

                string folder1a = WebView2Control.ResolveEnvironmentDataFolder("http://10.20.30.40:8080");
                string folder1b = WebView2Control.ResolveEnvironmentDataFolder("http://10.20.30.40:8080");
                string folder2 = WebView2Control.ResolveEnvironmentDataFolder("http://10.20.30.41:8080");

                string sessionsRoot = Path.GetFullPath(StoragePaths.BrowserSessionsDirectory);
                Assert.StartsWith(sessionsRoot, Path.GetFullPath(folder1a), StringComparison.OrdinalIgnoreCase);
                Assert.StartsWith("EmbeddedBrowser_Proxy_", Path.GetFileName(folder1a), StringComparison.Ordinal);
                Assert.Equal(folder1a, folder1b);
                Assert.NotEqual(folder1a, folder2);
                Assert.DoesNotContain("10.20.30.40", folder1a, StringComparison.Ordinal);

                // CleanupOrphanedProfiles respects StoragePaths.IsDirectoryStale on nested files
                string ebRoot = Path.Combine(folder1a, "EBWebView");
                string activeOldParentProfile = Path.Combine(ebRoot, "Shiro_" + Guid.NewGuid().ToString("N"));
                string staleProfile = Path.Combine(ebRoot, "Shiro_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(activeOldParentProfile);
                Directory.CreateDirectory(staleProfile);
                string nestedFile = Path.Combine(activeOldParentProfile, "Cookies");
                File.WriteAllText(nestedFile, "active");
                Directory.SetLastWriteTimeUtc(activeOldParentProfile, DateTime.UtcNow.AddHours(-2));
                Directory.SetLastWriteTimeUtc(staleProfile, DateTime.UtcNow.AddHours(-2));

                WebView2Control.CleanupOrphanedProfiles(folder1a);

                Assert.True(Directory.Exists(activeOldParentProfile));
                Assert.False(Directory.Exists(staleProfile));
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, true); }
                    catch (IOException ex) { AppServices.Logger.Debug($"WebView2 proxy test cleanup: {ex.Message}"); }
                }
            }
        }

        [Fact]
        public void WebView2Control_ConfigureProxy_ProtectsCredentialsInMemory()
        {
            var control = new WebView2Control();
            control.ConfigureProxy("http://proxyuser:VerySecretPass99@10.20.30.40:8080");

            FieldInfo[] stringFields = typeof(WebView2Control)
                .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(f => f.FieldType == typeof(string))
                .ToArray();

            foreach (FieldInfo field in stringFields)
            {
                string? value = field.GetValue(control) as string;
                if (value == null) continue;
                Assert.DoesNotContain("VerySecretPass99", value, StringComparison.Ordinal);
                Assert.DoesNotContain("proxyuser", value, StringComparison.Ordinal);
            }

            FieldInfo? proxyServerField = typeof(WebView2Control).GetField("_proxyServerArg", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(proxyServerField);
            Assert.Equal("http://10.20.30.40:8080", proxyServerField!.GetValue(control) as string);

            FieldInfo? protectedUserField = typeof(WebView2Control).GetField("_protectedProxyUser", BindingFlags.Instance | BindingFlags.NonPublic);
            FieldInfo? protectedPassField = typeof(WebView2Control).GetField("_protectedProxyPass", BindingFlags.Instance | BindingFlags.NonPublic);
            byte[]? protectedUser = protectedUserField?.GetValue(control) as byte[];
            byte[]? protectedPass = protectedPassField?.GetValue(control) as byte[];
            Assert.NotNull(protectedUser);
            Assert.NotNull(protectedPass);
            Assert.Equal("proxyuser", InMemorySecretProtector.Unprotect(protectedUser));
            Assert.Equal("VerySecretPass99", InMemorySecretProtector.Unprotect(protectedPass));

            control.ConfigureProxy(null);
            Assert.Null(proxyServerField.GetValue(control));
            Assert.Null(protectedUserField?.GetValue(control));
            Assert.Null(protectedPassField?.GetValue(control));
        }

        [Fact]
        public async Task Batch_GetAssetImage_DoesNotAttachRoblosecurityCookie()
        {
            using var suppressSave = Account.BeginSuppressDeferredSaveScope();
            AppSettings.General.Set("DisableImages", "false");
            AppServices.SelectionState.LastValidAccount = new Account
            {
                SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.--secret-cookie"
            };

            string? capturedUrl = null;
            string? capturedCookieHeader = null;
            var handler = new StubHttpHandler(req =>
            {
                capturedUrl = req.RequestUri?.AbsoluteUri;
                if (req.Headers.TryGetValues("Cookie", out var cookies))
                    capturedCookieHeader = string.Join("; ", cookies);

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"data\":[{\"targetId\":554433,\"errorCode\":0,\"imageUrl\":\"https://fake.rbx/asset-554433.png\"}]}",
                        Encoding.UTF8,
                        "application/json")
                };
            });

            var mockApi = Substitute.For<IRobloxApiClient>();
            using var thumbHttp = new HttpClient(handler);
            using var thumbClient = new RestClient(thumbHttp, "https://thumbnails.roblox.com/");
            mockApi.Thumbnails.Returns(thumbClient);
            using var apiScope = AppServices.Override<IRobloxApiClient>(mockApi);

            string imageUrl = await Batch.GetAssetImage(554433);

            Assert.Equal("https://fake.rbx/asset-554433.png", imageUrl);
            Assert.Contains("v1/assets", capturedUrl ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(".ROBLOSECURITY", capturedCookieHeader ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Batch_ProcessPlaceAsync_AttachesHbaAndUsesAccountProxyHttpClient()
        {
            using var suppressSave = Account.BeginSuppressDeferredSaveScope();
            AppSettings.General.Set("DisableImages", "false");
            AppSettings.General.Set(AppSettingsKeys.General.UseProxies, "true");

            long uniquePlaceId = 910_000_000L + Random.Shared.Next(1, 999_999);
            long uniqueUniverseId = 810_000_000L + Random.Shared.Next(1, 999_999);

            using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var account = new Account
            {
                Username = "BatchProxyHbaUser",
                UserID = 987654321L,
                SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.--batch-proxy-cookie",
                Proxy = "http://proxyuser:proxypass@10.20.30.40:8080",
                HbaPrivateKey = Convert.ToBase64String(ecdsa.ExportPkcs8PrivateKey()),
                HbaPublicKey = Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo()),
                SessionUserAgent = "Mozilla/5.0 BatchProxyTest"
            };
            AppServices.SelectionState.LastValidAccount = account;

            bool factoryCalled = false;
            Account? capturedAccount = null;
            string? capturedPlaceUrl = null;
            string? capturedHbaHeader = null;
            string? capturedCookieHeader = null;
            var capturedThumbUrls = new List<string>();

            var placeHandler = new StubHttpHandler(req =>
            {
                capturedPlaceUrl = req.RequestUri?.AbsoluteUri;
                if (req.Headers.TryGetValues(RobloxHbaService.BoundAuthHeaderName, out var hbaValues))
                    capturedHbaHeader = hbaValues.FirstOrDefault();
                if (req.Headers.TryGetValues("Cookie", out var cookieValues))
                    capturedCookieHeader = string.Join("; ", cookieValues);

                string json = $"[{{\"placeId\":{uniquePlaceId},\"universeId\":{uniqueUniverseId},\"name\":\"Proxy HBA Place\"}}]";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
            });

            RobloxAccountSessionClient.TestHttpClientFactory = acc =>
            {
                factoryCalled = true;
                capturedAccount = acc;
                return new HttpClient(placeHandler, disposeHandler: false);
            };

            var thumbHandler = new StubHttpHandler(req =>
            {
                string url = req.RequestUri?.AbsoluteUri ?? string.Empty;
                capturedThumbUrls.Add(url);
                if (url.Contains("v1/batch", StringComparison.OrdinalIgnoreCase))
                {
                    string batchJson = $"{{\"data\":[{{\"targetId\":{uniqueUniverseId},\"errorCode\":0,\"imageUrl\":\"https://fake.rbx/icon-{uniqueUniverseId}.png\"}}]}}";
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(batchJson, Encoding.UTF8, "application/json")
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });

            var mockApi = Substitute.For<IRobloxApiClient>();
            using var thumbHttp = new HttpClient(thumbHandler);
            using var thumbClient = new RestClient(thumbHttp, "https://thumbnails.roblox.com/");
            mockApi.Thumbnails.Returns(thumbClient);
            using var apiScope = AppServices.Override<IRobloxApiClient>(mockApi);

            string iconUrl = await Batch.GetGameIcon(uniquePlaceId);

            string expectedIconUrl = $"https://fake.rbx/icon-{uniqueUniverseId}.png";
            Assert.True(
                string.Equals(expectedIconUrl, iconUrl, StringComparison.Ordinal),
                $"Expected '{expectedIconUrl}' but got '{iconUrl}'. factoryCalled={factoryCalled}, capturedPlaceUrl={capturedPlaceUrl ?? "<null>"}, hasPlaceDetails={Batch.PlaceDetails.ContainsKey(uniquePlaceId)}, lastValid={AppServices.SelectionState.LastValidAccount?.Username ?? "<null>"}, thumbUrls=[{string.Join(", ", capturedThumbUrls)}]");
            Assert.True(factoryCalled);
            Assert.Same(account, capturedAccount);
            Assert.Contains("multiget-place-details", capturedPlaceUrl ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.False(string.IsNullOrWhiteSpace(capturedHbaHeader));
            Assert.StartsWith("v1|", capturedHbaHeader!, StringComparison.Ordinal);
            Assert.Contains(".ROBLOSECURITY=", capturedCookieHeader ?? string.Empty, StringComparison.Ordinal);
        }

        private sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult(handler(request));
        }
    }
}

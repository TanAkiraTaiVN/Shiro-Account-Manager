#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using Shiro.Classes;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public class ChromeLaunchHelperBehaviorTests
    {
        [Fact]
        public void StandardArgs_ContainsAutomationControlledFlag()
        {
            Assert.Contains("--disable-blink-features=AutomationControlled", ChromeLaunchHelper.StandardArgs);
        }

        [Fact]
        public void BuildLaunchArgs_Defaults_ContainsStandardArgsOnly()
        {
            List<string> args = ChromeLaunchHelper.BuildLaunchArgs(userDataDir: "");
            Assert.DoesNotContain(args, a => a.StartsWith("--user-data-dir="));
            Assert.DoesNotContain(args, a => a.StartsWith("--proxy-server="));
            Assert.DoesNotContain(args, a => a == "--headless=new");
            Assert.Contains("--no-default-browser-check", args);
        }

        [Fact]
        public void BuildLaunchArgs_WithCustomOptions_IncludesFlags()
        {
            string uDir = @"C:\Shiro\Profile1";
            string proxy = "http://127.0.0.1:8888";

            List<string> args = ChromeLaunchHelper.BuildLaunchArgs(uDir, proxy, headless: true);

            Assert.Contains($"--user-data-dir={uDir}", args);
            Assert.Contains($"--proxy-server={proxy}", args);
            Assert.Contains("--headless=new", args);
        }

        [Fact]
        public void GetFreeLoopbackPort_ReturnsValidPortNumber()
        {
            int port = ChromeLaunchHelper.GetFreeLoopbackPort();
            Assert.True(port > 1024);
            Assert.True(port <= 65535);
        }

        [Theory]
        [InlineData(null, "\"\"")]
        [InlineData("", "\"\"")]
        [InlineData("simple", "\"simple\"")]
        [InlineData("with space", "\"with space\"")]
        [InlineData("with\"quote", "\"with\\\"quote\"")]
        public void QuoteArgument_WrapsInQuotesAndEscapesExisting(string? input, string expected)
        {
            string quoted = ChromeLaunchHelper.QuoteArgument(input!);
            Assert.Equal(expected, quoted);
        }

        [Fact]
        public void GetInteractiveBrowserPosition_SingleSlot_ReturnsCoordinates()
        {
            string pos = ChromeLaunchHelper.GetInteractiveBrowserPosition(800, 600, windowSlot: 0, allowSideBySideSlots: false);
            Assert.Contains(",", pos);
            string[] coords = pos.Split(',');
            Assert.True(int.TryParse(coords[0], out int left));
            Assert.True(int.TryParse(coords[1], out int top));
            Assert.True(left >= 0);
            Assert.True(top >= 0);
        }

        [Fact]
        public void GetInteractiveBrowserPosition_SideBySideSlots_ReturnsSlottedCoordinates()
        {
            string posSlot0 = ChromeLaunchHelper.GetInteractiveBrowserPosition(400, 500, windowSlot: 0, allowSideBySideSlots: true);
            string posSlot1 = ChromeLaunchHelper.GetInteractiveBrowserPosition(400, 500, windowSlot: 1, allowSideBySideSlots: true);

            Assert.Contains(",", posSlot0);
            Assert.Contains(",", posSlot1);
        }

        [Theory]
        [InlineData("Session closed. Most likely the page has been closed.")]
        [InlineData("Protocol error (Target.detachedFromTarget): Target closed.")]
        [InlineData("Target closed.")]
        [InlineData("Execution context was destroyed, most likely because of a navigation.")]
        [InlineData("Navigating frame was detached")]
        [InlineData("frame was detached")]
        [InlineData("Cannot find context with specified id")]
        [InlineData("Response body is unavailable for redirect responses")]
        public void IsDetachedOrTransientBrowserException_RecognizesTransientMessages(string message)
        {
            var ex = new Exception(message);
            Assert.True(ChromeLaunchHelper.IsDetachedOrTransientBrowserException(ex));

            // Test nested inner exception
            var wrapper = new InvalidOperationException("Outer wrapper", ex);
            Assert.True(ChromeLaunchHelper.IsDetachedOrTransientBrowserException(wrapper));
        }

        [Fact]
        public void IsDetachedOrTransientBrowserException_WithOtherException_ReturnsFalse()
        {
            var ex = new ArgumentException("Parameter cannot be null");
            Assert.False(ChromeLaunchHelper.IsDetachedOrTransientBrowserException(ex));
        }

        [Fact]
        public void CloseBrowserProcess_WithNull_DoesNotThrow()
        {
            var ex = Record.Exception(() => ChromeLaunchHelper.CloseBrowserProcess(null!, dispose: true));
            Assert.Null(ex);
        }

        [Fact]
        public async Task TryDeleteDirectoryAsync_RemovesExistingDirectory()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "Shiro-DirDel-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            File.WriteAllText(Path.Combine(tempDir, "file.txt"), "content");

            await ChromeLaunchHelper.TryDeleteDirectoryAsync(tempDir);

            Assert.False(Directory.Exists(tempDir));
        }

        [Fact]
        public async Task TryDeleteDirectoryAsync_WithNonExistentDir_CompletesCleanly()
        {
            string nonExistent = Path.Combine(Path.GetTempPath(), "Shiro-NonExistent-" + Guid.NewGuid().ToString("N"));
            await ChromeLaunchHelper.TryDeleteDirectoryAsync(nonExistent);
            Assert.False(Directory.Exists(nonExistent));
        }

        [Fact]
        public async Task TryDeleteSessionDirectoryAsync_InsideAndOutsideSessionsDirectory_EnforcesBoundary()
        {
            // Inside BrowserSessionsDirectory
            string sessionDir = Path.Combine(StoragePaths.BrowserSessionsDirectory, "Shiro-SessDel-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sessionDir);
            File.WriteAllText(Path.Combine(sessionDir, "session.tmp"), "session");

            await ChromeLaunchHelper.TryDeleteSessionDirectoryAsync(sessionDir, maxAttempts: 2);
            Assert.False(Directory.Exists(sessionDir));

            // Outside BrowserSessionsDirectory: should reject deletion
            string outsideDir = Path.Combine(Path.GetTempPath(), "Shiro-Outside-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(outsideDir);
            try
            {
                await ChromeLaunchHelper.TryDeleteSessionDirectoryAsync(outsideDir, maxAttempts: 2);
                Assert.True(Directory.Exists(outsideDir));
            }
            finally
            {
                if (Directory.Exists(outsideDir))
                {
                    try { Directory.Delete(outsideDir, true); }
                    catch (IOException) { }
                }
            }
        }

        [Fact]
        public void VerifiedBrowserProxy_PropertiesRoundTrip()
        {
            var webProxy = new WebProxy("http://127.0.0.1:8080");
            var item = new VerifiedBrowserProxy
            {
                WebProxy = webProxy,
                BrowserServerArgument = "127.0.0.1:8080",
                RawAuthority = "user:pass@127.0.0.1:8080",
                IsSocks = false,
                AuthenticateInBrowser = true,
                Username = "user",
                Password = "pwd"
            };

            Assert.Same(webProxy, item.WebProxy);
            Assert.Equal("127.0.0.1:8080", item.BrowserServerArgument);
            Assert.Equal("user:pass@127.0.0.1:8080", item.RawAuthority);
            Assert.False(item.IsSocks);
            Assert.True(item.AuthenticateInBrowser);
            Assert.Equal("user", item.Username);
            Assert.Equal("pwd", item.Password);
        }

        [Fact]
        public async Task HandleCdpProxyAuthChallengeAsync_WithNullArgs_ReturnsSafely()
        {
            var ex = await Record.ExceptionAsync(() => ChromeLaunchHelper.HandleCdpProxyAuthChallengeAsync(null!, null!, "user", "pass"));
            Assert.Null(ex);
        }

        [Fact]
        public async Task SelectVerifiedBrowserProxyAsync_WhenUseProxiesDisabled_ReturnsNull()
        {
            AppSettings.General.Set("UseProxies", "false");
            var result = await ChromeLaunchHelper.SelectVerifiedBrowserProxyAsync();
            Assert.Null(result);
        }

        [Theory]
        [InlineData("Session closed", true)]
        [InlineData("Target.detachedFromTarget", true)]
        [InlineData("Target closed", true)]
        [InlineData("Execution context was destroyed", true)]
        [InlineData("most likely because of a navigation", true)]
        [InlineData("Cannot find context", true)]
        [InlineData("Navigating frame was detached", true)]
        [InlineData("frame was detached", true)]
        [InlineData("Response body is unavailable", true)]
        [InlineData("Regular generic exception", false)]
        public void IsDetachedOrTransientBrowserException_DetectsAllTransientMessages(string message, bool expected)
        {
            var ex = new InvalidOperationException(message);
            bool isTransient = ChromeLaunchHelper.IsDetachedOrTransientBrowserException(ex);
            Assert.Equal(expected, isTransient);

            var nested = new Exception("Outer", new Exception(message));
            bool isNestedTransient = ChromeLaunchHelper.IsDetachedOrTransientBrowserException(nested);
            Assert.Equal(expected, isNestedTransient);
        }

        [Fact]
        public async Task BrowserNullGuards_ExecuteSafely()
        {
            var ex1 = await Record.ExceptionAsync(() => ChromeLaunchHelper.ApplyAntiDetectionAsync(null!));
            Assert.Null(ex1);

            var ex2 = await Record.ExceptionAsync(() => ChromeLaunchHelper.DismissCookieBannerAsync(null!));
            Assert.Null(ex2);

            var page = await ChromeLaunchHelper.FindActiveRobloxPageAsync(null!);
            Assert.Null(page);

            var cookie = await ChromeLaunchHelper.ExtractRoblosecurityCookieAsync(null!);
            Assert.Null(cookie);

            var json = await ChromeLaunchHelper.ReadAuthenticatedRobloxAccountJsonAsync(null!);
            Assert.Null(json);

            var ex3 = Record.Exception(() => ChromeLaunchHelper.CloseBrowserProcess(null!));
            Assert.Null(ex3);

            var ex4 = await Record.ExceptionAsync(() => ChromeLaunchHelper.TryDeleteDirectoryAsync(null!));
            Assert.Null(ex4);

            var ex5 = await Record.ExceptionAsync(() => ChromeLaunchHelper.TryDeleteDirectoryAsync(""));
            Assert.Null(ex5);

            bool setNull = await ChromeLaunchHelper.SetReactControlledInputValueAsync(null!, "#input", "val");
            Assert.False(setNull);
        }

        [Fact]
        public async Task WaitForDevToolsPortAsync_CanceledToken_ThrowsOperationCanceledException()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                ChromeLaunchHelper.WaitForDevToolsPortAsync(12345, timeoutSeconds: 5, pollMilliseconds: 50, cancellationToken: cts.Token));
        }

        [Fact]
        public async Task HandleCdpProxyAuthChallengeAsync_NullGuards_DoNotThrow()
        {
            var ex1 = await Record.ExceptionAsync(() =>
                ChromeLaunchHelper.HandleCdpProxyAuthChallengeAsync(null!, null!, "user", "pass"));
            Assert.Null(ex1);
        }

        [Fact]
        public async Task SelectVerifiedBrowserProxyAsync_DisabledOrMissingFile_ReturnsNull()
        {
            var origUseProxies = AppSettings.General.Get<bool>("UseProxies");
            try
            {
                AppSettings.General.Set("UseProxies", "false");
                var resultDisabled = await ChromeLaunchHelper.SelectVerifiedBrowserProxyAsync();
                Assert.Null(resultDisabled);

                AppSettings.General.Set("UseProxies", "true");
                string proxiesPath = StoragePaths.GetUserFile("proxies.txt");
                if (!File.Exists(proxiesPath))
                {
                    var resultMissing = await ChromeLaunchHelper.SelectVerifiedBrowserProxyAsync();
                    Assert.Null(resultMissing);
                }
            }
            finally
            {
                AppSettings.General.Set("UseProxies", origUseProxies ? "true" : "false");
            }
        }

        [Fact]
        public async Task SelectVerifiedBrowserProxyAsync_InvalidProxies_ThrowsInvalidOperationException()
        {
            var origUseProxies = AppSettings.General.Get<bool>("UseProxies");
            string proxiesPath = StoragePaths.GetUserFile("proxies.txt");
            string? backupContent = File.Exists(proxiesPath) ? File.ReadAllText(proxiesPath) : null;

            try
            {
                AppSettings.General.Set("UseProxies", "true");
                Directory.CreateDirectory(Path.GetDirectoryName(proxiesPath)!);
                File.WriteAllLines(proxiesPath, new[]
                {
                    "# Sample comments",
                    "invalid-proxy-line",
                    "http://127.0.0.1:59999"
                });

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                var ex = await Record.ExceptionAsync(() => ChromeLaunchHelper.SelectVerifiedBrowserProxyAsync(cancellationToken: cts.Token));

                // Should throw InvalidOperationException or OperationCanceledException if probe timed out
                Assert.True(ex is InvalidOperationException || ex is OperationCanceledException);
            }
            finally
            {
                AppSettings.General.Set("UseProxies", origUseProxies ? "true" : "false");
                if (backupContent != null)
                    File.WriteAllText(proxiesPath, backupContent);
                else if (File.Exists(proxiesPath))
                    File.Delete(proxiesPath);
            }
        }
    }
}

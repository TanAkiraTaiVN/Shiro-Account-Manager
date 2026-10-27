using System;
using System.Threading.Tasks;
using RBX_Alt_Manager.AvaloniaUI.Views;
using RBX_Alt_Manager.Classes;
using RBX_Alt_Manager.Http;
using RBX_Alt_Manager.Services;
using Xunit;

namespace RBX_Alt_Manager.Tests
{
    public class EdgeCaseRegressionTests
    {
        private const string SampleCookie = "_|WARNING:-DO-NOT-SHARE-THIS.--1234567890abcdefghijklmnopqrstuvwxyz0123456789";

        [Fact]
        public void SanitizeCookie_NullOrWhitespace_ReturnsEmptyString()
        {
            Assert.Equal(string.Empty, Account.SanitizeCookie(null));
            Assert.Equal(string.Empty, Account.SanitizeCookie(""));
            Assert.Equal(string.Empty, Account.SanitizeCookie("   "));
        }

        [Fact]
        public void SanitizeCookie_CleanCookie_ReturnsTrimmed()
        {
            string raw = $"   {SampleCookie}   ";
            string result = Account.SanitizeCookie(raw);
            Assert.Equal(SampleCookie, result);
        }

        [Fact]
        public void SanitizeCookie_QuotedCookie_StripsQuotes()
        {
            string raw = $"\"{SampleCookie}\"";
            Assert.Equal(SampleCookie, Account.SanitizeCookie(raw));

            string singleQuoted = $"'{SampleCookie}'";
            Assert.Equal(SampleCookie, Account.SanitizeCookie(singleQuoted));
        }

        [Fact]
        public void SanitizeCookie_WithRoblosecurityPrefix_StripsPrefix()
        {
            string raw1 = $".ROBLOSECURITY={SampleCookie}";
            Assert.Equal(SampleCookie, Account.SanitizeCookie(raw1));

            string raw2 = $"ROBLOSECURITY={SampleCookie}";
            Assert.Equal(SampleCookie, Account.SanitizeCookie(raw2));

            // Double prefixed bug regression test
            string raw3 = $".ROBLOSECURITY=.ROBLOSECURITY={SampleCookie}";
            Assert.Equal(SampleCookie, Account.SanitizeCookie(raw3));
        }

        [Fact]
        public void SanitizeCookie_WithTrailingCookieAttributes_StripsTrailingAttributes()
        {
            string raw = $".ROBLOSECURITY={SampleCookie}; Domain=.roblox.com; Path=/; Secure; HttpOnly";
            Assert.Equal(SampleCookie, Account.SanitizeCookie(raw));
        }

        [Fact]
        public void Account_SecurityTokenSetter_AutomaticallySanitizesCookie()
        {
            var acc = new Account();
            acc.SecurityToken = $".ROBLOSECURITY=\"{SampleCookie}\"; path=/;";
            Assert.Equal(SampleCookie, acc.SecurityToken);
        }

        [Fact]
        public void BulkImportDialog_ParseAccountLine_UserPassCookie_WithColonsInCookie()
        {
            string line = $"myRobloxUser:mySecretPass:{SampleCookie}";
            var (cookie, password) = BulkImportDialog.ParseAccountLine(line);

            Assert.Equal(SampleCookie, cookie);
            Assert.Equal("mySecretPass", password);
        }

        [Fact]
        public void BulkImportDialog_ParseAccountLine_PipeSeparated_PreservesCookieAndPassword()
        {
            string line = $"myRobloxUser|mySecretPass|{SampleCookie}";
            var (cookie, password) = BulkImportDialog.ParseAccountLine(line);

            Assert.Equal(SampleCookie, cookie);
            Assert.Equal("mySecretPass", password);
        }

        [Fact]
        public void BulkImportDialog_ParseAccountLine_SingleCookieWithPrefix()
        {
            string line = $".ROBLOSECURITY={SampleCookie}; path=/;";
            var (cookie, password) = BulkImportDialog.ParseAccountLine(line);

            Assert.Equal(SampleCookie, cookie);
            Assert.Null(password);
        }

        [Fact]
        public async Task AppDispatcher_InvokeAsync_ExecutesAndReturnsResult()
        {
            int expected = 42;
            int actual = await AppDispatcher.InvokeAsync(() => expected * 2);
            Assert.Equal(84, actual);
        }

        [Fact]
        public async Task KestrelWebServer_ConcurrentBroadcasts_DoNotThrowOrCollide()
        {
            // Verify that calling concurrent broadcasts in rapid succession executes safely without throwing
            var task1 = Task.Run(() => KestrelWebServer.BroadcastAsync("event1", new { a = 1 }));
            var task2 = Task.Run(() => KestrelWebServer.BroadcastAsync("event2", new { b = 2 }));
            var task3 = Task.Run(() => KestrelWebServer.BroadcastAsync("event3", new { c = 3 }));

            await Task.WhenAll(task1, task2, task3);
        }

        [Fact]
        public void BulkImportDialog_CleanCookie_ExtractsStartingAtWarningEvenIfDirtyPrefix()
        {
            string dirty = $"random_prefix_user_123_{SampleCookie}; other_attribute=true";
            string cleaned = BulkImportDialog.CleanCookie(dirty);
            Assert.Equal(SampleCookie, cleaned);
        }

        [Fact]
        public void BulkImportDialog_ParseAccountLine_InvalidLine_ReturnsNull()
        {
            Assert.Equal((null, null), BulkImportDialog.ParseAccountLine(null));
            Assert.Equal((null, null), BulkImportDialog.ParseAccountLine("   "));
            Assert.Equal((null, null), BulkImportDialog.ParseAccountLine("not_a_cookie"));
        }

        

        [Fact]
        public void ParseAccessCode_ClassicHtml_ExtractsGuidCorrectly()
        {
            var acc = new Account();
            string html = "<html><script>Roblox.GameLauncher.joinPrivateGame(189707, 'a1b2c3d4-e5f6-7890-abcd-ef1234567890');</script></html>";
            var response = new RestResponse { Content = html, StatusCode = System.Net.HttpStatusCode.OK };
            bool parsed = acc.ParseAccessCode(response, out string code);

            Assert.True(parsed);
            Assert.Equal("a1b2c3d4-e5f6-7890-abcd-ef1234567890", code);
        }

        [Fact]
        public void ParseAccessCode_ModernJson_ExtractsAccessCodeCorrectly()
        {
            var acc = new Account();
            string json = "{\"data\":{\"accessCode\":\"f8e7d6c5-b4a3-2109-8765-4321fedcba09\"}}";
            var response = new RestResponse { Content = json, StatusCode = System.Net.HttpStatusCode.OK };
            bool parsed = acc.ParseAccessCode(response, out string code);

            Assert.True(parsed);
            Assert.Equal("f8e7d6c5-b4a3-2109-8765-4321fedcba09", code);
        }

        [Fact]
        public void ParsePlaceId_HandlesDirectIdAndRobloxUrls()
        {
            Assert.Equal(113290951185459L, AccountLauncherService.ParsePlaceId("113290951185459"));
            Assert.Equal(113290951185459L, AccountLauncherService.ParsePlaceId("https://www.roblox.com/games/113290951185459/Anime-Dice"));
            Assert.Equal(5315046213L, AccountLauncherService.ParsePlaceId("roblox://placeId=5315046213"));
            Assert.Equal(0L, AccountLauncherService.ParsePlaceId("invalid-id-or-text"));
        }

        [Fact]
        public void AccentThemes_AllHaveValidPrimaryColors()
        {
            foreach (AccentTheme theme in Enum.GetValues<AccentTheme>())
            {
                var colors = AccentThemeService.GetThemeColors(theme);
                Assert.NotEqual(default, colors.primary);
                Assert.NotEqual(default, colors.hover);
            }
        }
    }
}

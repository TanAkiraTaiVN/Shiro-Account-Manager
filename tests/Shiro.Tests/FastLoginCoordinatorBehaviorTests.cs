#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using NSubstitute;
using Shiro.AvaloniaUI.Views;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public class FastLoginCoordinatorBehaviorTests
    {

        [Fact]
        public void FingerprintCookie_ProducesSha256Hex()
        {
            string empty = FastLoginCoordinator.FingerprintCookie(null);
            Assert.Equal(64, empty.Length);

            string fp1 = FastLoginCoordinator.FingerprintCookie("cookie-1");
            string fp2 = FastLoginCoordinator.FingerprintCookie("cookie-1");
            string fp3 = FastLoginCoordinator.FingerprintCookie("cookie-2");

            Assert.Equal(64, fp1.Length);
            Assert.Equal(fp1, fp2);
            Assert.NotEqual(fp1, fp3);
        }

        [Fact]
        public void IsModerationOrLockedUrl_IdentifiesModerationUrls()
        {
            Assert.False(FastLoginCoordinator.IsModerationOrLockedUrl(null));
            Assert.False(FastLoginCoordinator.IsModerationOrLockedUrl(string.Empty));
            Assert.False(FastLoginCoordinator.IsModerationOrLockedUrl("https://www.roblox.com/home"));

            Assert.True(FastLoginCoordinator.IsModerationOrLockedUrl("https://www.roblox.com/not-approved"));
            Assert.True(FastLoginCoordinator.IsModerationOrLockedUrl("https://www.roblox.com/account-status"));
            Assert.True(FastLoginCoordinator.IsModerationOrLockedUrl("https://www.roblox.com/banned"));
            Assert.True(FastLoginCoordinator.IsModerationOrLockedUrl("https://www.roblox.com/moderation"));
            Assert.True(FastLoginCoordinator.IsModerationOrLockedUrl("https://www.roblox.com/account-locked"));
            Assert.True(FastLoginCoordinator.IsModerationOrLockedUrl("https://www.roblox.com/security-notification"));
        }

        [Theory]
        [InlineData("User is not authenticated", false, "UNAUTHENTICATED_MODERATION")]
        [InlineData("Incorrect username or password", false, "WRONG_PASSWORD")]
        [InlineData("Account is locked", false, "ACCOUNT_LOCKED")]
        [InlineData("You are banned for 1 day", true, "BAN_1_DAY")]
        [InlineData("Banned for 3 days", true, "BAN_3_DAYS")]
        [InlineData("Suspension for 7-day period", true, "BAN_7_DAYS")]
        [InlineData("Account banned 14-day duration", true, "BAN_14_DAYS")]
        [InlineData("Account deleted permanently", true, "TERMINATED")]
        [InlineData("Warning issued for chat", true, "WARNING")]
        [InlineData("Your account has been suspended", true, "ACCOUNT_BANNED")]
        [InlineData("Unknown moderation screen", true, "LOCKED_OR_BANNED")]
        public void ClassifyDomIssueCode_CategorizesAccurately(string text, bool isMod, string expectedCode)
        {
            string code = FastLoginCoordinator.ClassifyDomIssueCode(text, isMod);
            Assert.Equal(expectedCode, code);

            var (badge, reason) = FastLoginCoordinator.MapDomIssueCode(code);
            Assert.False(string.IsNullOrEmpty(badge));
            Assert.False(string.IsNullOrEmpty(reason));
        }

        [Fact]
        public void Scripts_GenerateExpectedContent()
        {
            string cssJs = FastLoginCoordinator.GetLoginPageEnhancementScript();
            Assert.Contains("html, body", cssJs);
            Assert.Contains("shiro_password_captured", cssJs);

            string fillScript = FastLoginCoordinator.BuildCredentialFillScript("testuser", "testpass", forceClick: true);
            Assert.Contains("testuser", fillScript);
            Assert.Contains("testpass", fillScript);
            Assert.Contains("forceClick = true", fillScript);
        }

        [Fact]
        public async Task ProcessCapturedSessionAsync_ValidSessionSuccess_UpdatesFieldsAndSaves()
        {
            var repo = Substitute.For<IAccountRepository>();
            using var _ = AppServices.Override<IAccountRepository>(repo);

            var acc = new Account { Username = "shiro_user", SecurityToken = "old_token", Valid = true };
            var result = await FastLoginCoordinator.ProcessCapturedSessionAsync(
                capturedAccount: acc,
                cookieValue: "new_cookie_value",
                expectedUsername: "shiro_user",
                password: "NewPass123",
                alias: "ShiroMain",
                group: "VIP",
                queueCount: 1,
                currentQueueAccount: acc);

            Assert.Equal(FastLoginCaptureStatus.Success, result.Status);
            Assert.Equal("NewPass123", acc.Password);
            Assert.Equal("ShiroMain", acc.Alias);
            Assert.Equal("VIP", acc.Group);
            await repo.Received(1).SaveAsync(true);
        }

        [Fact]
        public async Task ProcessCapturedSessionAsync_UsernameMismatch_ReturnsMismatch()
        {
            var acc = new Account { Username = "actual_user", Valid = true };
            var result = await FastLoginCoordinator.ProcessCapturedSessionAsync(
                capturedAccount: acc,
                cookieValue: "cookie",
                expectedUsername: "expected_user",
                password: null,
                alias: null,
                group: null,
                queueCount: 0,
                currentQueueAccount: null);

            Assert.Equal(FastLoginCaptureStatus.UsernameMismatch, result.Status);
        }

        [Fact]
        public async Task ProcessCapturedSessionAsync_QueueAccountIssues_ClassifiedCorrectly()
        {
            var queueAcc = new Account { Username = "queue_user" };

            // 1. Captured account is banned or locked
            var bannedAcc = new Account { Username = "banned", Valid = false, AccountStatusIssue = "Banned 7 Days" };
            var banResult = await FastLoginCoordinator.ProcessCapturedSessionAsync(
                capturedAccount: bannedAcc,
                cookieValue: "ban_cookie",
                expectedUsername: null,
                password: null,
                alias: null,
                group: null,
                queueCount: 1,
                currentQueueAccount: queueAcc);

            Assert.Equal(FastLoginCaptureStatus.AccountIssue, banResult.Status);
            Assert.Equal("ban_cookie", queueAcc.SecurityToken);

            // 2. DOM detects UNAUTHENTICATED_MODERATION
            var unauthResult = await FastLoginCoordinator.ProcessCapturedSessionAsync(
                capturedAccount: null,
                cookieValue: "c1",
                expectedUsername: null,
                password: null,
                alias: null,
                group: null,
                queueCount: 1,
                currentQueueAccount: queueAcc,
                detectDomIssueAsync: () => Task.FromResult<(string?, string?)>(("UNAUTHENTICATED_MODERATION", "Opening ban")));

            Assert.Equal(FastLoginCaptureStatus.UnauthenticatedModeration, unauthResult.Status);

            // 3. DOM detects other issue
            queueAcc.SecurityToken = string.Empty;
            var domIssueResult = await FastLoginCoordinator.ProcessCapturedSessionAsync(
                capturedAccount: null,
                cookieValue: "c2",
                expectedUsername: null,
                password: null,
                alias: null,
                group: null,
                queueCount: 1,
                currentQueueAccount: queueAcc,
                detectDomIssueAsync: () => Task.FromResult<(string?, string?)>(("BAN_1_DAY", "Banned 1 day")));

            Assert.Equal(FastLoginCaptureStatus.AccountIssue, domIssueResult.Status);
            Assert.Equal("c2", queueAcc.SecurityToken);

            // 4. Retry polling when no issue detected
            var retryResult = await FastLoginCoordinator.ProcessCapturedSessionAsync(
                capturedAccount: null,
                cookieValue: "c3",
                expectedUsername: null,
                password: null,
                alias: null,
                group: null,
                queueCount: 0,
                currentQueueAccount: null);

            Assert.Equal(FastLoginCaptureStatus.RetryPolling, retryResult.Status);
        }

        [Fact]
        public void ClassifyDomIssueCode_WhenUiIsVietnamese_MatchesEnglishDomWithoutSubstringCollisions()
        {
            var prevLang = LocalizationService.CurrentLanguage;
            try
            {
                LocalizationService.SetLanguage(AppLanguage.Vietnamese, saveSetting: false);

                Assert.Equal("BAN_1_DAY", FastLoginCoordinator.ClassifyDomIssueCode("Banned for 1 day", isModUrl: true));
                Assert.NotEqual("BAN_1_DAY", FastLoginCoordinator.ClassifyDomIssueCode("Banned for 21 days", isModUrl: true));
                Assert.Equal("ACCOUNT_BANNED", FastLoginCoordinator.ClassifyDomIssueCode("Banned for 21 days", isModUrl: true));
                Assert.Equal("ACCOUNT_BANNED", FastLoginCoordinator.ClassifyDomIssueCode("Banned for 17 days", isModUrl: true));
                Assert.Equal("ACCOUNT_BANNED", FastLoginCoordinator.ClassifyDomIssueCode("Banned for 13 days", isModUrl: true));
                Assert.Equal("BAN_14_DAYS", FastLoginCoordinator.ClassifyDomIssueCode("Banned for 14 days", isModUrl: true));
                Assert.Equal("BAN_7_DAYS", FastLoginCoordinator.ClassifyDomIssueCode("7-day suspension", isModUrl: true));
                Assert.Equal("BAN_3_DAYS", FastLoginCoordinator.ClassifyDomIssueCode("Banned for 3 days", isModUrl: true));
                Assert.Equal("WRONG_PASSWORD", FastLoginCoordinator.ClassifyDomIssueCode("Incorrect username or password.", isModUrl: false));
                Assert.Equal("WRONG_PASSWORD", FastLoginCoordinator.ClassifyDomIssueCode("Wrong password", isModUrl: false));
                Assert.Equal("TERMINATED", FastLoginCoordinator.ClassifyDomIssueCode("Account deleted permanently", isModUrl: true));
                Assert.Equal("WARNING", FastLoginCoordinator.ClassifyDomIssueCode("Moderation warning", isModUrl: true));
                Assert.Equal("ACCOUNT_LOCKED", FastLoginCoordinator.ClassifyDomIssueCode("Account locked", isModUrl: false));

                // MapDomIssueCode remains localized for UI display
                var (badge, _) = FastLoginCoordinator.ClassifyDomIssueText("Banned for 1 day", isModUrl: true);
                Assert.Equal("Ban 1 ngày", badge);

                // Policy 1: ClassifyDomIssueCode must NOT match localized Vietnamese keywords
                Assert.Equal(string.Empty, FastLoginCoordinator.ClassifyDomIssueCode("sai mật khẩu", isModUrl: false));
            }
            finally
            {
                LocalizationService.SetLanguage(prevLang, saveSetting: false);
                Avalonia.Application.Current?.Resources.MergedDictionaries.Clear();
            }
        }

        private const string CapturedWrongPassJson = "{\"type\":\"shiro_password_captured\",\"password\":\"WrongUserPass!\"}";

        private static Account MakeAcc(string username, string password) =>
            new Account { Username = username, Password = password };

        [AvaloniaFact]
        public async Task Task3_FastLoginDialog_HandleWebViewInitializedAsync_AbortsPostAwaitWhenClosed()
        {
            string viewsDir = Path.Combine(ArchitectureTests.RepoRoot, "src", "Shiro", "AvaloniaUI", "Views");
            string axamlCs = File.ReadAllText(Path.Combine(viewsDir, "FastLoginDialog.axaml.cs"));
            int initIdx = axamlCs.IndexOf("private async Task HandleWebViewInitializedAsync", StringComparison.Ordinal);
            Assert.True(initIdx >= 0);
            int nextIdx = axamlCs.IndexOf("private int _isPollTickBusy", initIdx, StringComparison.Ordinal);
            string initSlice = nextIdx > initIdx ? axamlCs.Substring(initIdx, nextIdx - initIdx) : axamlCs.Substring(initIdx);
            Assert.Contains("_isClosed", initSlice, StringComparison.Ordinal);
            Assert.Contains("InitializeCoreSessionAsync", initSlice, StringComparison.Ordinal);

            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var initSessionMethod = typeof(FastLoginDialog).GetMethod("InitializeCoreSessionAsync", flags);
            Assert.NotNull(initSessionMethod);

            FastLoginDialog? dlg = null;
            try
            {
                dlg = new FastLoginDialog();
                var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                bool navigated = false;
                Func<Task> configure = () => gate.Task;
                Func<Account, Task<string?>> restore = _ => Task.FromResult<string?>(null);
                Action<string> nav = _ => navigated = true;

                Task run = (Task)initSessionMethod!.Invoke(dlg, new object[] { configure, restore, nav })!;
                dlg.Close();
                gate.SetResult();
                await run;

                Assert.False(navigated);
                Assert.Null(typeof(FastLoginDialog).GetField("_pollTimer", flags)!.GetValue(dlg));
            }
            finally
            {
                dlg?.Close();
                Avalonia.Application.Current?.Resources.MergedDictionaries.Clear();
            }
        }

        [AvaloniaFact]
        public void Task3_FastLoginDialog_PreservesPasswordAcrossMismatchReset()
        {
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var handleMsgMethod = typeof(FastLoginDialog).GetMethod("HandleWebMessageJson", flags);
            var resetMethod = typeof(FastLoginDialog).GetMethod("ResetMismatchState", flags);
            var effectivePassMethod = typeof(FastLoginDialog).GetMethod("GetEffectivePasswordForCommit", flags);
            var passField = typeof(FastLoginDialog).GetField("_password", flags);
            Assert.NotNull(handleMsgMethod);
            Assert.NotNull(resetMethod);
            Assert.NotNull(effectivePassMethod);
            Assert.NotNull(passField);

            FastLoginDialog? dlgSaved = null;
            FastLoginDialog? dlgEmpty = null;
            try
            {
                dlgSaved = new FastLoginDialog(MakeAcc("ExpectedUser", "SavedPass1!"));
                handleMsgMethod!.Invoke(dlgSaved, new object?[] { CapturedWrongPassJson });
                Assert.Equal("SavedPass1!", passField!.GetValue(dlgSaved));
                Assert.Equal("WrongUserPass!", effectivePassMethod!.Invoke(dlgSaved, null));
                resetMethod!.Invoke(dlgSaved, null);
                Assert.Equal("SavedPass1!", passField.GetValue(dlgSaved));
                Assert.Equal("SavedPass1!", effectivePassMethod.Invoke(dlgSaved, null));

                dlgEmpty = new FastLoginDialog(MakeAcc("NoPassUser", string.Empty));
                handleMsgMethod.Invoke(dlgEmpty, new object?[] { CapturedWrongPassJson });
                Assert.Equal(string.Empty, passField.GetValue(dlgEmpty));
                resetMethod.Invoke(dlgEmpty, null);
                Assert.Equal(string.Empty, effectivePassMethod.Invoke(dlgEmpty, null));
            }
            finally
            {
                dlgSaved?.Close();
                dlgEmpty?.Close();
                Avalonia.Application.Current?.Resources.MergedDictionaries.Clear();
            }
        }

        private static void AssertGuardAfterAnchor(string source, string anchor, string expectedGuard = "if (_isClosed) return;")
        {
            int idx = source.IndexOf(anchor, StringComparison.Ordinal);
            Assert.True(idx >= 0, "Anchor not found: " + anchor);
            string after = source.Substring(idx + anchor.Length);
            using var reader = new StringReader(after);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                string trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("//", StringComparison.Ordinal))
                    continue;
                Assert.Equal(expectedGuard, trimmed);
                return;
            }
            Assert.Fail("No statement found after anchor: " + anchor);
        }

        [AvaloniaFact]
        public async Task Task3_FastLoginDialog_ConfigureCoreWebViewAsync_AbortsPostAwaitWhenClosed()
        {
            string viewsDir = Path.Combine(ArchitectureTests.RepoRoot, "src", "Shiro", "AvaloniaUI", "Views");
            string autoCs = File.ReadAllText(Path.Combine(viewsDir, "FastLoginDialog.Automation.cs"));
            int cfgIdx = autoCs.IndexOf("private Task ConfigureCoreWebViewAsync", StringComparison.Ordinal);
            if (cfgIdx < 0)
                cfgIdx = autoCs.IndexOf("private async Task ConfigureCoreWebViewAsync", StringComparison.Ordinal);
            Assert.True(cfgIdx >= 0);
            int nextIdx = autoCs.IndexOf("private void OnWebResourceRequested", cfgIdx, StringComparison.Ordinal);
            string cfgSlice = nextIdx > cfgIdx ? autoCs.Substring(cfgIdx, nextIdx - cfgIdx) : autoCs.Substring(cfgIdx);
            Assert.Contains("_isClosed", cfgSlice, StringComparison.Ordinal);
            AssertGuardAfterAnchor(cfgSlice, "await clearCookiesAsync();");
            AssertGuardAfterAnchor(cfgSlice, "await installHbaHookAsync();");
            AssertGuardAfterAnchor(cfgSlice, "await addEnhancementScriptAsync();");

            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var coreMethod = typeof(FastLoginDialog).GetMethod("ConfigureCoreWebViewCoreAsync", flags);
            Assert.NotNull(coreMethod);

            for (int closeAtStep = 0; closeAtStep <= 3; closeAtStep++)
            {
                FastLoginDialog? dlg = null;
                try
                {
                    dlg = new FastLoginDialog();
                    var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    int installCalls = 0, scriptCalls = 0, attachCalls = 0;

                    Func<Task> clearCookies = closeAtStep == 1 ? () => gate.Task : () => Task.CompletedTask;
                    Func<Task> installHook = () =>
                    {
                        installCalls++;
                        return closeAtStep == 2 ? gate.Task : Task.CompletedTask;
                    };
                    Func<Task> addScript = () =>
                    {
                        scriptCalls++;
                        return closeAtStep == 3 ? gate.Task : Task.CompletedTask;
                    };
                    Action attachHandlers = () => attachCalls++;

                    Task run = (Task)coreMethod!.Invoke(dlg, new object[] { clearCookies, installHook, addScript, attachHandlers })!;
                    if (closeAtStep > 0)
                    {
                        dlg.Close();
                        gate.SetResult();
                    }
                    await run;

                    Assert.Equal(closeAtStep == 1 ? 0 : 1, installCalls);
                    Assert.Equal(closeAtStep == 1 || closeAtStep == 2 ? 0 : 1, scriptCalls);
                    Assert.Equal(closeAtStep == 0 ? 1 : 0, attachCalls);
                }
                finally
                {
                    dlg?.Close();
                    Avalonia.Application.Current?.Resources.MergedDictionaries.Clear();
                }
            }
        }

        [AvaloniaFact]
        public async Task Task3_FastLoginDialog_CheckAndCaptureSessionAsync_AbortsPostAwaitWhenClosed()
        {
            string viewsDir = Path.Combine(ArchitectureTests.RepoRoot, "src", "Shiro", "AvaloniaUI", "Views");
            string sessionCs = File.ReadAllText(Path.Combine(viewsDir, "FastLoginDialog.SessionCapture.cs"));
            int checkIdx = sessionCs.IndexOf("private async Task CheckAndCaptureSessionAsync()", StringComparison.Ordinal);
            Assert.True(checkIdx >= 0);
            string checkSlice = sessionCs.Substring(checkIdx);

            AssertGuardAfterAnchor(checkSlice, "var (b, r) = await DetectDomAccountIssueAsync(core);");
            AssertGuardAfterAnchor(checkSlice, "var cookies = await core.CookieManager.GetCookiesAsync(\"https://www.roblox.com\");");
            AssertGuardAfterAnchor(checkSlice, "var (domBadge, domReason) = await DetectDomAccountIssueAsync(core);");
            AssertGuardAfterAnchor(checkSlice, "await Task.Delay(250);");
            AssertGuardAfterAnchor(checkSlice, "var currentCookies = await core.CookieManager.GetCookiesAsync(\"https://www.roblox.com\");", "if (_isClosed) return true;");
            AssertGuardAfterAnchor(checkSlice, "TimeSpan.FromMilliseconds(250));");
            AssertGuardAfterAnchor(checkSlice, "TimeSpan.FromMilliseconds(150));");
            AssertGuardAfterAnchor(checkSlice, "await Task.Delay(300);");
            AssertGuardAfterAnchor(checkSlice, "var (issueBadge, reason) = await DetectDomAccountIssueAsync(core);");

            int modIdx = sessionCs.IndexOf("private async Task HandleCapturedModerationPageAsync", StringComparison.Ordinal);
            Assert.True(modIdx >= 0 && modIdx < checkIdx);
            string modSlice = sessionCs.Substring(modIdx, checkIdx - modIdx);
            int entryGuardIdx = modSlice.IndexOf("if (_isClosed) return;", StringComparison.Ordinal);
            int qAccIdx = modSlice.IndexOf("var qAcc = GetCurrentQueueAccount();", StringComparison.Ordinal);
            Assert.True(entryGuardIdx >= 0 && entryGuardIdx < qAccIdx, "Expected entry _isClosed guard before GetCurrentQueueAccount.");

            var repo = Substitute.For<IAccountRepository>();
            using var repoOverride = AppServices.Override<IAccountRepository>(repo);
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var handleModMethod = typeof(FastLoginDialog).GetMethod("HandleCapturedModerationPageAsync", flags);
            var markIssueMethod = typeof(FastLoginDialog).GetMethod("MarkCurrentAccountIssueAndSkipAsync", flags);
            var redirectMethod = typeof(FastLoginDialog).GetMethod("RedirectUnauthenticatedModerationToLoginAsync", flags);
            Assert.NotNull(handleModMethod);
            Assert.NotNull(markIssueMethod);
            Assert.NotNull(redirectMethod);

            var acc1 = new Account { Username = "Queued1", Password = "P1", Valid = true, AccountStatusIssue = string.Empty, SecurityToken = string.Empty };
            var acc2 = new Account { Username = "Queued2", Password = "P2", Valid = true };
            FastLoginDialog? dlg = null;
            try
            {
                dlg = new FastLoginDialog(new[] { acc1, acc2 });
                dlg.Close();

                await (Task)handleModMethod!.Invoke(dlg, new object?[] { null, "_|WARNING:-tok", "fp-closed", 0 })!;
                await (Task)markIssueMethod!.Invoke(dlg, new object[] { "BAN_1_DAY", "Banned 1 day" })!;
                await (Task)redirectMethod!.Invoke(dlg, new object?[] { null })!;

                Assert.True(acc1.Valid);
                Assert.Equal(string.Empty, acc1.AccountStatusIssue);
                Assert.Equal(string.Empty, acc1.SecurityToken);
                await repo.DidNotReceiveWithAnyArgs().SaveAsync(default);
            }
            finally
            {
                dlg?.Close();
                Avalonia.Application.Current?.Resources.MergedDictionaries.Clear();
            }
        }
    }
}

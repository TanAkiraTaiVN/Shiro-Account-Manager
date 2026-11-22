#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using Shiro.Services;
using Shiro.ViewModels.Dialogs;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public class LaunchSummaryAndSettingsCaptchaTests
    {
        private sealed class FakeCoordinatorWithAppend : ILaunchCoordinator
        {
            private readonly Func<IReadOnlyList<LaunchRequest>, LaunchBatchResult> _onEnqueue;
            public bool IsRunningValue { get; set; }
            public bool IsRunning => IsRunningValue;
            public bool CancelCalled { get; private set; }
            public List<IReadOnlyList<LaunchRequest>> EnqueuedBatches { get; } = new();
            public List<IReadOnlyList<LaunchRequest>> AppendedBatches { get; } = new();

            public FakeCoordinatorWithAppend(Func<IReadOnlyList<LaunchRequest>, LaunchBatchResult>? onEnqueue = null)
            {
                _onEnqueue = onEnqueue ?? (reqs => new LaunchBatchResult(
                    reqs.Select((r, idx) => new LaunchProgress
                    {
                        Index = idx + 1,
                        Total = reqs.Count,
                        Account = r.Account,
                        Status = LaunchOutcome.Success,
                        Completed = true
                    }).ToList(),
                    false));
            }

            public Task<LaunchBatchResult> EnqueueAsync(
                IReadOnlyList<LaunchRequest> requests,
                int delaySeconds,
                bool asyncJoin,
                Action<LaunchProgress>? progress,
                CancellationToken cancellationToken = default)
            {
                EnqueuedBatches.Add(requests);
                return Task.FromResult(_onEnqueue(requests));
            }

            public bool TryAppendRequests(IReadOnlyList<LaunchRequest> requests, out int appendedCount)
            {
                if (!IsRunningValue)
                {
                    appendedCount = 0;
                    return false;
                }
                AppendedBatches.Add(requests);
                appendedCount = requests.Count;
                return true;
            }

            public void SignalNext() { }
            public void Cancel() => CancelCalled = true;
            public Task<bool> WaitForIdleAsync(TimeSpan timeout, CancellationToken cancellationToken = default) => Task.FromResult(true);
            public void Dispose() { }
        }

        [Fact]
        public async Task LaunchAccountsAsync_SeparatesSucceeded_Captcha_AndExpired_WithReloginAction()
        {
            var prevDispatcher = AppDispatcher.CustomDispatcher;
            var prevLang = LocalizationService.CurrentLanguage;
            AppDispatcher.CustomDispatcher = a => a();
            ToastService.ClearAll();

            var dialogMock = Substitute.For<IDialogService>();
            using var _dialogScope = AppServices.Override(dialogMock);

            try
            {
                LocalizationService.SetLanguage(AppLanguage.Vietnamese, saveSetting: false);

                var okAcc = new Account { Username = "OkUser", Valid = true };
                var retryOkAcc = new Account { Username = "RetryOkUser", Valid = true };
                var captchaFailAcc = new Account { Username = "CaptchaUser", Valid = true };
                var expiredAcc = new Account { Username = "ExpiredUser", Valid = false };

                var fakeCoord = new FakeCoordinatorWithAppend(reqs => new LaunchBatchResult(
                    new List<LaunchProgress>
                    {
                        new LaunchProgress { Index = 1, Total = 4, Account = okAcc, Status = LaunchOutcome.Success, Completed = true },
                        new LaunchProgress { Index = 2, Total = 4, Account = retryOkAcc, Status = LaunchOutcome.Success, CaptchaDetected = true, CaptchaRetried = true, Completed = true },
                        new LaunchProgress { Index = 3, Total = 4, Account = captchaFailAcc, Status = LaunchOutcome.Failed, CaptchaDetected = true, CaptchaRetried = true, CaptchaFailedAfterRetry = true, Completed = true },
                        new LaunchProgress { Index = 4, Total = 4, Account = expiredAcc, Status = LaunchOutcome.TicketError, Completed = true }
                    },
                    cancelled: false));

                string? lastStatus = null;
                await AccountLaunchBatchService.LaunchAccountsAsync(
                    fakeCoord,
                    new List<Account> { okAcc, retryOkAcc, captchaFailAcc, expiredAcc },
                    12345,
                    "",
                    false,
                    false,
                    5,
                    false,
                    msg => lastStatus = msg,
                    CancellationToken.None);

                Assert.NotNull(lastStatus);
                Assert.Contains("2/4", lastStatus);
                Assert.Contains("CaptchaUser", lastStatus);
                Assert.Contains("ExpiredUser", lastStatus);

                Assert.Single(ToastService.ActiveToasts);
                var toast = ToastService.ActiveToasts[0];
                Assert.Contains("2/4", toast.Message);
                Assert.Contains("CaptchaUser", toast.Message);
                Assert.Contains("ExpiredUser", toast.Message);
                Assert.True(toast.HasAction);
                Assert.NotNull(toast.ActionCommand);

                toast.ActionCommand!.Execute(null);
                dialogMock.Received(1).OpenFastLoginDialog(expiredAcc);
            }
            finally
            {
                ToastService.ClearAll();
                LocalizationService.SetLanguage(prevLang, saveSetting: false);
                AppDispatcher.CustomDispatcher = prevDispatcher;
            }
        }

        [Fact]
        public async Task LaunchAccountsAsync_WhenOnlyCaptchaFails_DoesNotShowExpiredReloginAction()
        {
            var prevDispatcher = AppDispatcher.CustomDispatcher;
            var prevLang = LocalizationService.CurrentLanguage;
            AppDispatcher.CustomDispatcher = a => a();
            ToastService.ClearAll();

            try
            {
                LocalizationService.SetLanguage(AppLanguage.Vietnamese, saveSetting: false);

                var okAcc = new Account { Username = "OkUser", Valid = true };
                var captchaAcc = new Account { Username = "CaptchaOnlyUser", Valid = true };

                var fakeCoord = new FakeCoordinatorWithAppend(_ => new LaunchBatchResult(
                    new List<LaunchProgress>
                    {
                        new LaunchProgress { Index = 1, Total = 2, Account = okAcc, Status = LaunchOutcome.Success, Completed = true },
                        new LaunchProgress { Index = 2, Total = 2, Account = captchaAcc, Status = LaunchOutcome.Failed, CaptchaDetected = true, CaptchaRetried = true, CaptchaFailedAfterRetry = true, Completed = true }
                    },
                    cancelled: false));

                string? lastStatus = null;
                await AccountLaunchBatchService.LaunchAccountsAsync(
                    fakeCoord,
                    new List<Account> { okAcc, captchaAcc },
                    12345,
                    "",
                    false,
                    false,
                    5,
                    false,
                    msg => lastStatus = msg,
                    CancellationToken.None);

                Assert.Single(ToastService.ActiveToasts);
                var toast = ToastService.ActiveToasts[0];
                Assert.Contains("CaptchaOnlyUser", toast.Message);
                Assert.False(toast.HasAction);
                Assert.DoesNotContain("hết phiên", toast.Message, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                ToastService.ClearAll();
                LocalizationService.SetLanguage(prevLang, saveSetting: false);
                AppDispatcher.CustomDispatcher = prevDispatcher;
            }
        }

        [Fact]
        public async Task ModernJoinServerDirect_WhenQueueIsRunning_AppendsInsteadOfCancelling()
        {
            var mgr = AccountManager.Instance ?? new AccountManager();
            var oldCoord = mgr.launchCoordinator;
            var fakeCoord = new FakeCoordinatorWithAppend { IsRunningValue = true };
            mgr.launchCoordinator = fakeCoord;

            var acc1 = new Account { Username = "AppendAcc1", Valid = true };
            var acc2 = new Account { Username = "AppendAcc2", Valid = true };
            var extra = new Account { Username = "AppendAcc3", Valid = true };

            try
            {
                AccountManager.ReplaceAccountsList(new List<Account> { acc1, acc2, extra });
                await mgr.ModernJoinServerDirect(new List<Account> { extra }, 987654, "job-xyz");

                Assert.False(fakeCoord.CancelCalled);
                Assert.Empty(fakeCoord.EnqueuedBatches);
                Assert.Single(fakeCoord.AppendedBatches);
                Assert.Single(fakeCoord.AppendedBatches[0]);
                Assert.Equal("AppendAcc3", fakeCoord.AppendedBatches[0][0].Account.Username);
                Assert.Equal(987654L, fakeCoord.AppendedBatches[0][0].PlaceId);
                Assert.Equal("job-xyz", fakeCoord.AppendedBatches[0][0].JobId);
            }
            finally
            {
                mgr.launchCoordinator = oldCoord;
                AccountManager.ReplaceAccountsList(new List<Account>());
            }
        }

        [Fact]
        public void SettingsViewModel_WaitForCaptchaInQueue_DefaultsFalse_AndPersistsOnSave()
        {
            var prevIni = AppSettings.ActiveIniFile;
            string tempFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"Shiro_CaptchaSetting_{Guid.NewGuid():N}.ini");
            AppSettings.ActiveIniFile = new IniFile(tempFile);

            try
            {
                var ini = new IniFile();
                ISettingsService settingsSvc = ini["General"];
                var effects = Substitute.For<ISettingsSideEffects>();
                var dialogs = Substitute.For<IDialogService>();
                var logger = Substitute.For<IAppLogger>();

                Assert.Equal("WaitForCaptchaInQueue", AppSettingsKeys.General.WaitForCaptchaInQueue);

                var vm = new SettingsViewModel(
                    settings: null,
                    general: settingsSvc,
                    effects: effects,
                    dialogs: dialogs,
                    feedbackDuration: TimeSpan.FromMilliseconds(10),
                    logger: logger);

                vm.LoadCommand.Execute(null);
                Assert.False(vm.WaitForCaptchaInQueue);

                vm.WaitForCaptchaInQueue = true;
                vm.SaveCommand.Execute(null);
                vm.OnClosing();

                Assert.Equal("true", settingsSvc.Get(AppSettingsKeys.General.WaitForCaptchaInQueue));

                var reloaded = new SettingsViewModel(
                    settings: null,
                    general: settingsSvc,
                    effects: effects,
                    dialogs: dialogs,
                    feedbackDuration: TimeSpan.FromMilliseconds(10),
                    logger: logger);
                reloaded.LoadCommand.Execute(null);
                Assert.True(reloaded.WaitForCaptchaInQueue);
                reloaded.OnClosing();
            }
            finally
            {
                AppSettings.ActiveIniFile = prevIni;
                try { if (System.IO.File.Exists(tempFile)) System.IO.File.Delete(tempFile); }
                catch (System.IO.IOException) { }
            }
        }
    }
}

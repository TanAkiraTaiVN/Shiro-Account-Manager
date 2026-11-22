#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using Shiro.Http;
using Shiro.Services;
using Shiro.ViewModels;
using Shiro.ViewModels.Dialogs;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public class BugHuntRegressionTests
    {
        private sealed class RecordingSyncContext : SynchronizationContext
        {
            public int PostCount { get; private set; }

            public override void Post(SendOrPostCallback d, object? state)
            {
                PostCount++;
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    var prev = Current;
                    SetSynchronizationContext(this);
                    try
                    {
                        d(state);
                    }
                    finally
                    {
                        SetSynchronizationContext(prev);
                    }
                });
            }
        }

        [Fact]
        public async Task AccountWorkflowService_PropertyUpdates_RaiseViewModelNotificationsAndUpdatePinnedCount()
        {
            using var suppressSave = Account.BeginSuppressDeferredSaveScope();
            var dialogs = Substitute.For<IDialogService>();
            var repo = Substitute.For<IAccountRepository>();
            using var dialogScope = AppServices.Override<IDialogService>(dialogs);
            using var repoScope = AppServices.Override<IAccountRepository>(repo);

            var acc = new Account { Username = "NotifyTester", Valid = true, Group = "Default", Pinned = false };
            var accVm = new AccountViewModel(acc);
            using var mainVm = new MainViewModel();
            mainVm.Accounts.Add(accVm);

            var vmChanged = new List<string>();
            accVm.PropertyChanged += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.PropertyName))
                    vmChanged.Add(e.PropertyName);
            };

            var mainChanged = new List<string>();
            mainVm.PropertyChanged += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.PropertyName))
                    mainChanged.Add(e.PropertyName);
            };

            dialogs.ShowInputPromptAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
                .Returns(Task.FromResult("AliasUpdated"));
            await AccountWorkflowService.Default.SetAliasAsync(accVm, static _ => { });
            Assert.Contains(nameof(AccountViewModel.Alias), vmChanged);
            Assert.Contains(nameof(AccountViewModel.PrimaryTitle), vmChanged);
            Assert.Equal("AliasUpdated", accVm.PrimaryTitle);

            vmChanged.Clear();
            dialogs.ShowInputPromptAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
                .Returns(Task.FromResult("Farmers"));
            await AccountWorkflowService.Default.SetGroupAsync(accVm, mainVm, static _ => { });
            Assert.Contains(nameof(AccountViewModel.Group), vmChanged);
            Assert.Contains(nameof(AccountViewModel.GroupDisplay), vmChanged);
            Assert.Contains(nameof(AccountViewModel.GroupTooltip), vmChanged);

            vmChanged.Clear();
            dialogs.ShowInputPromptAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
                .Returns(Task.FromResult("Updated notes"));
            await AccountWorkflowService.Default.SetDescriptionAsync(accVm, static _ => { });
            Assert.Contains(nameof(AccountViewModel.Description), vmChanged);

            vmChanged.Clear();
            mainChanged.Clear();
            AccountWorkflowService.Default.TogglePin(accVm, mainVm, static _ => { });
            Assert.True(accVm.Pinned);
            Assert.Contains(nameof(AccountViewModel.Pinned), vmChanged);
            Assert.Contains(nameof(MainViewModel.PinnedCount), mainChanged);
            Assert.Equal(1, mainVm.PinnedCount);
        }

        [Fact]
        public async Task BulkImportViewModel_StartImportAsync_CookieImport_PreservesCallerSynchronizationContext()
        {
            var prevCtx = SynchronizationContext.Current;
            var uiCtx = new RecordingSyncContext();
            SynchronizationContext.SetSynchronizationContext(uiCtx);
            try
            {
                var repo = Substitute.For<IAccountRepository>();
                repo.AddOrUpdateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                    .Returns(_ => Task.Run(async () =>
                    {
                        await Task.Delay(15).ConfigureAwait(false);
                        return (Account?)new Account { Username = "ImportedCookieUser", Valid = true };
                    }));
                repo.SaveAsync(Arg.Any<bool>()).Returns(Task.CompletedTask);

                var host = Substitute.For<IHostUiBridgeService>();
                var dialogs = Substitute.For<IDialogService>();
                var vm = new BulkImportViewModel(repo, dialogs, host, cookieConcurrency: 1);

                var postAwaitContexts = new List<SynchronizationContext?>();
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName is nameof(BulkImportViewModel.ImportedCount) or nameof(BulkImportViewModel.IsProgressVisible))
                        postAwaitContexts.Add(SynchronizationContext.Current);
                };

                vm.InputText = "_|WARNING:-DO-NOT-SHARE-THIS.|_testcookie123";
                await vm.StartImportAsync();

                Assert.NotEmpty(postAwaitContexts);
                Assert.All(postAwaitContexts, ctx => Assert.Same(uiCtx, ctx));
                Assert.True(uiCtx.PostCount > 0);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(prevCtx);
            }
        }

        [Fact]
        public async Task BulkImportViewModel_StartImportAsync_UserPassLookup_PreservesCallerSynchronizationContext()
        {
            var prevCtx = SynchronizationContext.Current;
            var uiCtx = new RecordingSyncContext();
            SynchronizationContext.SetSynchronizationContext(uiCtx);
            try
            {
                var repo = Substitute.For<IAccountRepository>();
                repo.GetAll().Returns(new List<Account>());
                repo.LookupUserIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                    .Returns(_ => Task.Run(async () =>
                    {
                        await Task.Delay(15).ConfigureAwait(false);
                        return (true, 998877L, (RestResponse?)null);
                    }));
                repo.SaveAsync(Arg.Any<bool>()).Returns(Task.CompletedTask);

                var host = Substitute.For<IHostUiBridgeService>();
                var dialogs = Substitute.For<IDialogService>();
                var vm = new BulkImportViewModel(repo, dialogs, host, cookieConcurrency: 1);

                SynchronizationContext? statusCtx = null;
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(DialogViewModelBase.IsBusy) && !vm.IsBusy)
                        statusCtx = SynchronizationContext.Current;
                };

                vm.InputText = "UserLookupOne:PassWord123";
                await vm.StartImportAsync();

                Assert.Same(uiCtx, statusCtx);
                Assert.True(uiCtx.PostCount > 0);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(prevCtx);
            }
        }

        [Fact]
        public async Task AccountUtilsViewModel_LogOutOtherSessionsAsync_PreservesCallerSynchronizationContext()
        {
            var prevCtx = SynchronizationContext.Current;
            var uiCtx = new RecordingSyncContext();
            SynchronizationContext.SetSynchronizationContext(uiCtx);
            try
            {
                var repo = Substitute.For<IAccountRepository>();
                var prompt = Substitute.For<IUserPrompt>();
                prompt.ConfirmAsync(Arg.Any<string>(), Arg.Any<string>())
                    .Returns(_ => Task.Run(async () =>
                    {
                        await Task.Delay(15).ConfigureAwait(false);
                        return true;
                    }));

                var acc = new Account { Username = "SessionCtxUser", Valid = true };
                var vm = new AccountUtilsViewModel(
                    acc,
                    repo,
                    prompt,
                    _ => Task.Run(async () =>
                    {
                        await Task.Delay(15).ConfigureAwait(false);
                        return true;
                    }));

                var contexts = new List<SynchronizationContext?>();
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName is nameof(DialogViewModelBase.IsBusy) or nameof(DialogViewModelBase.StatusText))
                        contexts.Add(SynchronizationContext.Current);
                };

                await vm.LogOutOtherSessionsAsync();

                Assert.NotEmpty(contexts);
                Assert.All(contexts, ctx => Assert.Same(uiCtx, ctx));
                Assert.True(uiCtx.PostCount >= 2);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(prevCtx);
            }
        }

        [Fact]
        public async Task SettingsSideEffectsService_ImportVaultFromPathAsync_NotifiesHostUiBridgeWhenAccountsAdded()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), $"shiro-vault-reg-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            string vaultPath = Path.Combine(tempDir, "test.shirovault");
            try
            {
                var exportedAcc = new Account { Username = "VaultImportedUser", UserID = 555001, SecurityToken = "tok", Valid = true };
                await using (var fs = File.Create(vaultPath))
                {
                    VaultExportService.ExportVault(new[] { exportedAcc }, "Passphrase#123", fs);
                }

                var stored = new List<Account>();
                var repo = Substitute.For<IAccountRepository>();
                repo.GetAll().Returns(_ => stored.ToList());
                repo.When(r => r.AddThreadSafe(Arg.Any<Account>()))
                    .Do(ci => stored.Add(ci.Arg<Account>()));
                repo.SaveAsync(Arg.Any<bool>()).Returns(Task.CompletedTask);

                var dialogs = Substitute.For<IDialogService>();
                dialogs.ShowInputPromptAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
                    .Returns(Task.FromResult("Passphrase#123"));
                dialogs.ShowSecretPromptAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
                    .Returns(Task.FromResult("Passphrase#123"));

                var host = Substitute.For<IHostUiBridgeService>();

                using var repoScope = AppServices.Override<IAccountRepository>(repo);
                using var dialogScope = AppServices.Override<IDialogService>(dialogs);
                using var hostScope = AppServices.Override<IHostUiBridgeService>(host);

                bool imported = await SettingsSideEffectsService.Default.ImportVaultFromPathAsync(vaultPath);

                Assert.True(imported);
                host.Received(1).NotifyAccountsReplaced(Arg.Is<IReadOnlyList<Account>>(list =>
                    list.Count == 1 && list[0].Username == "VaultImportedUser"));
            }
            finally
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
        }

        [Fact]
        public void StreamerModeService_ShouldApplyActiveOnOpened_UsesLiveIsActive_WhenAutoDetectTriggersBeforeOpen()
        {
            bool prevEnabled = StreamerModeService.IsEnabled;
            bool prevAutoDetect = StreamerModeService.AutoDetectEnabled;
            FieldInfo? detectedField = typeof(StreamerModeService).GetField("_isStreamingAppDetected", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(detectedField);
            bool prevDetected = (bool)(detectedField!.GetValue(null) ?? false);
            try
            {
                StreamerModeService.IsEnabled = false;
                StreamerModeService.AutoDetectEnabled = true;
                detectedField.SetValue(null, true);

                Assert.True(StreamerModeService.IsActive);
                Assert.True(StreamerModeService.ShouldApplyActiveOnOpened(capturedActive: false));
            }
            finally
            {
                detectedField.SetValue(null, prevDetected);
                StreamerModeService.AutoDetectEnabled = prevAutoDetect;
                StreamerModeService.IsEnabled = prevEnabled;
            }
        }
    }
}

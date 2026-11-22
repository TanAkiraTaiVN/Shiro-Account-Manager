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
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using NSubstitute;
using Shiro.AvaloniaUI.Views;
using Shiro.Services;
using Shiro.ViewModels;
using Shiro.ViewModels.Dialogs;
using Xunit;

namespace Shiro.Tests
{
    public sealed class P3AsyncDialogAndSettingsTests
    {
        private static string RepoRoot => ArchitectureTests.RepoRoot;

        [Fact]
        public void SourceFiles_MustNotUseDispatcherFramePushFrameOrBlockingDialogWait()
        {
            string appDir = Path.Combine(RepoRoot, "src", "Shiro");
            var offenders = Directory.EnumerateFiles(appDir, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Replace('\\', '/').Contains("/bin/", StringComparison.OrdinalIgnoreCase) &&
                            !f.Replace('\\', '/').Contains("/obj/", StringComparison.OrdinalIgnoreCase) &&
                            !f.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase))
                .Where(f =>
                {
                    string text = File.ReadAllText(f);
                    return text.Contains("DispatcherFrame", StringComparison.Ordinal) ||
                           text.Contains(".PushFrame(", StringComparison.Ordinal) ||
                           text.Contains("done.Wait(TimeSpan", StringComparison.Ordinal);
                })
                .Select(Path.GetFileName)
                .ToList();

            Assert.Empty(offenders);
        }

        [Fact]
        public void IDialogService_ExposesPureAsyncDialogMethods_AndEliminatesSyncBlockingOverloads()
        {
            Type iface = typeof(IDialogService);
            Assert.NotNull(iface.GetMethod("ShowMessageBoxAsync"));
            Assert.NotNull(iface.GetMethod("ShowConfirmationAsync"));
            Assert.NotNull(iface.GetMethod("ShowYesNoCancelAsync"));
            Assert.NotNull(iface.GetMethod("ShowInputPromptAsync"));
            Assert.NotNull(iface.GetMethod("ShowSecretPromptAsync"));

            Assert.Null(iface.GetMethod("ShowConfirmation"));
            Assert.Null(iface.GetMethod("ShowYesNoCancel"));
            Assert.Null(iface.GetMethod("ShowInputPrompt"));
        }

        [Fact]
        public void StoragePathsTestCollection_MustEnableParallelTestExecution()
        {
            var attr = typeof(StoragePathsTestCollection).Assembly
                .GetCustomAttributes<CollectionBehaviorAttribute>()
                .FirstOrDefault();

            Assert.NotNull(attr);
            Assert.False(attr!.DisableTestParallelization);
        }

        [Fact]
        public async Task AppServicesAndStaticTestOverrides_AreIsolatedAcrossConcurrentAsyncFlows()
        {
            int arrived = 0;
            var bothEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var dirA = Path.Combine(Path.GetTempPath(), "ShiroAsyncIsoA_" + Guid.NewGuid().ToString("N"));
            var dirB = Path.Combine(Path.GetTempPath(), "ShiroAsyncIsoB_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dirA);
            Directory.CreateDirectory(dirB);

            try
            {
                var fakeDialogA = Substitute.For<IDialogService>();
                var fakeDialogB = Substitute.For<IDialogService>();
                using var httpA = new HttpClient();
                using var httpB = new HttpClient();

                Task<(IDialogService Dialog, string DataDir, HttpClient Shared, HttpClient Session, int Dispatched)> RunFlowAsync(
                    IDialogService dlg,
                    string dir,
                    HttpClient http,
                    int marker) => Task.Run(async () =>
                {
                    using var appScope = AppServices.Override(dlg);
                    using var pathScope = StoragePaths.OverrideDataDirectoryForTesting(dir);
                    SharedHttpClient.SetTestClient("default", http);
                    RobloxAccountSessionClient.TestHttpClientFactory = _ => http;
                    int localMarker = 0;
                    AppDispatcher.CustomDispatcher = act =>
                    {
                        localMarker = marker;
                        act();
                    };

                    try
                    {
                        if (Interlocked.Increment(ref arrived) == 2)
                            bothEntered.TrySetResult(true);
                        await bothEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                        await Task.Delay(20).ConfigureAwait(false);

                        int observedDispatch = 0;
                        AppDispatcher.Post(() => observedDispatch = localMarker);

                        return (
                            AppServices.Dialogs,
                            StoragePaths.DataDirectory,
                            SharedHttpClient.Get("default"),
                            RobloxAccountSessionClient.Default.GetSessionHttpClient(new Account { Username = "iso" }),
                            observedDispatch);
                    }
                    catch (Exception ex)
                    {
                        bothEntered.TrySetException(ex);
                        throw;
                    }
                    finally
                    {
                        SharedHttpClient.ResetTestClient("default");
                        RobloxAccountSessionClient.TestHttpClientFactory = null;
                        AppDispatcher.CustomDispatcher = null;
                    }
                });

                var taskA = RunFlowAsync(fakeDialogA, dirA, httpA, 101);
                var taskB = RunFlowAsync(fakeDialogB, dirB, httpB, 202);
                var results = await Task.WhenAll(taskA, taskB);

                Assert.Same(fakeDialogA, results[0].Dialog);
                Assert.Same(fakeDialogB, results[1].Dialog);
                Assert.Equal(dirA, results[0].DataDir);
                Assert.Equal(dirB, results[1].DataDir);
                Assert.Same(httpA, results[0].Shared);
                Assert.Same(httpB, results[1].Shared);
                Assert.Same(httpA, results[0].Session);
                Assert.Same(httpB, results[1].Session);
                Assert.Equal(101, results[0].Dispatched);
                Assert.Equal(202, results[1].Dispatched);
            }
            finally
            {
                if (Directory.Exists(dirA)) Directory.Delete(dirA, true);
                if (Directory.Exists(dirB)) Directory.Delete(dirB, true);
            }
        }

        [Fact]
        public void SettingsDocument_MultiSectionUpdate_WritesDiskAtomicallyOnce()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ShiroSettingsDoc_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string path = Path.Combine(tempDir, "RAMSettings.json");
            try
            {
                int writeCount = 0;
                var doc = new SettingsDocument(path, onAtomicWrite: () => writeCount++);
                doc.Set("General", "Language", "en");
                doc.Set("Watcher", "NoConnectionTimeout", "20");
                doc.Set("WebServer", "Password", string.Empty);
                doc.Save();

                Assert.Equal(1, writeCount);
                Assert.True(File.Exists(path));

                var reloaded = new SettingsDocument(path);
                Assert.Equal("en", reloaded.Get("General", "Language"));
                Assert.Equal("20", reloaded.Get("Watcher", "NoConnectionTimeout"));
                Assert.True(reloaded.Exists("WebServer", "Password"));
                Assert.Equal(string.Empty, reloaded.Get("WebServer", "Password"));
            }
            finally
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            }
        }

        [Fact]
        public async Task SettingsDocument_BeginBatchAndFlushAsync_CoalescesWritesAndSkipsCleanFlush()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ShiroSettingsBatch_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string path = Path.Combine(tempDir, "RAMSettings.json");
            try
            {
                int writeCount = 0;
                var doc = new SettingsDocument(path, onAtomicWrite: () => Interlocked.Increment(ref writeCount));

                using (doc.BeginBatch())
                {
                    doc.Set("General", "Theme", "Cyan");
                    doc.Save();
                    doc.Set("Developer", "DevMode", "true");
                    doc.Save();
                    Assert.Equal(0, writeCount);
                }

                Assert.Equal(1, writeCount);
                Assert.False(doc.IsDirty);

                // Clean FlushAsync must be a no-op
                await doc.FlushAsync();
                Assert.Equal(1, writeCount);

                doc.Set("General", "Theme", "Emerald");
                Assert.True(doc.IsDirty);
                await doc.FlushAsync();
                Assert.Equal(2, writeCount);
                Assert.False(doc.IsDirty);
            }
            finally
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            }
        }

        [Fact]
        public async Task SettingsDocument_ConcurrentSaveAndFlushAsync_NeverPersistsStaleSnapshotOrLosesUpdates()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ShiroSettingsRace_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string path = Path.Combine(tempDir, "RAMSettings.json");
            try
            {
                var doc = new SettingsDocument(path);
                for (int round = 1; round <= 20; round++)
                {
                    string expected = round.ToString();
                    int arrived = 0;
                    var startBoth = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                    Task saveTask = Task.Run(async () =>
                    {
                        if (Interlocked.Increment(ref arrived) == 2) startBoth.TrySetResult(true);
                        await startBoth.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                        doc.Set("General", "Alpha", expected);
                        doc.Save();
                    });

                    Task flushTask = Task.Run(async () =>
                    {
                        if (Interlocked.Increment(ref arrived) == 2) startBoth.TrySetResult(true);
                        await startBoth.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                        doc.Set("General", "Beta", expected);
                        await doc.FlushAsync().ConfigureAwait(false);
                    });

                    await Task.WhenAll(saveTask, flushTask);
                    Assert.False(doc.IsDirty);

                    var reloaded = new SettingsDocument(path);
                    Assert.Equal(expected, reloaded.Get("General", "Alpha"));
                    Assert.Equal(expected, reloaded.Get("General", "Beta"));
                }
            }
            finally
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            }
        }

        [Fact]
        public async Task AvaloniaDialogService_PureAsyncMethods_CompleteWithoutAvaloniaDesktop()
        {
            var svc = AvaloniaDialogService.Default;
            var msgRes = await svc.ShowMessageBoxAsync("Body", "Title", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            bool confirmRes = await svc.ShowConfirmationAsync("Confirm?", "Title");
            bool? yncRes = await svc.ShowYesNoCancelAsync("Prompt?", "Title");
            string inputRes = await svc.ShowInputPromptAsync("Enter:", "Title", "def");
            string secretRes = await svc.ShowSecretPromptAsync("Secret:", "Title", "");

            Assert.Equal(MessageBoxResult.None, msgRes);
            Assert.False(confirmRes);
            Assert.Null(yncRes);
            Assert.Equal("/UC", inputRes);
            Assert.Equal("/UC", secretRes);
        }
    }

    [Collection("SerialGlobalState")]
    public sealed class P3AsyncDialogUiTests
    {
        [AvaloniaFact]
        public async Task MessageBoxDialog_ShowAsync_And_InputDialog_PromptAsync_ResolveCleanlyWithOwner()
        {
            var owner = new Window { Width = 320, Height = 200 };
            owner.Show();
            Dispatcher.UIThread.RunJobs();

            try
            {
                void CloseOwnedSoon(int remaining)
                {
                    var owned = owner.OwnedWindows.ToList();
                    if (owned.Count > 0)
                    {
                        foreach (var child in owned) child.Close();
                        return;
                    }
                    if (remaining > 0)
                        Dispatcher.UIThread.Post(() => CloseOwnedSoon(remaining - 1), DispatcherPriority.Background);
                }

                Dispatcher.UIThread.Post(() => CloseOwnedSoon(20), DispatcherPriority.Background);
                var msgResult = await MessageBoxDialog.ShowAsync("Async message", "Test", MessageBoxButtons.OKCancel, MessageBoxIcon.Information, owner);
                Assert.Equal(MessageBoxResult.None, msgResult);

                Dispatcher.UIThread.Post(() => CloseOwnedSoon(20), DispatcherPriority.Background);
                string inputResult = await InputDialog.PromptAsync("Async prompt", "Test", "default", big: false, owner: owner);
                Assert.Equal("/UC", inputResult);
            }
            finally
            {
                owner.Close();
            }
        }

        [Fact]
        public void SettingsViewModel_Save_CoalescesMultiSectionUpdatesIntoSingleWrite()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ShiroSettingsVmSave_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string path = Path.Combine(tempDir, "RAMSettings.json");
            var prevIni = AppSettings.ActiveIniFile;

            try
            {
                int writeCount = 0;
                var doc = new SettingsDocument(path, onAtomicWrite: () => writeCount++);
                AppSettings.ActiveIniFile = doc.BackingIni;

                var bridge = Substitute.For<IHostUiBridgeService>();
                bridge.When(b => b.SaveSettings()).Do(_ => doc.Save());
                using var bridgeScope = AppServices.Override<IHostUiBridgeService>(bridge);

                var vm = new SettingsViewModel(
                    new AppSettings(),
                    doc.BackingIni.Section(AppSettingsKeys.Sections.General),
                    Substitute.For<ISettingsSideEffects>(),
                    Substitute.For<IDialogService>());

                vm.LoadCommand.Execute(null);
                vm.UnlockFps = true;
                vm.TargetFps = 165m;
                vm.EnableWebServer = true;
                vm.WebServerPort = 8123m;
                vm.AllowExternalConnections = false;
                vm.NoConnectionTimeout = 25m;

                vm.SaveCommand.Execute(null);

                Assert.Equal(1, writeCount);
                Assert.True(File.Exists(path));
                var reloaded = new SettingsDocument(path);
                Assert.Equal("true", reloaded.Get(AppSettingsKeys.Sections.General, AppSettingsKeys.General.UnlockFPS));
                Assert.Equal("165", reloaded.Get(AppSettingsKeys.Sections.General, AppSettingsKeys.General.MaxFPSValue));
                Assert.Equal("true", reloaded.Get(AppSettingsKeys.Sections.Developer, AppSettingsKeys.Developer.EnableWebServer));
                Assert.Equal("8123", reloaded.Get(AppSettingsKeys.Sections.WebServer, AppSettingsKeys.WebServer.WebServerPort));
                Assert.Equal("25", reloaded.Get(AppSettingsKeys.Sections.Watcher, AppSettingsKeys.Watcher.NoConnectionTimeout));
            }
            finally
            {
                AppSettings.ActiveIniFile = prevIni;
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            }
        }

        [Fact]
        public async Task WorkflowDialogsAndSelectByRange_DispatchPostDialogUiUpdatesOnAppDispatcher_EvenWhenDialogCompletesOffThread()
        {
            var prevDispatcher = AppDispatcher.CustomDispatcher;
            int dispatchDepth = 0;
            AppDispatcher.CustomDispatcher = action =>
            {
                Interlocked.Increment(ref dispatchDepth);
                try { action(); }
                finally { Interlocked.Decrement(ref dispatchDepth); }
            };

            var mockDialog = Substitute.For<IDialogService>();
            mockDialog.ShowInputPromptAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
                .Returns(_ => Task.Run(async () => { await Task.Delay(5); return "OffThreadVal"; }));
            mockDialog.ShowConfirmationAsync(Arg.Any<string>(), Arg.Any<string>())
                .Returns(_ => Task.Run(async () => { await Task.Delay(5); return true; }));
            using var dialogScope = AppServices.Override(mockDialog);

            var acc1 = new Account { Username = "WfUser1", UserID = 101 };
            var acc2 = new Account { Username = "WfUser2", UserID = 102 };
            AccountManager.ReplaceAccountsList(new List<Account> { acc1, acc2 });
            try
            {
                using var mainVm = new MainViewModel();
                mainVm.LoadAccounts(new[] { acc1, acc2 });
                var vm1 = mainVm.Accounts[0];
                var vm2 = mainVm.Accounts[1];
                vm1.IsSelected = true;
                vm2.IsSelected = true;
                int outsideDispatch = 0;
                int insideDispatch = 0;
                void AssertDispatched()
                {
                    if (Volatile.Read(ref dispatchDepth) <= 0) Interlocked.Increment(ref outsideDispatch);
                    else Interlocked.Increment(ref insideDispatch);
                }
                vm1.PropertyChanged += (_, _) => AssertDispatched();
                vm2.PropertyChanged += (_, _) => AssertDispatched();
                mainVm.Accounts.CollectionChanged += (_, _) => AssertDispatched();
                Action<string> statusCb = _ => AssertDispatched();

                await AccountWorkflowService.Default.SetAliasAsync(vm1, statusCb);
                await AccountWorkflowService.Default.SetGroupAsync(vm1, mainVm, statusCb);
                await AccountWorkflowService.Default.SetDescriptionAsync(vm1, statusCb);
                await AccountWorkflowService.Default.SetGroupSelectedAsync(mainVm, statusCb);
                mockDialog.ShowInputPromptAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
                    .Returns(_ => Task.Run(async () => { await Task.Delay(5); return "1-2"; }));
                await AccountCollectionCommandActions.SelectByRangePromptAsync(_ => { AssertDispatched(); return 2; }, statusCb);
                await AccountWorkflowService.Default.RemoveAccountAsync(vm1, mainVm, statusCb);
                await AccountWorkflowService.Default.RemoveSelectedAccountsAsync(mainVm, statusCb);

                Assert.Equal(0, Volatile.Read(ref outsideDispatch));
                Assert.True(Volatile.Read(ref insideDispatch) >= 10);
                Assert.Empty(mainVm.Accounts);
            }
            finally
            {
                AccountManager.ReplaceAccountsList(new List<Account>());
                AppDispatcher.CustomDispatcher = prevDispatcher;
            }
        }

        [Fact]
        public async Task LoadAccounts_PendingBackupRestore_HydratesRuntimeState_AndPromotesLockedBackupBeforeUnlock()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ShiroPendingBackup_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            var prevStore = AccountPersistenceService.Default.Store;
            var prevLastValid = AppServices.SelectionState.LastValidAccount;
            var prevShowPresence = AppSettings.General.Get<string>("ShowPresence");
            try
            {
                using var dirScope = StoragePaths.OverrideDataDirectoryForTesting(tempDir);
                AppSettings.General.Set("ShowPresence", "true");
                var bridge = Substitute.For<IHostUiBridgeService>();
                using var bridgeScope = AppServices.Override<IHostUiBridgeService>(bridge);
                var mockDialog = Substitute.For<IDialogService>();
                mockDialog.ShowConfirmationAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(Task.FromResult(true));
                using var dialogScope = AppServices.Override(mockDialog);

                var svc = AccountPersistenceService.Default;
                svc.Store = new AccountStore(StoragePaths.AccountDataFile, StoragePaths.BackupsDirectory);

                // Case 1: Unencrypted backup pending restore hydrates AccountRuntimeStateStore, LastValidAccount, and dashboard
                var plainAcc = new Account { Username = "PlainBackupUser", UserID = 5501, LastUse = DateTime.UtcNow };
                AccountRuntimeStateStore.RecordWindowBounds(plainAcc, 111, 222, 800, 600);
                AccountRuntimeStateStore.Flush();
                AccountRuntimeStateStore.ResetInMemoryCacheForTests();
                File.WriteAllText(StoragePaths.GetUserFile("NoEncryption.IUnderstandTheRisks.iautamor"), "opt-in");
                File.WriteAllBytes(StoragePaths.AccountDataFile + ".backup", AccountDataSerializer.Serialize(new[] { plainAcc }, includePasswords: true));
                AppServices.SelectionState.LastValidAccount = null;
                AccountManager.ReplaceAccountsList(new List<Account>());

                svc.LoadAccounts();
                await mockDialog.DidNotReceiveWithAnyArgs().ShowConfirmationAsync(default!, default!);
                Assert.Equal(0, AppServices.AccountRepository.Count);
                Assert.True(await AppServices.AccountRepository.ResolvePendingBackupRestoreAsync());
                var hydrated = Assert.Single(AppServices.AccountRepository.GetAll());
                Assert.Equal("111", hydrated.GetField("Window_Position_X"));
                Assert.Equal("600", hydrated.GetField("Window_Height"));
                Assert.Same(hydrated, AppServices.SelectionState.LastValidAccount);
                bridge.Received().SchedulePresenceRefresh(null);

                // Case 2: Master-password-locked backup promoted sequentially before IsPasswordEncrypted() check
                File.Delete(StoragePaths.GetUserFile("NoEncryption.IUnderstandTheRisks.iautamor"));
                if (File.Exists(StoragePaths.AccountDataFile)) File.Delete(StoragePaths.AccountDataFile);
                byte[] masterHash = SHA512.HashData(Encoding.UTF8.GetBytes("StrongMasterPass123!"));
                var lockedAcc = new Account { Username = "LockedBackupUser", UserID = 5502 };
                File.WriteAllBytes(StoragePaths.AccountDataFile + ".backup",
                    Cryptography.Encrypt(AccountDataSerializer.Serialize(new[] { lockedAcc }, includePasswords: true), masterHash));
                AccountManager.ReplaceAccountsList(new List<Account>());

                svc.LoadAccounts();
                Assert.False(AppServices.AccountRepository.IsPasswordEncrypted());
                bool sawEncryptedAtUnlock = false;
                bool unlockedAfterPromotion = false;
                await MainWindow.RunStartupPromptsSequentiallyAsync(
                    () => Task.CompletedTask,
                    () =>
                    {
                        sawEncryptedAtUnlock = AppServices.AccountRepository.IsPasswordEncrypted();
                        unlockedAfterPromotion = AppServices.AccountRepository.LoadFromStore(masterHash);
                        return Task.CompletedTask;
                    },
                    () => AppServices.AccountRepository.ResolvePendingBackupRestoreAsync());

                Assert.True(sawEncryptedAtUnlock);
                Assert.True(unlockedAfterPromotion);
                Assert.Equal("LockedBackupUser", Assert.Single(AppServices.AccountRepository.GetAll()).Username);
            }
            finally
            {
                AccountManager.ReplaceAccountsList(new List<Account>());
                AccountPersistenceService.Default.Store = prevStore;
                AppServices.SelectionState.LastValidAccount = prevLastValid;
                if (prevShowPresence == null) AppSettings.General.RemoveProperty("ShowPresence");
                else AppSettings.General.Set("ShowPresence", prevShowPresence);
                AccountRuntimeStateStore.ResetInMemoryCacheForTests();
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            }
        }
    }
}

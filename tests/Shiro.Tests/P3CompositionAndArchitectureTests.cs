#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using NetArchTest.Rules;
using NSubstitute;
using Shiro.Services;
using Shiro.ViewModels;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public sealed class P3CompositionAndArchitectureTests
    {
        private static string RepoRoot => ArchitectureTests.RepoRoot;

        [Fact]
        public void ServicesNamespace_HasZeroReferencesToViewModels()
        {
            string servicesDir = Path.Combine(RepoRoot, "src", "Shiro", "Services");
            var hits = Directory.EnumerateFiles(servicesDir, "*.cs", SearchOption.AllDirectories)
                .Where(f => File.ReadAllText(f).Contains("using Shiro.ViewModels", StringComparison.Ordinal))
                .Select(Path.GetFileName)
                .ToList();

            Assert.Empty(hits);

            var archResult = Types.InAssembly(typeof(AppServices).Assembly)
                .That()
                .ResideInNamespace("Shiro.Services")
                .ShouldNot()
                .HaveDependencyOnAny("Shiro.ViewModels")
                .GetResult();

            var failing = archResult.FailingTypes?.Select(t => t.FullName ?? t.Name).ToList() ?? new List<string>();
            Assert.Empty(failing);
        }

        [Fact]
        public void SourceFiles_HaveZeroModuleInitializerAttributes()
        {
            string appDir = Path.Combine(RepoRoot, "src", "Shiro");
            var hits = Directory.EnumerateFiles(appDir, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Replace('\\', '/').Contains("/bin/", StringComparison.OrdinalIgnoreCase) &&
                            !f.Replace('\\', '/').Contains("/obj/", StringComparison.OrdinalIgnoreCase) &&
                            !f.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase))
                .Where(f => Regex.IsMatch(File.ReadAllText(f), @"\[\s*(?:System\.Runtime\.CompilerServices\.)?ModuleInitializer\s*\]"))
                .Select(Path.GetFileName)
                .ToList();

            Assert.Empty(hits);
        }

        [Fact]
        public void AppServices_SectionSettingsAdapters_ReturnSingletonInstancesAcrossReads()
        {
            var prevIni = AppSettings.ActiveIniFile;
            try
            {
                var prompts1 = AppServices.PromptsSettings;
                var prompts2 = AppServices.PromptsSettings;
                var dev1 = AppServices.DeveloperSettings;
                var dev2 = AppServices.DeveloperSettings;
                var ws1 = AppServices.WebServerSettings;
                var ws2 = AppServices.WebServerSettings;

                Assert.Same(prompts1, prompts2);
                Assert.Same(dev1, dev2);
                Assert.Same(ws1, ws2);

                var ini = new IniFile();
                ini.Section("Prompts").Set("TestPromptFlag", "true");
                AppSettings.ActiveIniFile = ini;
                Assert.True(prompts1.GetBool("TestPromptFlag"));

                using (AppServices.Override<ISettingsService>(new IniSection("General")))
                {
                    Assert.Same(prompts1, AppServices.PromptsSettings);
                    Assert.Same(dev1, AppServices.DeveloperSettings);
                    Assert.Same(ws1, AppServices.WebServerSettings);
                }
            }
            finally
            {
                AppSettings.ActiveIniFile = prevIni;
            }
        }

        [Fact]
        public void IAccountRepository_DoesNotExpose_EnsureBackendInitialized()
        {
            MethodInfo? ifaceMethod = typeof(IAccountRepository).GetMethod("EnsureBackendInitialized");
            MethodInfo? implMethod = typeof(AccountRepository).GetMethod("EnsureBackendInitialized");

            Assert.Null(ifaceMethod);
            Assert.Null(implMethod);
        }

        [Fact]
        public void AccountRepository_ReplaceAll_RaisesAccountsChangedAndPreservesLockSafety()
        {
            var repo = new AccountRepository();
            int raisedCount = -1;
            bool lockHeldDuringEvent = true;
            IReadOnlyList<Account>? snapshot = null;

            repo.AccountsChanged += (_, e) =>
            {
                lockHeldDuringEvent = repo.SyncRoot.IsHeldByCurrentThread;
                snapshot = e.Accounts;
                raisedCount = e.Accounts.Count;
            };

            repo.ReplaceAll(new[] { new Account { Username = "u1", UserID = 101, Valid = true } });

            Assert.Equal(1, raisedCount);
            Assert.False(lockHeldDuringEvent);
            Assert.NotNull(snapshot);
            Assert.Equal("u1", snapshot![0].Username);
            Assert.Throws<NotSupportedException>(() => ((IList<Account>)snapshot).Add(new Account()));
        }

        [Fact]
        public void AccountRepository_AddAndRemoveMutations_RaiseAccountsChangedOutsideSyncRoot()
        {
            var repo = new AccountRepository();
            var events = new List<(int Count, bool HeldLock)>();

            repo.AccountsChanged += (_, e) =>
            {
                events.Add((e.Accounts.Count, repo.SyncRoot.IsHeldByCurrentThread));
            };

            var acc1 = new Account { Username = "acc1", UserID = 201, Valid = true };
            var acc2 = new Account { Username = "acc2", UserID = 202, Valid = true };

            repo.AddThreadSafe(acc1);
            repo.AddThreadSafe(acc2);
            var (removedNone, _) = repo.RemoveMatching(new HashSet<Account> { new Account { Username = "missing" } });
            var (removedOne, isEmptyAfterOne) = repo.RemoveMatching(new HashSet<Account> { acc1 });

            Assert.Equal(0, removedNone);
            Assert.Equal(1, removedOne);
            Assert.False(isEmptyAfterOne);
            Assert.Equal(3, events.Count);
            Assert.Equal(1, events[0].Count);
            Assert.Equal(2, events[1].Count);
            Assert.Equal(1, events[2].Count);
            Assert.All(events, item => Assert.False(item.HeldLock));
        }

        [Fact]
        public void AccountRepository_AccountsChanged_UpdatesMainViewModelOnUiDispatcher_AndUnsubscribesOnDispose()
        {
            var prevDispatcher = AppDispatcher.CustomDispatcher;
            int postedCount = 0;
            AppDispatcher.CustomDispatcher = action =>
            {
                postedCount++;
                action();
            };

            try
            {
                var repo = new AccountRepository();
                var mainVm = new MainViewModel(Substitute.For<IAppLogger>(), new IniFile()["General"], repo);

                repo.ReplaceAll(new[] { new Account { Username = "ReactiveOne", UserID = 301, Valid = true } });
                Assert.True(postedCount >= 1);
                Assert.Single(mainVm.Accounts);
                Assert.Equal("ReactiveOne", mainVm.Accounts[0].Username);
                Assert.Equal(1, mainVm.TotalCount);

                var second = new Account { Username = "ReactiveTwo", UserID = 302, Valid = true };
                repo.AddThreadSafe(second);
                Assert.Equal(2, mainVm.Accounts.Count);
                Assert.NotNull(mainVm.TryGetAccountViewModel(second));

                mainVm.Dispose();
                int countBeforeAfterDispose = postedCount;
                repo.ReplaceAll(Array.Empty<Account>());

                Assert.Equal(countBeforeAfterDispose, postedCount);
                Assert.Equal(2, mainVm.Accounts.Count);
            }
            finally
            {
                AppDispatcher.CustomDispatcher = prevDispatcher;
            }
        }

        [Fact]
        public async Task AppBootstrap_InitializeAsync_WiresServicesAndHostCallbacks_WithoutAccountManagerDependency()
        {
            var archResult = Types.InAssembly(typeof(AppBootstrap).Assembly)
                .That()
                .HaveName("AppBootstrap")
                .ShouldNot()
                .HaveDependencyOnAny("Shiro.AccountManager")
                .GetResult();
            Assert.True(archResult.IsSuccessful);

            bool hostWired = false;
            int shutdownCount = 0;
            var bootstrap = new AppBootstrap();
            await bootstrap.InitializeAsync(
                initializeHostAsync: (sp, _) =>
                {
                    hostWired = ReferenceEquals(sp, bootstrap.Services);
                    return Task.CompletedTask;
                },
                shutdownHostAsync: () =>
                {
                    shutdownCount++;
                    return ValueTask.CompletedTask;
                });

            Assert.True(hostWired);
            Assert.Same(
                bootstrap.Services.GetRequiredKeyedService<ISettingsService>("Prompts"),
                bootstrap.Services.GetRequiredKeyedService<ISettingsService>("Prompts"));
            Assert.Same(
                bootstrap.Services.GetRequiredKeyedService<ISettingsService>("Developer"),
                bootstrap.Services.GetRequiredKeyedService<ISettingsService>("Developer"));
            Assert.Same(
                bootstrap.Services.GetRequiredKeyedService<ISettingsService>("WebServer"),
                bootstrap.Services.GetRequiredKeyedService<ISettingsService>("WebServer"));
            Assert.IsType<AccountRepository>(bootstrap.Services.GetRequiredService<IAccountRepository>());

            await bootstrap.DisposeAsync();
            await bootstrap.DisposeAsync();

            Assert.Equal(1, shutdownCount);
            Assert.Throws<ObjectDisposedException>(() => { _ = bootstrap.Services; });
        }

        [Fact]
        public async Task AppBootstrap_DisposeAsync_ReleasesDeferredSettingsSaveTimer()
        {
            var bootstrap = new AppBootstrap();
            await bootstrap.InitializeAsync();

            int saveInvocations = 0;
            bootstrap.QueueDeferredSettingsSave(() => Interlocked.Increment(ref saveInvocations), delayMs: 5000);

            FieldInfo? timerField = typeof(AppBootstrap).GetField("_deferredSettingsSaveTimer", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(timerField);
            Assert.NotNull(timerField!.GetValue(bootstrap));

            await bootstrap.DisposeAsync();
            Assert.Null(timerField.GetValue(bootstrap));

            bootstrap.QueueDeferredSettingsSave(() => Interlocked.Increment(ref saveInvocations), delayMs: 50);
            Assert.Null(timerField.GetValue(bootstrap));
            Assert.Equal(0, saveInvocations);
        }

        [Fact]
        public void ToastAndSettingsSideEffects_OperateWithoutViewModelDependencies()
        {
            var prevDispatcher = AppDispatcher.CustomDispatcher;
            var prevNotifyCb = SettingsSideEffectsService.NotifyDisplayNamesChangedCallback;
            var prevClearCb = SettingsSideEffectsService.ClearAvatarCacheCallback;
            AppDispatcher.CustomDispatcher = a => a();

            try
            {
                ToastService.ClearAll();
                bool actionRan = false;
                ToastService.ShowWarningWithAction("Title", "Body", "Act", () => actionRan = true, durationSeconds: 0);
                ToastItem item = Assert.Single(ToastService.ActiveToasts);
                Assert.True(item.HasAction);
                Assert.True(item.ActionCommand!.CanExecute(null));
                item.ActionCommand.Execute(null);
                Assert.True(actionRan);
                Assert.Empty(ToastService.ActiveToasts);

                int displayNotifyCount = 0;
                int clearAvatarCount = 0;
                SettingsSideEffectsService.NotifyDisplayNamesChangedCallback = () => displayNotifyCount++;
                SettingsSideEffectsService.ClearAvatarCacheCallback = () => clearAvatarCount++;

                SettingsSideEffectsService.Default.NotifyDisplayNamesChanged();
                _ = SettingsSideEffectsService.Default.ClearAvatarCache();

                Assert.Equal(1, displayNotifyCount);
                Assert.Equal(1, clearAvatarCount);
            }
            finally
            {
                ToastService.ClearAll();
                SettingsSideEffectsService.NotifyDisplayNamesChangedCallback = prevNotifyCb;
                SettingsSideEffectsService.ClearAvatarCacheCallback = prevClearCb;
                AppDispatcher.CustomDispatcher = prevDispatcher;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference<MainViewModel> CreateUndisposedMainViewModelWeak(IAccountRepository repo)
        {
            var vm = new MainViewModel(Substitute.For<IAppLogger>(), new IniFile()["General"], repo);
            return new WeakReference<MainViewModel>(vm);
        }

        [Fact]
        public void MainViewModel_UndisposedInstance_IsReclaimedByGcWhileSubscribedToRepository()
        {
            var repo = new AccountRepository();
            WeakReference<MainViewModel> weakVm = CreateUndisposedMainViewModelWeak(repo);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            repo.AddThreadSafe(new Account { Username = "TriggerDetach", UserID = 9001, Valid = true });

            Assert.False(weakVm.TryGetTarget(out _));
        }

        [Fact]
        public void AccountPersistenceAndRegistration_DoNotDispatchRedundantNotifyAccountsReplaced()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ShiroNoRedundantNotify_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            var bridge = Substitute.For<IHostUiBridgeService>();
            using var bridgeScope = AppServices.Override<IHostUiBridgeService>(bridge);

            try
            {
                var repo = new AccountRepository();
                int changedCount = 0;
                repo.AccountsChanged += (_, _) => changedCount++;

                var persistence = new AccountPersistenceService(repo)
                {
                    Store = new AccountStore(Path.Combine(tempDir, "AccountData.json"), Path.Combine(tempDir, "Backups")),
                    StartTime = DateTime.Now.AddMinutes(-5)
                };
                var registration = new AccountRegistrationService(repo, persistence);

                var initial = new Account { Username = "InitialPersistUser", UserID = 4101, Valid = true, SecurityToken = "tok-1" };
                persistence.ReplaceAccountsList(new List<Account> { initial });
                persistence.SaveAccounts(bypassRateLimit: true, bypassCountCheck: true);
                persistence.LoadAccounts();

                var added = new Account { Username = "RegisteredPersistUser", UserID = 4102, Valid = true, SecurityToken = "tok-2" };
                registration.RegisterValidatedAccount(added, "tok-2", saveImmediate: true);

                Assert.Equal(3, changedCount);
                bridge.DidNotReceive().NotifyAccountsReplaced(Arg.Any<IReadOnlyList<Account>>());
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, recursive: true); }
                    catch (IOException) { }
                }
            }
        }

        [Fact]
        public void MainViewModel_OutOfOrderAccountsChanged_IgnoresStaleLowerVersion()
        {
            var prevDispatcher = AppDispatcher.CustomDispatcher;
            var queuedActions = new Queue<Action>();
            AppDispatcher.CustomDispatcher = action => queuedActions.Enqueue(action);

            try
            {
                // 1. Real AccountRepository emits strictly monotonic versions inside _syncRoot
                var realRepo = new AccountRepository();
                var realVersions = new List<long>();
                realRepo.AccountsChanged += (_, e) => realVersions.Add(e.Version);
                var realAcc = new Account { Username = "MonoUser", UserID = 5001, Valid = true };
                realRepo.ReplaceAll(new[] { realAcc });
                realRepo.AddThreadSafe(new Account { Username = "MonoUser2", UserID = 5002, Valid = true });
                realRepo.RemoveMatching(new HashSet<Account> { realAcc });
                Assert.Equal(new long[] { 1L, 2L, 3L }, realVersions);

                // 2. Out-of-order arrival: Version 2 arrives before Version 1 -> Version 1 must not overwrite Version 2
                var mockRepo = Substitute.For<IAccountRepository>();
                using var mainVm = new MainViewModel(Substitute.For<IAppLogger>(), new IniFile()["General"], mockRepo);
                var newerAcc = new Account { Username = "NewerUser", UserID = 5102, Valid = true };
                var staleAcc = new Account { Username = "StaleUser", UserID = 5101, Valid = true };

                mockRepo.AccountsChanged += Raise.Event<EventHandler<AccountsChangedEventArgs>>(
                    mockRepo,
                    new AccountsChangedEventArgs(new[] { newerAcc }, version: 2));
                mockRepo.AccountsChanged += Raise.Event<EventHandler<AccountsChangedEventArgs>>(
                    mockRepo,
                    new AccountsChangedEventArgs(new[] { staleAcc }, version: 1));

                while (queuedActions.Count > 0)
                    queuedActions.Dequeue().Invoke();

                Assert.Single(mainVm.Accounts);
                Assert.Equal("NewerUser", mainVm.Accounts[0].Username);

                // 3. Queued stale event (Version 3) superseded by newer event (Version 4) before UI drain
                var midAcc = new Account { Username = "MidUser", UserID = 5103, Valid = true };
                var latestAcc = new Account { Username = "LatestUser", UserID = 5104, Valid = true };
                mockRepo.AccountsChanged += Raise.Event<EventHandler<AccountsChangedEventArgs>>(
                    mockRepo,
                    new AccountsChangedEventArgs(new[] { midAcc }, version: 3));
                mockRepo.AccountsChanged += Raise.Event<EventHandler<AccountsChangedEventArgs>>(
                    mockRepo,
                    new AccountsChangedEventArgs(new[] { latestAcc }, version: 4));

                while (queuedActions.Count > 0)
                    queuedActions.Dequeue().Invoke();

                Assert.Single(mainVm.Accounts);
                Assert.Equal("LatestUser", mainVm.Accounts[0].Username);
            }
            finally
            {
                AppDispatcher.CustomDispatcher = prevDispatcher;
            }
        }
    }
}

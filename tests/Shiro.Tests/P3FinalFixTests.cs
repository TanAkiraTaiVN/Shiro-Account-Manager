#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
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
    [Collection("SerialGlobalState")]
    public sealed class P3FinalFixTests
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference<AccountCollectionViewModel> CreateUndisposedAccountsVmWithSideEffectsWeak(
            IAccountRepository repo,
            ISettingsSideEffects sideEffects)
        {
            var vm = new AccountCollectionViewModel(
                Substitute.For<IAppLogger>(),
                new IniFile()["General"],
                repo,
                settingsSideEffects: sideEffects);
            return new WeakReference<AccountCollectionViewModel>(vm);
        }

        [AvaloniaFact]
        public async Task SettingsSideEffects_NotifyAcrossSuppressedExecutionContext_ReachesAccountCollectionViewModel_AndReclaimsUndisposedOnGc()
        {
            Assert.Null(typeof(SettingsSideEffectsService).GetProperty(
                "NotifyDisplayNamesChangedCallback",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static));
            Assert.Null(typeof(SettingsSideEffectsService).GetProperty(
                "ClearAvatarCacheCallback",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static));
            Assert.NotNull(typeof(SettingsSideEffectsService).GetProperty(
                "TestPickExportPathOverride",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static));

            string tempDir = Path.Combine(Path.GetTempPath(), "ShiroSideEffectsEc_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            IniFile ini = new IniFile();
            IniSection general = ini["General"];
            general.Set("HideUsernames", "false");

            bool prevStreamerEnabled = StreamerModeService.IsEnabled;
            bool prevStreamerAuto = StreamerModeService.AutoDetectEnabled;
            StreamerModeService.AutoDetectEnabled = false;
            StreamerModeService.IsEnabled = false;

            var sideEffects = new SettingsSideEffectsService();
            var repo = new AccountRepository();
            using var settingsScope = AppServices.Override<ISettingsService>(general);
            using var effectsScope = AppServices.Override<ISettingsSideEffects>(sideEffects);
            using var dirScope = StoragePaths.OverrideDataDirectoryForTesting(tempDir);

            try
            {
                string settingsViewCodeBehind = File.ReadAllText(Path.Combine(
                    ArchitectureTests.RepoRoot, "src", "Shiro", "AvaloniaUI", "Views", "SettingsView.axaml.cs"));
                Assert.DoesNotContain("MainWindow", settingsViewCodeBehind, StringComparison.Ordinal);
                Assert.DoesNotContain("NotifyDisplayNamesChangedCallback", settingsViewCodeBehind, StringComparison.Ordinal);
                Assert.DoesNotContain("ClearAvatarCacheCallback", settingsViewCodeBehind, StringComparison.Ordinal);

                _ = new SettingsView(new SettingsViewModel(new AppSettings(), general, sideEffects));

                using var accountsVm = new AccountCollectionViewModel(
                    Substitute.For<IAppLogger>(),
                    general,
                    repo);

                var account = new Account { Username = "DisplaySecretUser", UserID = 778899, Valid = true };
                accountsVm.LoadAccounts(new[] { account });
                AccountViewModel accVm = Assert.Single(accountsVm.Accounts);

                string avatarsDir = Path.Combine(tempDir, "Cache", "Avatars");
                Directory.CreateDirectory(avatarsDir);
                string avatarPath = Path.Combine(avatarsDir, account.UserID + ".png");
                byte[] pngBytes = Convert.FromBase64String(
                    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");
                await File.WriteAllBytesAsync(avatarPath, pngBytes);

                Assert.Equal("DisplaySecretUser", accVm.ShownUsername);
                Assert.Equal(avatarPath, await accVm.FetchAndCacheAvatarAsync());
                Dispatcher.UIThread.RunJobs();
                Assert.NotNull(accVm.AvatarImage);

                var changedProps = new List<string>();
                accVm.PropertyChanged += (_, e) =>
                {
                    if (!string.IsNullOrEmpty(e.PropertyName))
                        changedProps.Add(e.PropertyName);
                };

                general.Set("HideUsernames", "true");
                Assert.Empty(changedProps);

                Task<int> bgTask;
                using (ExecutionContext.SuppressFlow())
                {
                    bgTask = Task.Run(() =>
                    {
                        using var bgDirScope = StoragePaths.OverrideDataDirectoryForTesting(tempDir);
                        sideEffects.NotifyDisplayNamesChanged();
                        return sideEffects.ClearAvatarCache();
                    });
                }

                int deletedCount = await bgTask;
                Dispatcher.UIThread.RunJobs();

                Assert.Equal(1, deletedCount);
                Assert.False(File.Exists(avatarPath));
                Assert.Contains("ShownUsername", changedProps);
                Assert.Contains("DisplayTitle", changedProps);
                Assert.Contains("AvatarImage", changedProps);
                Assert.Equal("D***************r", accVm.ShownUsername);
                Assert.Null(accVm.AvatarImage);

                changedProps.Clear();
                accountsVm.Dispose();
                general.Set("HideUsernames", "false");
                sideEffects.NotifyDisplayNamesChanged();
                _ = sideEffects.ClearAvatarCache();
                Dispatcher.UIThread.RunJobs();
                Assert.Empty(changedProps);

                WeakReference<AccountCollectionViewModel> weakVm =
                    CreateUndisposedAccountsVmWithSideEffectsWeak(repo, sideEffects);
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                sideEffects.NotifyDisplayNamesChanged();
                _ = sideEffects.ClearAvatarCache();
                Dispatcher.UIThread.RunJobs();

                Assert.False(weakVm.TryGetTarget(out _));
            }
            finally
            {
                StreamerModeService.IsEnabled = prevStreamerEnabled;
                StreamerModeService.AutoDetectEnabled = prevStreamerAuto;
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, recursive: true); }
                    catch (IOException) { }
                }
            }
        }

        [Fact]
        public async Task SettingsService_And_AccountManagerSaveSettings_ResolveOverrideDirectoryDynamicallyAtSaveTime()
        {
            string tempRoot = Path.Combine(Path.GetTempPath(), "ShiroSettingsIso_" + Guid.NewGuid().ToString("N"));
            string initialDir = Path.Combine(tempRoot, "initial");
            string overrideDir = Path.Combine(tempRoot, "override");
            string flushDir = Path.Combine(tempRoot, "flush");
            string managerDir = Path.Combine(tempRoot, "manager");
            Directory.CreateDirectory(initialDir);
            Directory.CreateDirectory(overrideDir);
            Directory.CreateDirectory(flushDir);
            Directory.CreateDirectory(managerDir);

            IniFile prevIni = AppSettings.ActiveIniFile;
            AccountManager? prevInstance = AccountManager.Instance;
            AccountStore prevStore = AccountPersistenceService.Default.Store;
            var prevAccounts = AccountManager.Instance != null
                ? AppServices.AccountRepository.GetAll()
                : Array.Empty<Account>();

            try
            {
                SettingsService settingsService;
                SettingsService customRootedService;
                string customRootedPath = Path.Combine(initialDir, "explicit-rooted-settings.json");

                using (StoragePaths.OverrideDataDirectoryForTesting(initialDir))
                {
                    var ini = new IniFile();
                    settingsService = new SettingsService(ini);

                    var customDoc = new SettingsDocument(customRootedPath);
                    customRootedService = new SettingsService(customDoc);

                    // Construct AccountManager while initialDir is active (before overrideDir is active).
                    _ = new AccountManager();
                }

                string initialSettingsPath = Path.Combine(initialDir, "RAMSettings.json");
                string overrideSettingsPath = Path.Combine(overrideDir, "RAMSettings.json");
                string flushSettingsPath = Path.Combine(flushDir, "RAMSettings.json");
                string managerSettingsPath = Path.Combine(managerDir, "RAMSettings.json");

                using (StoragePaths.OverrideDataDirectoryForTesting(overrideDir))
                {
                    settingsService.Set("General", "DynamicKey", "OverrideValue");
                    settingsService.Save();

                    Assert.True(File.Exists(overrideSettingsPath));
                    Assert.False(File.Exists(initialSettingsPath));
                    var reloaded = new SettingsDocument(overrideSettingsPath);
                    Assert.Equal("OverrideValue", reloaded.Get("General", "DynamicKey"));

                    customRootedService.Set("General", "ExplicitKey", "ExplicitValue");
                    customRootedService.Save();
                    Assert.True(File.Exists(customRootedPath));
                    Assert.Equal("ExplicitValue", new SettingsDocument(customRootedPath).Get("General", "ExplicitKey"));
                }

                using (StoragePaths.OverrideDataDirectoryForTesting(flushDir))
                {
                    settingsService.Set("General", "FlushKey", "FlushValue");
                    await settingsService.Document.FlushAsync();

                    Assert.True(File.Exists(flushSettingsPath));
                    Assert.False(File.Exists(initialSettingsPath));
                    Assert.Equal("FlushValue", new SettingsDocument(flushSettingsPath).Get("General", "FlushKey"));
                }

                using (StoragePaths.OverrideDataDirectoryForTesting(managerDir))
                {
                    AppSettings.General.Set("ManagerDynamicKey", "ManagerOverrideValue");
                    AccountManager.SaveSettings();

                    Assert.True(File.Exists(managerSettingsPath));
                    Assert.False(File.Exists(initialSettingsPath));
                    Assert.Equal("ManagerOverrideValue", new SettingsDocument(managerSettingsPath).Get("General", "ManagerDynamicKey"));
                }
            }
            finally
            {
                AccountManager.ReplaceAccountsList(new List<Account>(prevAccounts));
                AccountPersistenceService.Default.Store = prevStore;
                AppSettings.ActiveIniFile = prevIni;
                AccountManager.Instance = prevInstance;
                AccountManager.WireHostServices();
                if (Directory.Exists(tempRoot))
                {
                    try { Directory.Delete(tempRoot, recursive: true); }
                    catch (IOException) { }
                }
            }
        }

        [Fact]
        public async Task AppBootstrap_TwoPhaseInit_InvokesHostInitializerOnce_AndAllowsRetryAfterFailure()
        {
            await using var syncBootstrap = new AppBootstrap();
            int syncCalls = 0;

            syncBootstrap.InitializeSync(configureServices: null);
            syncBootstrap.InitializeSync(
                configureServices: null,
                initializeHost: sp =>
                {
                    syncCalls++;
                    Assert.Same(syncBootstrap.Services, sp);
                });
            syncBootstrap.InitializeSync(
                configureServices: null,
                initializeHost: _ => syncCalls++);

            Assert.Equal(1, syncCalls);

            await using var asyncBootstrap = new AppBootstrap();
            int asyncCalls = 0;

            await asyncBootstrap.InitializeAsync();
            await asyncBootstrap.InitializeAsync(
                initializeHostAsync: (sp, _) =>
                {
                    asyncCalls++;
                    Assert.Same(asyncBootstrap.Services, sp);
                    return Task.CompletedTask;
                });
            await asyncBootstrap.InitializeAsync(
                initializeHostAsync: (_, _) =>
                {
                    asyncCalls++;
                    return Task.CompletedTask;
                });

            Assert.Equal(1, asyncCalls);

            await using var retryBootstrap = new AppBootstrap();
            int retryCalls = 0;
            Assert.Throws<InvalidOperationException>(() =>
                retryBootstrap.InitializeSync(initializeHost: _ => throw new InvalidOperationException("transient")));

            retryBootstrap.InitializeSync(initializeHost: _ => retryCalls++);
            retryBootstrap.InitializeSync(initializeHost: _ => retryCalls++);
            Assert.Equal(1, retryCalls);
        }

        [Fact]
        public void WindowBackdropService_FirstFrameUncloakCoordinator_UncloaksOnceAfterTwoFrames()
        {
            var pendingFrames = new Queue<Action<TimeSpan>>();
            int startTimerCalls = 0;
            int stopTimerCalls = 0;
            int uncloakCalls = 0;

            var coordinator = new WindowBackdropService.FirstFrameUncloakCoordinator(
                requestFrame: cb => pendingFrames.Enqueue(cb),
                startFailsafeTimer: () => startTimerCalls++,
                stopFailsafeTimer: () => stopTimerCalls++,
                uncloakAction: () => uncloakCalls++);

            coordinator.OnOpened();
            Assert.Equal(1, startTimerCalls);
            Assert.Equal(0, uncloakCalls);
            Assert.Single(pendingFrames);

            // Frame 1 schedules Frame 2 without uncloaking prematurely.
            pendingFrames.Dequeue().Invoke(TimeSpan.FromMilliseconds(16));
            Assert.Equal(0, uncloakCalls);
            Assert.Equal(0, stopTimerCalls);
            Assert.Single(pendingFrames);

            // Frame 2 completes presentation and uncloaks exactly once.
            pendingFrames.Dequeue().Invoke(TimeSpan.FromMilliseconds(32));
            Assert.Equal(1, uncloakCalls);
            Assert.Equal(1, stopTimerCalls);

            // Later failsafe or close events must be idempotent no-ops.
            coordinator.OnFailsafeElapsed();
            coordinator.OnClosed();
            Assert.Equal(1, uncloakCalls);
            Assert.Equal(1, stopTimerCalls);
        }

        [Fact]
        public void WindowBackdropService_FirstFrameUncloakCoordinator_FailsafeAndClose_NeverLeaveWindowCloaked()
        {
            int failsafeStarts = 0;
            int failsafeStops = 0;
            int failsafeUncloaks = 0;
            var failsafeCoordinator = new WindowBackdropService.FirstFrameUncloakCoordinator(
                requestFrame: _ => { },
                startFailsafeTimer: () => failsafeStarts++,
                stopFailsafeTimer: () => failsafeStops++,
                uncloakAction: () => failsafeUncloaks++);

            failsafeCoordinator.OnOpened();
            failsafeCoordinator.OnFailsafeElapsed();
            failsafeCoordinator.OnClosed();
            Assert.Equal(1, failsafeStarts);
            Assert.Equal(1, failsafeStops);
            Assert.Equal(1, failsafeUncloaks);

            int closeStops = 0;
            int closeUncloaks = 0;
            var earlyCloseCoordinator = new WindowBackdropService.FirstFrameUncloakCoordinator(
                requestFrame: _ => { },
                startFailsafeTimer: () => { },
                stopFailsafeTimer: () => closeStops++,
                uncloakAction: () => closeUncloaks++);

            earlyCloseCoordinator.OnClosed();
            earlyCloseCoordinator.OnOpened();
            Assert.Equal(1, closeStops);
            Assert.Equal(1, closeUncloaks);
        }

        [Fact]
        public void WindowBackdropService_DarkCaptionColor_MatchesBackgroundAndWindows11Threshold()
        {
            Assert.Equal(0x000F0B0B, WindowBackdropService.DarkWindowColorRef);
            Assert.False(WindowBackdropService.ShouldApplyDarkCaptionColor(19045));
            Assert.True(WindowBackdropService.ShouldApplyDarkCaptionColor(22000));
            Assert.True(WindowBackdropService.ShouldApplyDarkCaptionColor(22621));
        }
    }
}

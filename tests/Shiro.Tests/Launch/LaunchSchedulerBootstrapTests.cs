#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using NetArchTest.Rules;
using NSubstitute;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public sealed class LaunchSchedulerBootstrapTests : IDisposable
    {
        private readonly string _tempDir;
        private readonly IDisposable _dirScope;

        public LaunchSchedulerBootstrapTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), $"Shiro_SchedBootstrap_{Guid.NewGuid():N}");
            _dirScope = StoragePaths.OverrideDataDirectoryForTesting(_tempDir);
        }

        public void Dispose()
        {
            AccountManager.CancelDeferredSaveSettings();
            _dirScope.Dispose();
            try
            {
                if (Directory.Exists(_tempDir))
                    Directory.Delete(_tempDir, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort temp cleanup
            }
        }

        [Fact]
        public async Task AppBootstrap_RegistersLaunchScheduleStoreAndScheduler_AsSingletons()
        {
            await using var bootstrap = new AppBootstrap();
            IServiceProvider provider = bootstrap.Services;

            LaunchScheduleStore store1 = provider.GetRequiredService<LaunchScheduleStore>();
            LaunchScheduleStore store2 = provider.GetRequiredService<LaunchScheduleStore>();
            ILaunchSchedulerService scheduler1 = provider.GetRequiredService<ILaunchSchedulerService>();
            ILaunchSchedulerService scheduler2 = provider.GetRequiredService<ILaunchSchedulerService>();

            Assert.Same(store1, store2);
            Assert.Same(scheduler1, scheduler2);
            Assert.IsType<LaunchSchedulerService>(scheduler1);
            Assert.False(scheduler1.IsRunning);

            AppServices.EnsureInitialized();
            Assert.Same(AppServices.LaunchScheduleStore, AppServices.LaunchScheduleStore);
            Assert.Same(AppServices.LaunchScheduler, AppServices.LaunchScheduler);
            Assert.IsType<LaunchSchedulerService>(AppServices.LaunchScheduler);
        }

        [Fact]
        public void AccountManager_ShutdownCoordinator_StopsSchedulerInStopMonitors_AndFlushesStoreInFlushPersistence()
        {
            AccountManager? prevInstance = AccountManager.Instance;
            IniFile prevIni = AppSettings.ActiveIniFile;
            bool prevSuppress = AccountManager.SuppressExitOnShutdown;
            bool origSingletonEnabled = RobloxSingletonService.IsEnabled;
            AccountManager? mgr = null;
            AccountManager? liveMgr = null;

            try
            {
                AccountManager.SuppressExitOnShutdown = true;
                AppServices.EnsureInitialized();

                mgr = new AccountManager();
                AppSettings.Developer.Set("EnableWebServer", "false");
                AppSettings.General.Set("ShowPresence", "false");
                AppSettings.General.Set("AutoCookieRefresh", "false");
                AppSettings.General.Set("EnableMultiRbx", "false");

                MethodInfo? buildMethod = typeof(AccountManager).GetMethod(
                    "BuildShutdownCoordinator",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(buildMethod);

                FieldInfo? participantsField = typeof(ShutdownCoordinator).GetField(
                    "_participants",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(participantsField);

                var mockScheduler = Substitute.For<ILaunchSchedulerService>();
                using (AppServices.Override<ILaunchSchedulerService>(mockScheduler))
                {
                    var coordinator = (ShutdownCoordinator)buildMethod!.Invoke(mgr, null)!;
                    var participants = ((IEnumerable)participantsField!.GetValue(coordinator)!)
                        .Cast<object>()
                        .Select(entry =>
                        {
                            FieldInfo item1 = entry.GetType().GetField("Item1")!;
                            return (IShutdownParticipant)item1.GetValue(entry)!;
                        })
                        .ToList();

                    IShutdownParticipant? stopSched = participants.FirstOrDefault(p => p.Name == "LaunchScheduler.StopForShutdown");
                    IShutdownParticipant? flushStore = participants.FirstOrDefault(p => p.Name == "LaunchScheduleStore.Flush");

                    Assert.NotNull(stopSched);
                    Assert.NotNull(flushStore);
                    Assert.Equal(ShutdownPhase.StopMonitors, stopSched!.Phase);
                    Assert.Equal(ShutdownPhase.FlushPersistence, flushStore!.Phase);

                    mgr.InitializeBackendServices();
                    mgr.InitializePostStartup();
                    mockScheduler.Received(1).AttachExecutor(Arg.Any<ISchedulerActionExecutor>());
                    mockScheduler.Received(1).Start();

                    IReadOnlyList<string> executionOrder = coordinator.ExecuteAll();
                    int stopIdx = executionOrder.ToList().IndexOf("LaunchScheduler.StopForShutdown");
                    int flushIdx = executionOrder.ToList().IndexOf("LaunchScheduleStore.Flush");
                    Assert.True(stopIdx >= 0, "Expected LaunchScheduler.StopForShutdown in execution order.");
                    Assert.True(flushIdx > stopIdx, "Expected FlushPersistence step to execute after StopMonitors step.");
                    mockScheduler.Received(1).StopForShutdown();
                }

                liveMgr = new AccountManager();
                AppSettings.Developer.Set("EnableWebServer", "false");
                AppSettings.General.Set("ShowPresence", "false");
                AppSettings.General.Set("AutoCookieRefresh", "false");
                AppSettings.General.Set("EnableMultiRbx", "false");
                liveMgr.InitializeBackendServices();
                liveMgr.InitializePostStartup();
                Assert.True(AppServices.LaunchScheduler.IsRunning);

                liveMgr.ShutdownBackend();
                Assert.True(liveMgr.IsDisposed);
                Assert.False(AppServices.LaunchScheduler.IsRunning);
            }
            finally
            {
                AntiAfkService.Stop();
                MemoryOptimizer.ConfigureAutoTrim(false);
                RobloxAudioService.ConfigureSmartMute(false);
                RobloxSingletonService.ResetForTest(origSingletonEnabled);
                if (liveMgr != null && !liveMgr.IsDisposed)
                    liveMgr.Dispose();
                if (mgr != null && !mgr.IsDisposed)
                    mgr.Dispose();

                AppSettings.ActiveIniFile = prevIni;
                AccountManager.Instance = prevInstance;
                if (prevInstance != null && !prevInstance.IsDisposed)
                    AccountManager.EnsureBackendInitialized();
                AccountManager.SuppressExitOnShutdown = prevSuppress;
            }
        }

        [Fact]
        public void SchedulerServices_HaveZeroDependencyOnAccountManagerOrViews()
        {
            var assembly = typeof(LaunchSchedulerService).Assembly;
            var result = Types.InAssembly(assembly)
                .That()
                .ResideInNamespace("Shiro.Services")
                .And()
                .HaveNameMatching(".*LaunchSchedule.*|.*Scheduler.*")
                .ShouldNot()
                .HaveDependencyOnAny("Shiro.AccountManager", "Shiro.AvaloniaUI.Views", "Shiro.ViewModels")
                .GetResult();

            string failing = string.Join(", ", result.FailingTypes?.Select(t => t.FullName) ?? Enumerable.Empty<string>());
            Assert.True(result.IsSuccessful, $"Scheduler services must not depend on AccountManager, Views, or ViewModels. Violations: {failing}");
        }
    }
}

#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using NSubstitute;
using Shiro.AvaloniaUI.Views;
using Shiro.Services;
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
    }
}

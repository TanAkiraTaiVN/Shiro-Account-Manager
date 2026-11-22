#nullable enable
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using NSubstitute;
using Shiro.AvaloniaUI.Views;
using Shiro.Services;
using Shiro.ViewModels;
using Shiro.ViewModels.Dialogs;
using Xunit;

namespace Shiro.Tests.Ui
{
    [Collection("SerialGlobalState")]
    public sealed class SettingsAndWorkflowTask3Tests : IDisposable
    {
        private sealed class AsyncHttpHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _responder;
            public AsyncHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) => _responder = responder;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                _responder(request, cancellationToken);
        }

        private static readonly byte[] OneByOnePngBytes = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

        private const string HeadshotUrlJson = "{\"data\":[{\"targetId\":777666555445,\"imageUrl\":\"https://cdn.rbx.test/av-race.png\"}]}";

        public void Dispose()
        {
            AntiAfkService.Stop();
            RobloxBackgroundThrottlerService.Instance.IsEnabled = false;
            RobloxBackgroundThrottlerService.Instance.TargetBackgroundFps = 10;
            SharedHttpClient.ResetTestClient("default");
            AccountViewModel.ClearAvatarMemoryCache();
        }

        private static bool IsBitmapDisposed(Bitmap bitmap)
        {
            try
            {
                _ = bitmap.PixelSize;
                return false;
            }
            catch (ObjectDisposedException)
            {
                return true;
            }
            catch (NullReferenceException)
            {
                // Avalonia Bitmap.Dispose() clears its internal PlatformImpl ref, causing PixelSize to throw NullReferenceException.
                return true;
            }
        }

        [Fact]
        public void SettingsSideEffects_SetAntiAfk_AndMainViewModel_IsAntiAfk_PersistEnableAntiAfk()
        {
            var ini = new IniFile();
            var gen = ini["General"];
            using var settingsScope = AppServices.Override<ISettingsService>(gen);

            SettingsSideEffectsService.Default.SetAntiAfk(true);
            Assert.True(AntiAfkService.IsEnabled);
            Assert.Equal("true", gen.Get<string>("EnableAntiAfk"));

            SettingsSideEffectsService.Default.SetAntiAfk(false);
            Assert.False(AntiAfkService.IsEnabled);
            Assert.Equal("false", gen.Get<string>("EnableAntiAfk"));

            using var mainVm = new MainViewModel(logger: Substitute.For<IAppLogger>(), settings: gen);
            mainVm.IsAntiAfk = true;
            Assert.Equal("true", gen.Get<string>("EnableAntiAfk"));
        }

        [Fact]
        public void AccountManager_InitializePostStartup_AppliesBackgroundThrottlingSettings()
        {
            var prevMgr = AccountManager.Instance;
            var prevIni = AppSettings.ActiveIniFile;
            bool prevSuppress = AccountManager.SuppressExitOnShutdown;
            bool origSingletonEnabled = RobloxSingletonService.IsEnabled;
            string tempDir = Path.Combine(Path.GetTempPath(), $"Shiro_Task3Init_{Guid.NewGuid():N}");
            IDisposable? dirScope = null;
            AccountManager? tempMgr = null;
            try
            {
                dirScope = StoragePaths.OverrideDataDirectoryForTesting(tempDir);
                AccountManager.SuppressExitOnShutdown = true;
                AppSettings.ActiveIniFile = new IniFile();
                tempMgr = new AccountManager();
                AppSettings.General.Set("EnableMultiRbx", "false");
                AppSettings.General.Set(AppSettingsKeys.General.EnableBackgroundThrottling, "true");
                AppSettings.General.Set(AppSettingsKeys.General.BackgroundTargetFps, "15");

                tempMgr.InitializePostStartup();

                Assert.True(RobloxBackgroundThrottlerService.Instance.IsEnabled);
                Assert.Equal(15, RobloxBackgroundThrottlerService.Instance.TargetBackgroundFps);
            }
            finally
            {
                RobloxBackgroundThrottlerService.Instance.IsEnabled = false;
                RobloxSingletonService.ResetForTest(origSingletonEnabled);
                tempMgr?.Dispose();
                AppSettings.ActiveIniFile = prevIni;
                AccountManager.Instance = prevMgr;
                if (prevMgr != null) AccountManager.EnsureBackendInitialized();
                AccountManager.SuppressExitOnShutdown = prevSuppress;
                AccountManager.CancelDeferredSaveSettings();
                dirScope?.Dispose();
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch (IOException) { }
            }
        }

        [Fact]
        public void SettingsViewModel_Save_PersistsAntiAfkAndCallsUpdateMultiRobloxOnBridge()
        {
            var prevIni = AppSettings.ActiveIniFile;
            string tempIni = Path.Combine(Path.GetTempPath(), $"shiro-task3-set-{Guid.NewGuid():N}.ini");
            try
            {
                AppSettings.ActiveIniFile = new IniFile(tempIni);
                var settings = new AppSettings();
                var general = AppSettings.ActiveIniFile.Section("General");
                using var generalScope = AppServices.Override<ISettingsService>(general);

                var mockBridge = Substitute.For<IHostUiBridgeService>();
                using var bridgeScope = AppServices.Override<IHostUiBridgeService>(mockBridge);

                var vm = new SettingsViewModel(settings, general, SettingsSideEffectsService.Default);
                vm.Load();
                vm.MultiRbx = false;
                vm.AntiAfk = true;
                vm.EnableBackgroundThrottling = true;
                vm.BackgroundTargetFps = 15m;
                vm.Save();

                mockBridge.Received(1).UpdateMultiRoblox();
                Assert.Equal("true", general.Get<string>("EnableAntiAfk"));
                Assert.Equal("true", general.Get<string>(AppSettingsKeys.General.EnableBackgroundThrottling));
                Assert.Equal("15", general.Get<string>(AppSettingsKeys.General.BackgroundTargetFps));
                Assert.True(RobloxBackgroundThrottlerService.Instance.IsEnabled);
                Assert.Equal(15, RobloxBackgroundThrottlerService.Instance.TargetBackgroundFps);

                var reloaded = new SettingsViewModel(settings, general, SettingsSideEffectsService.Default);
                reloaded.Load();
                Assert.True(reloaded.AntiAfk);
                Assert.True(reloaded.EnableBackgroundThrottling);
                Assert.Equal(15m, reloaded.BackgroundTargetFps);

                reloaded.EnableBackgroundThrottling = false;
                reloaded.BackgroundTargetFps = 999m;
                reloaded.Save();
                Assert.False(RobloxBackgroundThrottlerService.Instance.IsEnabled);
                Assert.Equal(60, RobloxBackgroundThrottlerService.Instance.TargetBackgroundFps);
                Assert.Equal("60", general.Get<string>(AppSettingsKeys.General.BackgroundTargetFps));
            }
            finally
            {
                AppSettings.ActiveIniFile = prevIni;
                if (File.Exists(tempIni))
                {
                    try { File.Delete(tempIni); }
                    catch (IOException ex) { AppServices.Logger.Debug($"Temp INI delete: {ex.Message}"); }
                }
            }
        }

        [Fact]
        public async Task CookieImportViewModel_LoadFromAccount_ClearsStaleCookiePreviewAndVerifiedState()
        {
            var repo = Substitute.For<IAccountRepository>();
            var verifiedAccount = new Account
            {
                Username = "VerifiedPrev",
                UserID = 101,
                Valid = true,
                SecurityToken = "_|WARNING:-verified-cookie-1"
            };
            var vm = new CookieImportViewModel(
                repo: repo,
                verify: _ => Task.FromResult(verifiedAccount));

            vm.Cookie = "_|WARNING:-verified-cookie-1";
            await vm.VerifyAsync();
            Assert.Contains("VerifiedPrev", vm.PreviewUsername);

            var nextEmptyAccount = new Account
            {
                Username = "NextNoCookie",
                Alias = "NextAlias",
                Group = "BatchGroup",
                Password = "pw",
                SecurityToken = string.Empty
            };

            vm.LoadFromAccount(nextEmptyAccount);

            Assert.Equal(string.Empty, vm.Cookie);
            Assert.Equal(string.Empty, vm.PreviewUsername);
            Assert.Equal("NextAlias", vm.Alias);
            Assert.Equal("BatchGroup", vm.Group);
        }

        [AvaloniaFact]
        public async Task AccountViewModel_InvalidateAvatar_RejectsStaleInFlightAndDisposesReplacedBitmapSafely()
        {
            AccountViewModel.ClearAvatarMemoryCache();
            long userId = 777666555444L;
            long raceUserId = 777666555445L;
            string avatarsDir = Path.Combine(StoragePaths.CacheDirectory, "Avatars");
            Directory.CreateDirectory(avatarsDir);
            string diskPath = Path.Combine(avatarsDir, $"{userId}.png");
            string raceDiskPath = Path.Combine(avatarsDir, $"{raceUserId}.png");
            if (File.Exists(raceDiskPath)) File.Delete(raceDiskPath);

            var downloadStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseDownload = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            try
            {
                await File.WriteAllBytesAsync(diskPath, OneByOnePngBytes);
                var vm = new AccountViewModel(new Account { UserID = userId, Username = "AvatarDisposeUser" });

                await vm.FetchAndCacheAvatarAsync();
                Dispatcher.UIThread.RunJobs();
                Bitmap? firstBmp = vm.AvatarImage;
                Assert.NotNull(firstBmp);

                vm.InvalidateAvatar();
                Dispatcher.UIThread.RunJobs();
                Assert.Null(vm.AvatarImage);
                Assert.True(IsBitmapDisposed(firstBmp!));

                SharedHttpClient.SetTestClient("default", new HttpClient(new AsyncHttpHandler(async (req, _) =>
                {
                    string u = req.RequestUri?.AbsoluteUri ?? string.Empty;
                    if (u.Contains("avatar-headshot"))
                    {
                        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(HeadshotUrlJson) };
                    }
                    downloadStarted.TrySetResult(true);
                    await releaseDownload.Task.ConfigureAwait(false);
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(OneByOnePngBytes) };
                })));

                var raceVm = new AccountViewModel(new Account { UserID = raceUserId, Username = "AvatarRaceUser" });
                Task<string> inFlightTask = raceVm.FetchAndCacheAvatarAsync();
                await downloadStarted.Task;

                // Clear/invalidate BEFORE the in-flight HTTP download completes
                raceVm.InvalidateAvatar();
                releaseDownload.TrySetResult(true);
                await inFlightTask;
                Dispatcher.UIThread.RunJobs();

                // Delete disk file so next check tests pure memory cache / stale in-flight isolation
                if (File.Exists(raceDiskPath)) File.Delete(raceDiskPath);
                Assert.Null(raceVm.AvatarImage);
            }
            finally
            {
                releaseDownload.TrySetResult(true);
                SharedHttpClient.ResetTestClient("default");
                AccountViewModel.ClearAvatarMemoryCache();
                if (File.Exists(diskPath)) File.Delete(diskPath);
                if (File.Exists(raceDiskPath)) File.Delete(raceDiskPath);
            }
        }

        [AvaloniaFact]
        public void AccountCardView_DoubleTap_IgnoresInteractiveButtonsAndDebouncesRapidDoubleTaps()
        {
            var dashboard = new DashboardView();
            int launchCalls = 0;
            dashboard.ViewModel.OnLaunchRequested = _ => launchCalls++;

            var accVm = new AccountViewModel(new Account { Username = "CardDoubleTapUser", Valid = true });
            var card = new AccountCardView { DataContext = accVm };
            var hostPanel = dashboard.Content as Panel;
            Assert.NotNull(hostPanel);
            hostPanel!.Children.Add(card);

            var window = new Window { Content = dashboard, Width = 800, Height = 600 };
            window.Show();
            try
            {
                var cardRoot = card.FindControl<Border>("CardRoot");
                var btnDelete = card.FindControl<Button>("BtnDelete");
                Assert.NotNull(cardRoot);
                Assert.NotNull(btnDelete);

                // Double-tapping an interactive button inside the card must NOT launch the account
                btnDelete!.RaiseEvent(new TappedEventArgs(InputElement.DoubleTappedEvent, null!) { Source = btnDelete });
                Assert.Equal(0, launchCalls);

                // Rapid duplicate double-taps on the card surface must debounce to 1 launch
                cardRoot!.RaiseEvent(new TappedEventArgs(InputElement.DoubleTappedEvent, null!) { Source = cardRoot });
                cardRoot.RaiseEvent(new TappedEventArgs(InputElement.DoubleTappedEvent, null!) { Source = cardRoot });
                Assert.Equal(1, launchCalls);
            }
            finally
            {
                hostPanel.Children.Remove(card);
                window.Close();
                dashboard.ViewModel.Dispose();
            }
        }

        [Fact]
        public void SettingsSideEffects_UpdateMultiRoblox_WhenBridgeUnwired_AppliesSingletonState()
        {
            bool origEnabled = RobloxSingletonService.IsEnabled;
            var ini = new IniFile();
            var gen = ini["General"];
            gen.Set(AppSettingsKeys.General.EnableMultiRbx, "false");
            var cleanBridge = new HostUiBridgeService();
            using var genScope = AppServices.Override<ISettingsService>(gen);
            using var bridgeScope = AppServices.Override<IHostUiBridgeService>(cleanBridge);

            try
            {
                SettingsSideEffectsService.Default.UpdateMultiRoblox();
                Assert.False(RobloxSingletonService.IsEnabled);
            }
            finally
            {
                RobloxSingletonService.ResetForTest(origEnabled);
            }
        }
    }
}

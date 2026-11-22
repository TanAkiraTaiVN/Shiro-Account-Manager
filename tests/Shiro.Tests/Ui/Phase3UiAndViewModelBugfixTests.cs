#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using NSubstitute;
using Shiro.Http;
using Shiro.Services;
using Shiro.ViewModels;
using Shiro.ViewModels.Dialogs;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public sealed class Phase3UiAndViewModelBugfixTests
    {
        private static readonly byte[] OneByOnePngBytes = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

        private sealed class FakeHttpHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _responder;
            public FakeHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
                => _responder = responder;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => _responder(request, cancellationToken);
        }

        private sealed class StubSettingsSideEffects : ISettingsSideEffects
        {
            public void ApplyTheme(AccentTheme theme, bool save) { }
            public void SetLanguage(AppLanguage lang) { }
            public void PatchClientSettings() { }
            public void SetAntiAfk(bool on) { }
            public void UpdateMultiRoblox() { }
            public void ConfigureBackgroundThrottling(bool enabled, int targetFps) { }
            public void ConfigureSmartMute(bool on) { }
            public void ConfigureAutoTrim(bool on) { }
            public void SetCpuRateLimit(int percent) { }
            public Task RestartWebServerAsync(int port, bool allowExternal) => Task.CompletedTask;
            public Task StopWebServerAsync() => Task.CompletedTask;
            public void SetAutoCookieRefresh(bool on) { }
            public event EventHandler? DisplayNamesChanged { add { } remove { } }
            public event EventHandler? AvatarCacheCleared { add { } remove { } }
            public void NotifyDisplayNamesChanged() { }
            public void QueueDeferredSave() { }
            public void CleanNotificationArea() { }
            public int ClearAvatarCache() => 0;
            public Task ResetEncryptionAsync() => Task.CompletedTask;
            public Task<bool> ExportVaultAsync() => Task.FromResult(false);
            public Task<bool> ImportVaultAsync() => Task.FromResult(false);
        }

        [AvaloniaFact]
        public async Task AccountViewModel_InvalidateAvatar_OnOneAccount_DoesNotInvalidateOtherAccountAvatar()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), $"shiro-p3-avatar-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            using var dirScope = StoragePaths.OverrideDataDirectoryForTesting(tempDir);
            AccountViewModel.ClearAvatarMemoryCache();

            const long userId1 = 910001L;
            const long userId2 = 910002L;
            const long userId3 = 910003L;
            string avatarsDir = Path.Combine(StoragePaths.CacheDirectory, "Avatars");
            Directory.CreateDirectory(avatarsDir);
            string path1 = Path.Combine(avatarsDir, $"{userId1}.png");
            string path2 = Path.Combine(avatarsDir, $"{userId2}.png");

            var downloadStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseDownload = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            try
            {
                await File.WriteAllBytesAsync(path1, OneByOnePngBytes);
                await File.WriteAllBytesAsync(path2, OneByOnePngBytes);

                var acc1 = new AccountViewModel(new Account { UserID = userId1, Username = "AvatarIsoA" });
                var acc2 = new AccountViewModel(new Account { UserID = userId2, Username = "AvatarIsoB" });

                Assert.Equal(path1, await acc1.FetchAndCacheAvatarAsync());
                Assert.Equal(path2, await acc2.FetchAndCacheAvatarAsync());
                Dispatcher.UIThread.RunJobs();

                Bitmap? acc2BmpBefore = acc2.AvatarImage;
                Assert.NotNull(acc1.AvatarImage);
                Assert.NotNull(acc2BmpBefore);
                Assert.Equal(72, acc2BmpBefore!.PixelSize.Width);

                SharedHttpClient.SetTestClient("default", new HttpClient(new FakeHttpHandler(async (req, _) =>
                {
                    string uri = req.RequestUri?.AbsoluteUri ?? string.Empty;
                    if (uri.Contains("avatar-headshot", StringComparison.OrdinalIgnoreCase))
                    {
                        return new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent($"{{\"data\":[{{\"targetId\":{userId3},\"imageUrl\":\"https://cdn.rbx.test/av3.png\"}}]}}")
                        };
                    }

                    downloadStarted.TrySetResult(true);
                    await releaseDownload.Task.ConfigureAwait(false);
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(OneByOnePngBytes) };
                })));

                var acc3 = new AccountViewModel(new Account { UserID = userId3, Username = "AvatarIsoC" });
                Task<string> acc3FetchTask = acc3.FetchAndCacheAvatarAsync();
                await downloadStarted.Task;

                // Invalidating acc1 must clear ONLY acc1, leaving acc2 cached and acc3 in-flight intact.
                acc1.InvalidateAvatar();
                Dispatcher.UIThread.RunJobs();

                Assert.Null(acc1.AvatarImage);
                Assert.Same(acc2BmpBefore, acc2.AvatarImage);
                Assert.Equal(72, acc2.AvatarImage!.PixelSize.Height);

                releaseDownload.TrySetResult(true);
                string acc3Path = await acc3FetchTask;
                Dispatcher.UIThread.RunJobs();

                Assert.False(string.IsNullOrEmpty(acc3Path));
                Assert.NotNull(acc3.AvatarImage);
                Assert.Equal(72, acc3.AvatarImage!.PixelSize.Width);

                // Global memory cache clear invalidates all accounts when disk files are gone.
                File.Delete(path2);
                File.Delete(acc3Path);
                AccountViewModel.ClearAvatarMemoryCache();
                Dispatcher.UIThread.RunJobs();

                Assert.Null(acc2.AvatarImage);
                Assert.Null(acc3.AvatarImage);
            }
            finally
            {
                releaseDownload.TrySetResult(true);
                SharedHttpClient.ResetTestClient("default");
                AccountViewModel.ClearAvatarMemoryCache();
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, recursive: true); }
                    catch (IOException) { }
                }
            }
        }

        [Fact]
        public void ToastService_CoalescedToast_UpdatesActionCommandToLatestOnAction()
        {
            var prevDispatcher = AppDispatcher.CustomDispatcher;
            AppDispatcher.CustomDispatcher = action => action();
            try
            {
                ToastService.ClearAll();
                int firstCallbackCount = 0;
                int secondCallbackCount = 0;

                ToastService.ShowWarningWithAction("Sync Warning", "Session needs attention", "Retry", () => firstCallbackCount++, durationSeconds: 0);
                ToastService.ShowWarningWithAction("Sync Warning", "Session needs attention", "Retry", () => secondCallbackCount++, durationSeconds: 0);

                ToastItem toast = Assert.Single(ToastService.ActiveToasts);
                Assert.True(toast.HasAction);
                Assert.NotNull(toast.ActionCommand);

                toast.ActionCommand!.Execute(null);

                Assert.Equal(0, firstCallbackCount);
                Assert.Equal(1, secondCallbackCount);
                Assert.Empty(ToastService.ActiveToasts);
            }
            finally
            {
                ToastService.ClearAll();
                AppDispatcher.CustomDispatcher = prevDispatcher;
            }
        }

        [Fact]
        public void SettingsViewModel_LoadAndSave_ClampsOutOfRangeWebServerPort()
        {
            string tempIni = Path.Combine(Path.GetTempPath(), $"shiro-p3-port-{Guid.NewGuid():N}.ini");
            IniFile? prevIni = AppSettings.ActiveIniFile;
            try
            {
                AppSettings.ActiveIniFile = new IniFile(tempIni);
                using var bridgeScope = AppServices.Override<IHostUiBridgeService>(new HostUiBridgeService());
                var settings = new AppSettings();
                var general = new IniSection("General");
                var effects = new StubSettingsSideEffects();
                var vm = new SettingsViewModel(settings, general, effects);

                settings.WebServerPort = 0;
                vm.Load();
                Assert.Equal(1m, vm.WebServerPort);

                settings.WebServerPort = 99999;
                vm.Load();
                Assert.Equal(65535m, vm.WebServerPort);

                settings.WebServerPort = 8080;
                vm.Load();
                Assert.Equal(8080m, vm.WebServerPort);

                vm.WebServerPort = 0m;
                vm.Save();
                Assert.Equal(1, settings.WebServerPort);

                vm.WebServerPort = 99999m;
                vm.Save();
                Assert.Equal(65535, settings.WebServerPort);

                vm.WebServerPort = 7963m;
                vm.Save();
                Assert.Equal(7963, settings.WebServerPort);
            }
            finally
            {
                AppSettings.ActiveIniFile = prevIni;
                if (File.Exists(tempIni))
                {
                    try { File.Delete(tempIni); }
                    catch (IOException) { }
                }
            }
        }

        [Fact]
        public async Task BulkImportViewModel_DuplicateUserPassLines_QueuesEachAccountOnceForFastLogin()
        {
            var repo = Substitute.For<IAccountRepository>();
            var dialogs = Substitute.For<IDialogService>();
            var host = Substitute.For<IHostUiBridgeService>();

            repo.GetAll().Returns(Array.Empty<Account>());
            repo.LookupUserIdAsync("dupuser", Arg.Any<CancellationToken>())
                .Returns((true, 555001L, (Shiro.Http.RestResponse?)null));

            var vm = new BulkImportViewModel(repo, dialogs, host)
            {
                InputText = "dupuser:pass1\ndupuser:pass2\nDUPUSER:pass3"
            };

            await vm.StartImportAsync();

            Assert.Equal(3, vm.ImportedCount);
            dialogs.Received(1).OpenFastLoginDialog(Arg.Is<Account>(a =>
                a.Username == "dupuser" && a.Password == "pass3" && a.UserID == 555001L));
            dialogs.DidNotReceiveWithAnyArgs().OpenFastLoginDialogBatch(default!);
        }

        [Fact]
        public async Task ClipboardHelper_SetClipboardTextAsync_MarshalsOffUiThreadCleanly()
        {
            bool completedResult = await Task.Run(() => ClipboardHelper.SetClipboardTextAsync("hello"));
            Assert.False(completedResult);

            string repoRoot = FindRepoRoot();
            string src = File.ReadAllText(Path.Combine(repoRoot, "src", "Shiro", "Services", "Windows", "ClipboardHelper.cs"));
            int asyncStart = src.IndexOf("SetClipboardTextAsync(", StringComparison.Ordinal);
            int syncStart = src.IndexOf("SetClipboardTextSafe(", StringComparison.Ordinal);
            Assert.True(asyncStart >= 0 && syncStart > asyncStart);

            string asyncBody = src.Substring(asyncStart, syncStart - asyncStart);
            Assert.Contains("CheckAccess()", asyncBody, StringComparison.Ordinal);
            Assert.Contains("InvokeAsync(", asyncBody, StringComparison.Ordinal);
        }

        private static string FindRepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "Shiro.sln")))
                    return dir.FullName;
                dir = dir.Parent;
            }
            throw new InvalidOperationException("Could not locate Shiro.sln");
        }
    }
}

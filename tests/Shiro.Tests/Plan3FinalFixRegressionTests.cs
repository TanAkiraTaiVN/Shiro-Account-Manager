#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using NSubstitute;
using Shiro.AvaloniaUI.Controls;
using Shiro.AvaloniaUI.Views;
using Shiro.Http;
using Shiro.Services;
using Shiro.Tests.TestInfrastructure;
using Shiro.ViewModels;
using Shiro.ViewModels.Dialogs;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public sealed class Plan3FinalFixRegressionTests : IDisposable
    {
        private const string HeadshotUrlJson = "{\"data\":[{\"targetId\":77777,\"imageUrl\":\"https://rbx.cdn/headshot77777.png\"}]}";
        private const string BatchHitJson = "{\"data\":[{\"requestId\":\"0:tok_123:AvatarHeadshot:48x48:png:regular\",\"imageUrl\":\"https://rbx.cdn/headshot77777.png\"}]}";
        private const string GamesSearchJson = "{\"data\":[{\"id\":\"secret-job-id-777\",\"maxPlayers\":20,\"playing\":3,\"playerTokens\":[\"tok_123\"]}],\"nextPageCursor\":\"\"}";
        private const string ExpectedTemplateForegroundAttr = "Foreground=\"{TemplateBinding Foreground}\"";
        private const string ValidModCookie = "_|WARNING:-DO-NOT-SHARE-THIS.--ModCancelTok";

        private readonly bool _origStreamerEnabled = StreamerModeService.IsEnabled;
        private readonly bool _origAutoDetect = StreamerModeService.AutoDetectEnabled;
        private readonly string? _origStreamerSetting = AppServices.GeneralSettings.Get<string>(AppSettingsKeys.General.StreamerModeEnabled);
        private readonly string? _origHideSetting = AppServices.GeneralSettings.Get<string>(AppSettingsKeys.General.HideUsernames);
        private readonly Action<Action>? _origDispatcher = AppDispatcher.CustomDispatcher;
        private readonly Func<Account, HttpClient>? _origHttpFactory = RobloxAccountSessionClient.TestHttpClientFactory;
        private readonly long _origCachedPlaceId = AppServices.ServerBrowserState.CachedServersPlaceId;

        public void Dispose()
        {
            AccountManager.ResetAccountsForTests(Array.Empty<Account>());
            AppDispatcher.CustomDispatcher = _origDispatcher;
            RobloxAccountSessionClient.TestHttpClientFactory = _origHttpFactory;
            StreamerModeService.AutoDetectEnabled = _origAutoDetect;
            StreamerModeService.IsEnabled = _origStreamerEnabled;
            RestoreGeneralSetting(AppSettingsKeys.General.StreamerModeEnabled, _origStreamerSetting);
            RestoreGeneralSetting(AppSettingsKeys.General.HideUsernames, _origHideSetting);
            StreamerModeService.InitializeForTest();
            AppServices.ServerBrowserState.CachedServersPlaceId = _origCachedPlaceId;
            Application.Current?.Resources.MergedDictionaries.Clear();
        }

        private static void RestoreGeneralSetting(string key, string? previous)
        {
            if (previous is null) AppServices.GeneralSettings.RemoveProperty(key);
            else AppServices.GeneralSettings.Set(key, previous);
        }

        private sealed class TogglePlaceServerBrowserState : IServerBrowserStateService
        {
            private int _placeIdReads;
            public string CurrentPlaceId { get; set; } = "1818";
            public string CurrentJobId { get; set; } = string.Empty;
            public bool ShuffleJobId { get; set; }
            public string? CurrentClientVersion { get; set; }
            public string? WebSocketPassword { get; set; } = "StrongAdminPass123";
            public long CachedServersPlaceId
            {
                get => Interlocked.Increment(ref _placeIdReads) == 1 ? 1818L : 9999L;
                set { }
            }
            public List<ServerData> CachedServers { get; set; } = new() { new ServerData { id = "stale-job-id" } };
            public List<Shiro.Game> RecentGames { get; set; } = new();
            public Task LoadClientVersionAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        }

        [Fact]
        public void LegacyHideUsernames_ActivatesStreamerModeOnInit_AndAlignsAccountViewModelShownUsername()
        {
            StreamerModeService.AutoDetectEnabled = false;
            StreamerModeService.IsEnabled = false;
            var gen = new IniFile()["General"];
            gen.Set(AppSettingsKeys.General.HideUsernames, "true");
            using var settingsScope = AppServices.Override<ISettingsService>(gen);

            StreamerModeService.InitializeForTest();
            Assert.True(StreamerModeService.IsEnabled);
            Assert.True(StreamerModeService.IsActive);

            var acc = new Account { Username = "LegacyUserAlpha", UserID = 123456, Valid = true };
            var vm = new AccountViewModel(acc, settings: gen);
            string expectedMasked = StreamerModeService.MaskUsername("LegacyUserAlpha");
            Assert.Equal(expectedMasked, vm.ShownUsername);

            StreamerModeService.IsEnabled = false;
            gen.Set(AppSettingsKeys.General.HideUsernames, "true");
            Assert.Equal(expectedMasked, vm.ShownUsername);
            Assert.Equal("***", vm.ShownUserId);
        }

        [Fact]
        public void SettingsViewModel_BidirectionalSyncWithStreamerMode_AndDetachesOnClose()
        {
            AppDispatcher.CustomDispatcher = a => a();
            StreamerModeService.AutoDetectEnabled = false;
            StreamerModeService.IsEnabled = false;
            var gen = new IniFile()["General"];
            using var settingsScope = AppServices.Override<ISettingsService>(gen);
            var effects = Substitute.For<ISettingsSideEffects>();
            var vm = new SettingsViewModel(new AppSettings(), gen, effects);
            try
            {
                vm.Load();
                Assert.False(vm.HideUsernames);

                StreamerModeService.IsEnabled = true;
                Assert.True(vm.HideUsernames);
                effects.DidNotReceive().SetStreamerMode(Arg.Any<bool>());

                vm.Save();
                effects.Received(1).SetStreamerMode(true);
                Assert.Equal("true", gen.Get<string>(AppSettingsKeys.General.HideUsernames));
                Assert.Equal("true", gen.Get<string>(AppSettingsKeys.General.StreamerModeEnabled));

                StreamerModeService.IsEnabled = false;
                Assert.False(vm.HideUsernames);

                vm.OnClosing();
                StreamerModeService.IsEnabled = true;
                Assert.False(vm.HideUsernames);
            }
            finally { vm.OnClosing(); }
        }

        [AvaloniaFact]
        public void FastLoginDialog_LiveUpdatesDialogTitleOnStreamerModeChange_AndUnsubscribesOnClose()
        {
            using var settingsScope = AppServices.Override<ISettingsService>(new IniFile()["General"]);
            StreamerModeService.AutoDetectEnabled = false;
            StreamerModeService.IsEnabled = false;
            var dlg = new FastLoginDialog(new Account { Username = "AccAlpha", Password = "pw" }, startInCookieMode: false);
            try
            {
                dlg.Show();
                Dispatcher.UIThread.RunJobs();
                var titleTb = dlg.FindControl<TextBlock>("TxtDialogTitle");
                Assert.NotNull(titleTb);
                Assert.Contains("AccAlpha", titleTb!.Text ?? string.Empty, StringComparison.Ordinal);

                StreamerModeService.IsEnabled = true;
                Dispatcher.UIThread.RunJobs();
                Assert.DoesNotContain("AccAlpha", titleTb.Text ?? string.Empty, StringComparison.Ordinal);
                Assert.Contains("A******a", titleTb.Text ?? string.Empty, StringComparison.Ordinal);

                StreamerModeService.IsEnabled = false;
                Dispatcher.UIThread.RunJobs();
                Assert.Contains("AccAlpha", titleTb.Text ?? string.Empty, StringComparison.Ordinal);

                dlg.Close();
                Dispatcher.UIThread.RunJobs();

                StreamerModeService.IsEnabled = true;
                Dispatcher.UIThread.RunJobs();
                Assert.Contains("AccAlpha", titleTb.Text ?? string.Empty, StringComparison.Ordinal);

                var evtField = typeof(StreamerModeService).GetField("StateChanged", BindingFlags.Static | BindingFlags.NonPublic);
                var del = evtField?.GetValue(null) as Delegate;
                Assert.False(del != null && del.GetInvocationList().Any(d => ReferenceEquals(d.Target, dlg)));
            }
            finally
            {
                dlg.Close();
                Dispatcher.UIThread.RunJobs();
            }
        }

        [Fact]
        public void FastLoginDialog_AxamlModeTabPill_BindsForegroundAndOrdersPointerOverBeforeChecked()
        {
            string axaml = File.ReadAllText(Path.Combine(ArchitectureTests.RepoRoot, "src", "Shiro", "AvaloniaUI", "Views", "FastLoginDialog.axaml"));
            int presenterIdx = axaml.IndexOf("<ContentPresenter", StringComparison.Ordinal);
            Assert.True(presenterIdx >= 0);
            int presenterEnd = axaml.IndexOf("/>", presenterIdx, StringComparison.Ordinal);
            string presenterSlice = axaml.Substring(presenterIdx, presenterEnd - presenterIdx + 2);
            Assert.Contains(ExpectedTemplateForegroundAttr, presenterSlice, StringComparison.Ordinal);

            int hoverIdx = axaml.IndexOf("Style Selector=\"RadioButton.ModeTabPill:pointerover\"", StringComparison.Ordinal);
            int checkedIdx = axaml.IndexOf("Style Selector=\"RadioButton.ModeTabPill:checked\"", StringComparison.Ordinal);
            Assert.True(hoverIdx >= 0 && checkedIdx >= 0 && hoverIdx < checkedIdx);
        }

        [Fact]
        public void MainViewModel_BatchLaunchBanner_MasksUsernameUnderStreamerMode()
        {
            StreamerModeService.AutoDetectEnabled = false;
            StreamerModeService.IsEnabled = true;
            using var mainVm = new MainViewModel(Substitute.For<IAppLogger>(), new IniFile()["General"]);
            mainVm.UpdateBatchLaunchBanner(new LaunchProgress
            {
                Index = 1,
                Total = 3,
                Account = new Account { Username = "BatchLeakUser" },
                Status = LaunchOutcome.Pending
            });
            Assert.DoesNotContain("BatchLeakUser", mainVm.BatchLaunchTitleText, StringComparison.Ordinal);
            Assert.Contains(StreamerModeService.MaskUsername("BatchLeakUser"), mainVm.BatchLaunchTitleText, StringComparison.Ordinal);
        }

        [Fact]
        public async Task BulkImportViewModel_SavingAccountStatus_MasksUsernameUnderStreamerMode()
        {
            AppDispatcher.CustomDispatcher = a => a();
            StreamerModeService.AutoDetectEnabled = false;
            StreamerModeService.IsEnabled = true;
            var repo = Substitute.For<IAccountRepository>();
            repo.GetAll().Returns(new List<Account>());
            repo.LookupUserIdAsync("ImportLeakUser", Arg.Any<CancellationToken>())
                .Returns(Task.FromResult((true, 991122L, (RestResponse?)null)));

            var vm = new BulkImportViewModel(repo, Substitute.For<IDialogService>(), Substitute.For<IHostUiBridgeService>(), 1, Substitute.For<IAppLogger>());
            var statuses = new List<string>();
            vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(BulkImportViewModel.StatusText)) statuses.Add(vm.StatusText); };
            vm.InputText = "ImportLeakUser:SecretPass123";
            await vm.StartImportAsync();

            Assert.Contains(statuses, s => s.Contains(StreamerModeService.MaskUsername("ImportLeakUser"), StringComparison.Ordinal));
            Assert.DoesNotContain(statuses, s => s.Contains("ImportLeakUser", StringComparison.Ordinal));
        }

        [Fact]
        public async Task AccountWorkflowService_MasksStatusAndPrompts_WhileClipboardCopyRemainsRaw()
        {
            StreamerModeService.AutoDetectEnabled = false;
            StreamerModeService.IsEnabled = true;
            var promptMsgs = new List<string>();
            var dialogs = Substitute.For<IDialogService>();
            dialogs.ShowInputPromptAsync(Arg.Do<string>(m => promptMsgs.Add(m)), Arg.Any<string>(), Arg.Any<string>()).Returns(Task.FromResult("NewAliasSecret"));
            using var dialogScope = AppServices.Override<IDialogService>(dialogs);

            var acc = new Account { Username = "WorkflowSecretUser", Alias = "OldAlias", UserID = 88776655, Password = "pw", SecurityToken = "tok-raw" };
            var accVm = new AccountViewModel(acc);
            var svc = new AccountWorkflowService(Substitute.For<IAppLogger>());
            var statuses = new List<string>();

            await svc.SetAliasAsync(accVm, statuses.Add);
            svc.CopyUsername(accVm, statuses.Add);
            svc.CopyUserPass(accVm, statuses.Add);
            svc.CopyUserId(accVm, statuses.Add);
            svc.CopyProfileUrl(accVm, statuses.Add);
            svc.CopySecurityToken(accVm, statuses.Add);

            Assert.DoesNotContain(promptMsgs, m => m.Contains("WorkflowSecretUser", StringComparison.Ordinal));
            Assert.DoesNotContain(statuses, s => s.Contains("WorkflowSecretUser", StringComparison.Ordinal));
            Assert.DoesNotContain(statuses, s => s.Contains("NewAliasSecret", StringComparison.Ordinal));
            Assert.DoesNotContain(statuses, s => s.Contains("88776655", StringComparison.Ordinal));
            Assert.Contains(statuses, s => s.Contains(StreamerModeService.MaskUsername("WorkflowSecretUser"), StringComparison.Ordinal));
            Assert.Contains(statuses, s => s.Contains(StreamerModeService.MaskUsername("NewAliasSecret"), StringComparison.Ordinal));
            Assert.Contains(statuses, s => s.Contains("***", StringComparison.Ordinal));

            string wfSrc = File.ReadAllText(Path.Combine(ArchitectureTests.RepoRoot, "src", "Shiro", "Services", "Storage", "AccountWorkflowService.cs"));
            Assert.Contains("ClipboardHelper.SetClipboardTextSafe(acc.Model.Username)", wfSrc, StringComparison.Ordinal);
            Assert.Contains("ClipboardHelper.SetClipboardTextSafe(acc.Model.UserID.ToString())", wfSrc, StringComparison.Ordinal);
            Assert.Contains("ClipboardHelper.SetClipboardTextSafe(acc.Model.SecurityToken)", wfSrc, StringComparison.Ordinal);
        }

        [Fact]
        public async Task ServerListViewModel_PlayerSearch_MasksMatchedJobIdUnderStreamerMode()
        {
            AppDispatcher.CustomDispatcher = a => a();
            StreamerModeService.AutoDetectEnabled = false;
            StreamerModeService.IsEnabled = true;

            var mockRepo = Substitute.For<IAccountRepository>();
            mockRepo.LookupUserIdAsync("TargetPlayer", Arg.Any<CancellationToken>())
                .Returns(Task.FromResult((true, 77777L, (RestResponse?)null)));
            using var repoScope = AppServices.Override<IAccountRepository>(mockRepo);

            var mockApi = Substitute.For<IRobloxApiClient>();
            var thumbHandler = new FakeHttpHandler().Enqueue(HttpStatusCode.OK, HeadshotUrlJson).Enqueue(HttpStatusCode.OK, BatchHitJson);
            var gamesHandler = new FakeHttpHandler().Enqueue(HttpStatusCode.OK, GamesSearchJson);
            mockApi.Thumbnails.Returns(new RestClient(new HttpClient(thumbHandler), "https://thumbnails.roblox.com/"));
            mockApi.Games.Returns(new RestClient(new HttpClient(gamesHandler), "https://games.roblox.com/"));
            using var apiScope = AppServices.Override<IRobloxApiClient>(mockApi);

            using var mainVm = new MainViewModel();
            var vm = mainVm.ServerListViewModel;
            vm.SearchPlayerUsername = "TargetPlayer";
            vm.PlaceIdInput = "1818";
            await ((AsyncRelayCommand)vm.SearchPlayerCommand).ExecuteAsync(null);

            Assert.DoesNotContain("secret-job-id-777", vm.PlayerSearchStatus, StringComparison.Ordinal);
            Assert.DoesNotContain("secret-job-id-777", vm.LoadingStatus, StringComparison.Ordinal);
            Assert.Contains("***-***", vm.PlayerSearchStatus, StringComparison.Ordinal);
            Assert.Contains("***-***", vm.LoadingStatus, StringComparison.Ordinal);
        }

        [Fact]
        public async Task RobloxModerationService_WhenCallerCancelsDuringInitialHomeProbe_PropagatesOperationCanceledException()
        {
            using var callerCts = new CancellationTokenSource();
            int homeCalls = 0;
            var handler = new FakeHttpHandler((req, _) =>
            {
                string url = req.RequestUri!.AbsoluteUri;
                if (url.Contains("usermoderation.roblox.com", StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
                if (url.Contains("www.roblox.com/home", StringComparison.OrdinalIgnoreCase))
                {
                    Interlocked.Increment(ref homeCalls);
                    callerCts.Cancel();
                    throw new OperationCanceledException(callerCts.Token);
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            });

            RobloxAccountSessionClient.TestHttpClientFactory = _ => new HttpClient(handler);
            var account = new Account { Username = "HomeCancelUser", UserID = 7791, SecurityToken = ValidModCookie };
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                new RobloxModerationService().CheckModerationStatusAsync(account, ValidModCookie, httpClientOverride: null, callerCts.Token, dispatchAlert: false));
            Assert.Equal(1, homeCalls);
        }

        [Fact]
        public void WebView2Control_CleanupOrphanedProfiles_PreservesStaleNonGuidShiroDirAndRemovesStaleOwnedGuidDir()
        {
            string tempRoot = Path.Combine(Path.GetTempPath(), "ShiroWvKeep_" + Guid.NewGuid().ToString("N"));
            string profilesRoot = Path.Combine(tempRoot, "EBWebView");
            string staleOwnedGuidDir = Path.Combine(profilesRoot, "Shiro_" + Guid.NewGuid().ToString("N"));
            string staleCustomKeepDir = Path.Combine(profilesRoot, "Shiro_custom_keep");
            try
            {
                Directory.CreateDirectory(staleOwnedGuidDir);
                Directory.CreateDirectory(staleCustomKeepDir);
                DateTime staleUtc = DateTime.UtcNow.AddHours(-2);
                Directory.SetLastWriteTimeUtc(staleOwnedGuidDir, staleUtc);
                Directory.SetLastWriteTimeUtc(staleCustomKeepDir, staleUtc);

                WebView2Control.CleanupOrphanedProfiles(tempRoot);
                Assert.False(Directory.Exists(staleOwnedGuidDir));
                Assert.True(Directory.Exists(staleCustomKeepDir));
            }
            finally
            {
                if (Directory.Exists(tempRoot))
                {
                    try { Directory.Delete(tempRoot, true); }
                    catch (IOException) { }
                }
            }
        }

        [Fact]
        public async Task AccountApiDispatcher_SetRecommendedServer_Returns400OnCachedServersPlaceIdMismatchEvenWhenPopulated()
        {
            var mgr = AccountManager.Instance ?? new AccountManager();
            var acc = new Account { Username = "RecToctouUser", UserID = 4455, Valid = true, SecurityToken = ValidModCookie };
            AccountManager.ResetAccountsForTests(new[] { acc });

            int csrfCalls = 0;
            RobloxAccountSessionClient.TestHttpClientFactory = _ => new HttpClient(new FakeHttpHandler(_ =>
            {
                Interlocked.Increment(ref csrfCalls);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }));

            using var stateScope = AppServices.Override<IServerBrowserStateService>(new TogglePlaceServerBrowserState());
            bool prevAllowEdit = AppSettings.WebServer.Get<bool>("AllowAccountEditing");
            AppSettings.WebServer.Set("AllowAccountEditing", "true");
            try
            {
                int status = 200;
                var q = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Password"] = "StrongAdminPass123",
                    ["Account"] = "RecToctouUser",
                    ["PlaceId"] = "1818"
                };
                var req = new AccountManager.ApiRequestContext
                {
                    AbsolutePath = "/SetRecommendedServer",
                    IsV2 = true,
                    IsLocal = true,
                    Host = "localhost",
                    Origin = "",
                    Body = string.Empty,
                    GetQuery = k => q.TryGetValue(k, out var val) ? val : null,
                    GetHeader = _ => null,
                    SetStatusCode = s => status = s
                };

                string resp = await mgr.ProcessApiRequestAsync(req);
                Assert.Equal(400, status);
                Assert.Equal(0, csrfCalls);
                Assert.Contains("Cached servers do not match", resp, StringComparison.Ordinal);
            }
            finally
            {
                AppSettings.WebServer.Set("AllowAccountEditing", prevAllowEdit ? "true" : "false");
            }
        }
    }
}

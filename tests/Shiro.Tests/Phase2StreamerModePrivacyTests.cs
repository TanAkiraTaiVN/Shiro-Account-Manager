#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using NSubstitute;
using Shiro.Services;
using Shiro.Tests.TestInfrastructure;
using Shiro.ViewModels;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public sealed class Phase2StreamerModePrivacyTests : IDisposable
    {
        private readonly bool _origStreamerEnabled = StreamerModeService.IsEnabled;
        private readonly bool _origAutoDetect = StreamerModeService.AutoDetectEnabled;
        private readonly string? _origStreamerSetting = AppServices.GeneralSettings.Get<string>(AppSettingsKeys.General.StreamerModeEnabled);
        private readonly string? _origHideUsernamesSetting = AppServices.GeneralSettings.Get<string>(AppSettingsKeys.General.HideUsernames);
        private readonly string? _origStreamerHideUsernames = AppServices.GeneralSettings.Get<string>(AppSettingsKeys.General.StreamerModeHideUsernames);
        private readonly string? _origStreamerHideRobux = AppServices.GeneralSettings.Get<string>(AppSettingsKeys.General.StreamerModeHideRobux);
        private readonly Action<Action>? _origDispatcher = AppDispatcher.CustomDispatcher;
        private readonly Func<Account, HttpClient>? _origHttpFactory = RobloxAccountSessionClient.TestHttpClientFactory;
        private readonly Func<IReadOnlyList<int>>? _origPipesOverride = DiscordRpcService.TestAvailablePipesOverride;

        public void Dispose()
        {
            AppDispatcher.CustomDispatcher = _origDispatcher;
            RobloxAccountSessionClient.TestHttpClientFactory = _origHttpFactory;
            DiscordRpcService.TestAvailablePipesOverride = _origPipesOverride;
            StreamerModeService.AutoDetectEnabled = _origAutoDetect;
            StreamerModeService.IsEnabled = _origStreamerEnabled;
            RestoreSetting(AppSettingsKeys.General.StreamerModeEnabled, _origStreamerSetting);
            RestoreSetting(AppSettingsKeys.General.HideUsernames, _origHideUsernamesSetting);
            RestoreSetting(AppSettingsKeys.General.StreamerModeHideUsernames, _origStreamerHideUsernames);
            RestoreSetting(AppSettingsKeys.General.StreamerModeHideRobux, _origStreamerHideRobux);
            StreamerModeService.InitializeForTest();
            ToastService.ClearAll();
        }

        private static void RestoreSetting(string key, string? previous)
        {
            if (previous is null)
                AppServices.GeneralSettings.RemoveProperty(key);
            else
                AppServices.GeneralSettings.Set(key, previous);
        }

        [Fact]
        public void DiscordRpcService_UpdatePresence_InStreamerMode_HidesGameNameCountsAndJoinUrl()
        {
            var logger = Substitute.For<IAppLogger>();
            var settings = Substitute.For<ISettingsService>();
            settings.Get<bool>(AppSettingsKeys.General.DiscordRpcEnabled, true).Returns(true);
            settings.Get<bool>(AppSettingsKeys.General.DiscordRpcJoinCurrentServer, true).Returns(true);
            settings.Get<bool>(AppSettingsKeys.General.DiscordRpcShowGame, true).Returns(true);

            DiscordRpcService.TestAvailablePipesOverride = () => Array.Empty<int>();
            StreamerModeService.IsEnabled = true;

            try
            {
                using var service = new DiscordRpcService(logger, settings);
                service.UpdatePresence("SecretBloxGame", 99887766, "secret-job-uuid", totalAccounts: 17, activeInGameAccounts: 4);

                FieldInfo? lastActivityField = typeof(DiscordRpcService).GetField("_lastActivity", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(lastActivityField);
                var activity = lastActivityField!.GetValue(service) as DiscordActivityItem;
                Assert.NotNull(activity);

                Assert.Equal("Shiro Account Manager", activity!.Details);
                Assert.Equal(LocalizationService.Get("DiscordRpc.StreamerMode"), activity.State);

                string payload = Encoding.UTF8.GetString(DiscordRpcService.BuildSetActivityPayload(activity));
                Assert.DoesNotContain("SecretBloxGame", payload, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("99887766", payload, StringComparison.Ordinal);
                Assert.DoesNotContain("secret-job-uuid", payload, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(LocalizationService.Format("DiscordRpc.ManagingAccounts", 17), payload, StringComparison.Ordinal);
                Assert.DoesNotContain(LocalizationService.Format("DiscordRpc.ClientsOnline", 4), payload, StringComparison.Ordinal);
                Assert.DoesNotContain(" 17 ", payload, StringComparison.Ordinal);
                Assert.DoesNotContain("buttons", payload, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                StreamerModeService.IsEnabled = _origStreamerEnabled;
                DiscordRpcService.TestAvailablePipesOverride = _origPipesOverride;
            }
        }

        [Fact]
        public async Task AccountCollectionCommandActions_InStreamerMode_MasksUsernameAndRobuxBalance()
        {
            AppDispatcher.CustomDispatcher = static action => action();
            StreamerModeService.IsEnabled = true;
            AppServices.GeneralSettings.Set(AppSettingsKeys.General.StreamerModeHideUsernames, "true");
            AppServices.GeneralSettings.Set(AppSettingsKeys.General.StreamerModeHideRobux, "true");

            RobloxAccountSessionClient.TestHttpClientFactory = _ => new HttpClient(new FakeHttpHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"robux\": 98765}", Encoding.UTF8, "application/json")
                }));

            try
            {
                var account = new Account
                {
                    Username = "SecretStreamerUser",
                    UserID = 556677,
                    Valid = true,
                    SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.--PrivacyToken"
                };
                var accountVm = new AccountViewModel(account, Substitute.For<IAppLogger>(), AppServices.GeneralSettings);
                var repo = Substitute.For<IAccountRepository>();
                repo.SaveAsync(true).Returns(Task.CompletedTask);

                var capturedStatuses = new List<string>();
                bool completed = false;

                await AccountCollectionCommandActions.CheckRobuxSingleAsync(
                    accountVm,
                    repo,
                    s => capturedStatuses.Add(s),
                    () => completed = true);

                Assert.True(completed);
                Assert.True(capturedStatuses.Count >= 2);
                foreach (string status in capturedStatuses)
                {
                    Assert.DoesNotContain("SecretStreamerUser", status, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain("98,765", status, StringComparison.Ordinal);
                    Assert.DoesNotContain("98765", status, StringComparison.Ordinal);
                }

                string maskedName = StreamerModeService.MaskUsername("SecretStreamerUser");
                Assert.Contains(maskedName, capturedStatuses[^1], StringComparison.Ordinal);
                Assert.Contains("***", capturedStatuses[^1], StringComparison.Ordinal);
            }
            finally
            {
                StreamerModeService.IsEnabled = _origStreamerEnabled;
                RobloxAccountSessionClient.TestHttpClientFactory = _origHttpFactory;
                AppDispatcher.CustomDispatcher = _origDispatcher;
            }
        }

        [Fact]
        public void ToolsStatusViewModel_FocusAccountWindow_InStreamerMode_MasksUsernameInToastAndStatus()
        {
            AppDispatcher.CustomDispatcher = static action => action();
            ToastService.ClearAll();
            StreamerModeService.IsEnabled = true;
            AppServices.GeneralSettings.Set(AppSettingsKeys.General.StreamerModeHideUsernames, "true");

            try
            {
                using var vm = new ToolsStatusViewModel(Substitute.For<IAppLogger>(), AppServices.GeneralSettings);
                var account = new Account
                {
                    Username = "SecretFocusUser",
                    UserID = 112233,
                    Valid = true
                };
                var accountVm = new AccountViewModel(account, Substitute.For<IAppLogger>(), AppServices.GeneralSettings);

                vm.FocusAccountWindowCommand.Execute(accountVm);

                Assert.DoesNotContain("SecretFocusUser", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
                ToastItem[] toasts = ToastService.ActiveToasts.ToArray();
                Assert.NotEmpty(toasts);
                foreach (ToastItem toast in toasts)
                {
                    Assert.DoesNotContain("SecretFocusUser", toast.Title, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain("SecretFocusUser", toast.Message, StringComparison.OrdinalIgnoreCase);
                }

                string maskedName = StreamerModeService.MaskUsername("SecretFocusUser");
                Assert.Contains(toasts, t => t.Title.Contains(maskedName, StringComparison.Ordinal));
            }
            finally
            {
                ToastService.ClearAll();
                StreamerModeService.IsEnabled = _origStreamerEnabled;
                AppDispatcher.CustomDispatcher = _origDispatcher;
            }
        }

        [Fact]
        public void ServerBrowserTabViewModel_InStreamerMode_MasksJobIdInStatusMessages()
        {
            AppDispatcher.CustomDispatcher = static action => action();
            StreamerModeService.IsEnabled = true;

            try
            {
                var repo = Substitute.For<IAccountRepository>();
                repo.GetAll().Returns(Array.Empty<Account>());
                using var mainVm = new MainViewModel(Substitute.For<IAppLogger>(), AppServices.GeneralSettings, repo);

                const string rawJobId = "deadbeef-1234-5678-90ab-cdef12345678";
                var serverItem = new ServerItemViewModel
                {
                    JobId = rawJobId,
                    Type = "Public"
                };

                mainVm.ServerListViewModel.BrowserTab.SelectServerJobIdCommand.Execute(serverItem);

                Assert.Contains("***-***", mainVm.StatusMessage, StringComparison.Ordinal);
                Assert.DoesNotContain(rawJobId, mainVm.StatusMessage, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("***-***", mainVm.ServerListViewModel.LoadingStatus, StringComparison.Ordinal);
                Assert.DoesNotContain(rawJobId, mainVm.ServerListViewModel.LoadingStatus, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(rawJobId, mainVm.JobId);

                mainVm.ServerListViewModel.BrowserTab.CopyJobIdCommand.Execute(serverItem);

                Assert.Contains("***-***", mainVm.ServerListViewModel.LoadingStatus, StringComparison.Ordinal);
                Assert.DoesNotContain(rawJobId, mainVm.ServerListViewModel.LoadingStatus, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                StreamerModeService.IsEnabled = _origStreamerEnabled;
                AppDispatcher.CustomDispatcher = _origDispatcher;
            }
        }
    }
}

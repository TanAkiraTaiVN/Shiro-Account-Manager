#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NSubstitute;
using Shiro.AvaloniaUI.Views;
using Shiro.Services;
using Shiro.ViewModels.Dialogs;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public sealed class AuditUiAndAuthRegressionTests
    {
        [Fact]
        public void Bug2_FastLoginDialog_DoesNotCommitBeforeMismatchCheck_AndUnsubscribesWebViewHandlers()
        {
            string repoRoot = ArchitectureTests.RepoRoot;
            string captureCode = File.ReadAllText(Path.Combine(repoRoot, "src", "Shiro", "AvaloniaUI", "Views", "FastLoginDialog.SessionCapture.cs"));
            string closingCode = File.ReadAllText(Path.Combine(repoRoot, "src", "Shiro", "AvaloniaUI", "Views", "FastLoginDialog.axaml.cs"));

            // Session capture must resolve/check username mismatch via coordinator before committing into repository
            Assert.DoesNotContain("AddOrUpdateWithHbaAsync(", captureCode, StringComparison.Ordinal);

            int mismatchIdx = captureCode.IndexOf("case FastLoginCaptureStatus.UsernameMismatch:", StringComparison.Ordinal);
            Assert.True(mismatchIdx >= 0);
            string mismatchBranch = captureCode.Substring(mismatchIdx, Math.Min(600, captureCode.Length - mismatchIdx));
            Assert.Contains("core.WebResourceRequested -= OnWebResourceRequested", mismatchBranch, StringComparison.Ordinal);
            Assert.Contains("core.WebMessageReceived -= OnWebMessageReceived", mismatchBranch, StringComparison.Ordinal);

            int closingIdx = closingCode.IndexOf("OnClosing(", StringComparison.Ordinal);
            Assert.True(closingIdx >= 0);
            string closingBody = closingCode.Substring(closingIdx);
            Assert.Contains("core.WebMessageReceived -= OnWebMessageReceived", closingBody, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Bug2_FastLoginCoordinator_ResolveAndCommit_UsernameMismatchNeverCommitsOrSavesToRepository()
        {
            var repo = Substitute.For<IAccountRepository>();
            var wrongCandidate = new Account
            {
                Username = "ActualWrongUserB",
                UserID = 7070,
                Valid = true,
                SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.--tokB"
            };

            var res = await FastLoginCoordinator.ResolveAndCommitCapturedSessionAsync(
                cookieValue: "_|WARNING:-DO-NOT-SHARE-THIS.--tokB",
                expectedUsername: "ExpectedUserA",
                password: "SecretPasswordForA",
                alias: "AliasA",
                group: "GroupA",
                accountJson: null,
                hbaCapture: null,
                queueCount: 1,
                currentQueueAccount: new Account { Username = "ExpectedUserA" },
                detectDomIssueAsync: null,
                repository: repo,
                createAccountAsync: (_, _, _, _) => Task.FromResult(wrongCandidate));

            Assert.Equal(FastLoginCaptureStatus.UsernameMismatch, res.Status);
            repo.DidNotReceiveWithAnyArgs().RegisterValidated(default!, default!, default!, default);
            await repo.DidNotReceiveWithAnyArgs().AddOrUpdateWithHbaAsync(default!, default!, default, default, default, default);
            await repo.DidNotReceiveWithAnyArgs().SaveAsync(default);
        }

        [Fact]
        public void Bug5_SettingsViewModel_ProtectsWebhookAndTelegramSecretsWithDpapi()
        {
            var ini = new IniFile();
            var general = ini["General"];
            const string initialWebhook = "https://discord.com/api/webhooks/999/initial-secret";
            const string initialBotToken = "777888:ABC-INITIAL-BOT-TOKEN";
            general.Set(AppSettingsKeys.General.DiscordWebhookUrl, CryptoHelper.ProtectSecretString(initialWebhook));
            general.Set(AppSettingsKeys.General.TelegramBotToken, CryptoHelper.ProtectSecretString(initialBotToken));

            using var bridgeScope = AppServices.Override<IHostUiBridgeService>(new HostUiBridgeService());
            var vm = new SettingsViewModel(
                new AppSettings(),
                general,
                Substitute.For<ISettingsSideEffects>(),
                Substitute.For<IDialogService>(),
                TimeSpan.FromMilliseconds(10),
                Substitute.For<IAppLogger>());

            vm.Load();
            Assert.Equal(initialWebhook, vm.DiscordWebhookUrl);
            Assert.Equal(initialBotToken, vm.TelegramBotToken);

            const string updatedWebhook = "https://discord.com/api/webhooks/111/updated-secret";
            const string updatedBotToken = "999000:XYZ-UPDATED-TOKEN";
            vm.DiscordWebhookUrl = updatedWebhook;
            vm.TelegramBotToken = updatedBotToken;
            vm.Save();

            string? savedRawWebhook = general.Get(AppSettingsKeys.General.DiscordWebhookUrl);
            string? savedRawToken = general.Get(AppSettingsKeys.General.TelegramBotToken);
            Assert.StartsWith("dpapi:", savedRawWebhook, StringComparison.Ordinal);
            Assert.StartsWith("dpapi:", savedRawToken, StringComparison.Ordinal);
            Assert.Equal(updatedWebhook, CryptoHelper.UnprotectSecretString(savedRawWebhook!));
            Assert.Equal(updatedBotToken, CryptoHelper.UnprotectSecretString(savedRawToken!));
        }

        [Fact]
        public void Bug6_SettingsViewModel_Save_AppliesDiscordRpcChangesLive()
        {
            var ini = new IniFile();
            var general = ini["General"];
            general.Set(AppSettingsKeys.General.DiscordRpcEnabled, "false");
            general.Set(AppSettingsKeys.General.DiscordRpcClientId, "1301012345678901234");

            var effects = Substitute.For<ISettingsSideEffects>();
            using var bridgeScope = AppServices.Override<IHostUiBridgeService>(new HostUiBridgeService());
            var vm = new SettingsViewModel(
                new AppSettings(),
                general,
                effects,
                Substitute.For<IDialogService>(),
                TimeSpan.FromMilliseconds(10),
                Substitute.For<IAppLogger>());
            vm.Load();

            vm.DiscordRpcEnabled = true;
            vm.DiscordRpcClientId = "999888777666555444";
            vm.Save();
            effects.Received(1).ApplyDiscordRpcSettings(true, true);

            using var rpc = new DiscordRpcService(Substitute.For<IAppLogger>(), general);
            var ctsField = typeof(DiscordRpcService).GetField("_cts", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            rpc.ApplyConfigurationChanged(enabled: true, reconnect: true);
            bool startedWorker = ctsField?.GetValue(rpc) != null;
            Assert.True(startedWorker);

            rpc.ApplyConfigurationChanged(enabled: false, reconnect: false);
            bool stoppedWorker = ctsField?.GetValue(rpc) == null;
            Assert.True(stoppedWorker);
            Assert.False(rpc.IsEnabled);
        }

        [Fact]
        public void Bug7_AllAxamlDynamicAndStaticResources_ExistInEnAndViDictionariesCaseSensitively()
        {
            string repoRoot = ArchitectureTests.RepoRoot;
            var enRaw = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(
                File.ReadAllText(Path.Combine(repoRoot, "src", "Shiro", "Resources", "Strings", "en.json")))!;
            var viRaw = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(
                File.ReadAllText(Path.Combine(repoRoot, "src", "Shiro", "Resources", "Strings", "vi.json")))!;

            var enDict = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var kvp in enRaw)
            {
                enDict[kvp.Key] = kvp.Value;
                enDict["Loc_" + kvp.Key] = kvp.Value;
            }
            var viDict = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var kvp in viRaw)
            {
                viDict[kvp.Key] = kvp.Value;
                viDict["Loc_" + kvp.Key] = kvp.Value;
            }

            var axamlFiles = Directory.GetFiles(Path.Combine(repoRoot, "src", "Shiro"), "*.axaml", SearchOption.AllDirectories)
                .Where(f => !f.Replace('\\', '/').Contains("/bin/") && !f.Replace('\\', '/').Contains("/obj/"));

            var regex = new Regex(@"\x7B(?:Dynamic|Static)Resource\s+([^\x7D\s,]+)\x7D");
            var missing = new List<string>();
            foreach (string file in axamlFiles)
            {
                string content = File.ReadAllText(file);
                foreach (Match m in regex.Matches(content))
                {
                    string key = m.Groups[1].Value;
                    bool caseInsensitiveMatchEn = enDict.Keys.Any(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
                    if (!caseInsensitiveMatchEn)
                        continue; // Non-localization brush/theme keys

                    if (!enDict.ContainsKey(key))
                        missing.Add($"{Path.GetFileName(file)}: '{key}' missing in en (case-sensitive)");
                    if (!viDict.ContainsKey(key))
                        missing.Add($"{Path.GetFileName(file)}: '{key}' missing in vi (case-sensitive)");
                }
            }

            Assert.Empty(missing);
        }

        [AvaloniaFact]
        public void Bug7_And_Bug8_SettingsView_RendersFpsLabels_AndReloadsWhenBecomingVisible()
        {
            var prevLang = LocalizationService.CurrentLanguage;
            var prevPotato = AppServices.Settings.EnablePotatoMode;
            Window? window = null;
            try
            {
                LocalizationService.SetLanguage(AppLanguage.English, saveSetting: false);
                var view = new SettingsView { IsVisible = false };
                window = new Window { Width = 900, Height = 700, Content = view };
                window.Show();
                Dispatcher.UIThread.RunJobs();

                // Simulate Dashboard changing PotatoMode while SettingsView is hidden
                bool flippedPotato = !view.ViewModel.PotatoMode;
                AppServices.Settings.EnablePotatoMode = flippedPotato;

                view.IsVisible = true;
                Dispatcher.UIThread.RunJobs();

                Assert.Equal(flippedPotato, view.ViewModel.PotatoMode);

                var texts = view.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? string.Empty).ToList();
                Assert.Contains("Unlock FPS Limit", texts);
                Assert.Contains("Target FPS", texts);
            }
            finally
            {
                window?.Close();
                AppServices.Settings.EnablePotatoMode = prevPotato;
                LocalizationService.SetLanguage(prevLang, saveSetting: false);
                Avalonia.Application.Current?.Resources.MergedDictionaries.Clear();
            }
        }

        [AvaloniaFact]
        public async Task Bug10_ProxyManagerViewModel_TestAllAsync_UpdatesUiPropertiesOnUiThreadAfterPostedLatency()
        {
            var store = Substitute.For<IProxyStore>();
            var proxy = new ProxyItem { Protocol = "http", Host = "127.0.0.1", Port = 8080, LatencyMs = -1 };
            store.Load().Returns(new List<ProxyItem> { proxy });
            store.TestAllAsync(Arg.Any<IEnumerable<ProxyItem>>(), Arg.Any<CancellationToken>())
                .Returns(async ci =>
                {
                    await Task.Run(async () =>
                    {
                        await Task.Delay(25).ConfigureAwait(false);
                        AppDispatcher.Post(() =>
                        {
                            proxy.LatencyMs = 42;
                            proxy.IsTesting = false;
                        });
                    }).ConfigureAwait(false);
                });

            var vm = new ProxyManagerViewModel(store);
            var threadViolations = new List<string>();
            vm.PropertyChanged += (_, e) =>
            {
                if ((e.PropertyName == nameof(vm.StatusText) || e.PropertyName == nameof(vm.IsBusy)) &&
                    !Dispatcher.UIThread.CheckAccess())
                {
                    threadViolations.Add(e.PropertyName ?? "(null)");
                }
            };

            await vm.TestAllAsync();
            Dispatcher.UIThread.RunJobs();

            Assert.Empty(threadViolations);
            Assert.Equal(LocalizationService.Format("ProxyManager.CheckSummaryResult", 1, 1, 0), vm.StatusText);
            Assert.False(vm.IsBusy);
        }
    }
}

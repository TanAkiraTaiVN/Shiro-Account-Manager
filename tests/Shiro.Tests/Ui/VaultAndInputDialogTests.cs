#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Logging;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using LucideAvalonia;
using LucideAvalonia.Enum;
using NSubstitute;
using Shiro.AvaloniaUI.Views;
using Shiro.Services;
using Shiro.ViewModels.Dialogs;
using Xunit;

namespace Shiro.Tests.Ui
{
    [Collection("SerialGlobalState")]
    public sealed class VaultAndInputDialogTests : IDisposable
    {
        private readonly ILogSink? _prevSink = Logger.Sink;
        private readonly AppLanguage _origLanguage = LocalizationService.CurrentLanguage;

        public void Dispose()
        {
            Logger.Sink = _prevSink;
            LocalizationService.SetLanguage(_origLanguage, saveSetting: false);
        }

        private static void EnsureBaseThemeTokens()
        {
            var app = Application.Current;
            if (app?.Resources == null) return;
            if (!app.Resources.TryGetResource("RadiusOuter16", null, out _))
            {
                var tokens = (ResourceDictionary)AvaloniaXamlLoader.Load(
                    new Uri("avares://Shiro/AvaloniaUI/Styles/ThemeTokens.axaml"));
                app.Resources.MergedDictionaries.Add(tokens);
            }
        }

        private static ISettingsService CreateInMemorySettings(Dictionary<string, string?> store)
        {
            var mock = Substitute.For<ISettingsService>();
            mock.Get(Arg.Any<string>()).Returns(ci => store.TryGetValue(ci.Arg<string>(), out var v) ? v : null);
            mock.Get(Arg.Any<string>(), Arg.Any<string>()).Returns(ci =>
                store.TryGetValue(ci.ArgAt<string>(0), out var v) && v != null ? v : ci.ArgAt<string>(1));
            mock.Get(Arg.Any<string>(), Arg.Any<bool>()).Returns(ci =>
                store.TryGetValue(ci.ArgAt<string>(0), out var v) && bool.TryParse(v, out var b) ? b : ci.ArgAt<bool>(1));
            mock.Get(Arg.Any<string>(), Arg.Any<int>()).Returns(ci =>
                store.TryGetValue(ci.ArgAt<string>(0), out var v) && int.TryParse(v, out var n) ? n : ci.ArgAt<int>(1));
            mock.Exists(Arg.Any<string>()).Returns(ci => store.ContainsKey(ci.Arg<string>()));
            return mock;
        }

        [AvaloniaFact]
        public void InputDialog_SecretAndConfirmationModes_ToggleRevealAndValidateMismatch()
        {
            EnsureBaseThemeTokens();
            var sink = new BindingErrorSink();
            Logger.Sink = sink;

            InputDialog? plainDlg = null;
            InputDialog? secretDlg = null;
            InputDialog? confirmDlg = null;
            try
            {
                plainDlg = InputDialog.CreateDialogInstance("Enter name", "Group", string.Empty, big: false, isSecret: false, requireConfirmation: false);
                plainDlg.Show();
                Dispatcher.UIThread.RunJobs();

                Assert.Equal(nameof(LucideIconNames.Pencil), plainDlg.FindControl<Lucide>("IconLucide")!.Icon?.ToString());
                Assert.False(plainDlg.FindControl<Button>("BtnToggleReveal")!.IsVisible);
                Assert.False(plainDlg.FindControl<StackPanel>("PnlConfirmPassword")!.IsVisible);

                secretDlg = InputDialog.CreateDialogInstance("Enter passphrase", "Import Vault", string.Empty, big: false, isSecret: true, requireConfirmation: false);
                secretDlg.Show();
                Dispatcher.UIThread.RunJobs();

                var secretInput = secretDlg.FindControl<TextBox>("TxtInput")!;
                var btnReveal = secretDlg.FindControl<Button>("BtnToggleReveal")!;
                var iconReveal = secretDlg.FindControl<Lucide>("IconToggleReveal")!;
                Assert.Equal(LucideIconNames.Lock, secretDlg.FindControl<Lucide>("IconLucide")!.Icon);
                Assert.Equal('•', secretInput.PasswordChar);
                Assert.True(btnReveal.IsVisible);
                Assert.Equal(nameof(LucideIconNames.Eye), iconReveal.Icon?.ToString());

                btnReveal.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Dispatcher.UIThread.RunJobs();
                Assert.Equal('\0', secretInput.PasswordChar);
                Assert.Equal(LucideIconNames.EyeOff, iconReveal.Icon);

                btnReveal.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Dispatcher.UIThread.RunJobs();
                Assert.Equal('•', secretInput.PasswordChar);
                Assert.Equal(LucideIconNames.Eye, iconReveal.Icon);

                confirmDlg = InputDialog.CreateDialogInstance("Enter passphrase", "Export Vault", string.Empty, big: false, isSecret: true, requireConfirmation: true);
                confirmDlg.Show();
                Dispatcher.UIThread.RunJobs();

                var txtInput = confirmDlg.FindControl<TextBox>("TxtInput")!;
                var txtConfirm = confirmDlg.FindControl<TextBox>("TxtConfirmInput")!;
                var txtError = confirmDlg.FindControl<TextBlock>("TxtValidationError")!;
                var btnOk = confirmDlg.FindControl<Button>("BtnOK")!;

                Assert.True(confirmDlg.FindControl<StackPanel>("PnlConfirmPassword")!.IsVisible);
                Assert.Equal('•', txtConfirm.PasswordChar);
                Assert.False(txtError.IsVisible);

                txtInput.Text = "SecretPass123";
                txtConfirm.Text = "DifferentPass";
                Dispatcher.UIThread.RunJobs();
                btnOk.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Dispatcher.UIThread.RunJobs();

                Assert.True(confirmDlg.IsVisible);
                Assert.True(txtError.IsVisible);
                Assert.Equal(LocalizationService.Get("InputDialog_PasswordMismatch"), txtError.Text);

                txtConfirm.Text = "SecretPass123";
                Dispatcher.UIThread.RunJobs();
                Assert.False(txtError.IsVisible);

                btnOk.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Dispatcher.UIThread.RunJobs();
                Assert.False(confirmDlg.IsVisible);
                Assert.Empty(sink.Errors);
            }
            finally
            {
                foreach (var dlg in new[] { plainDlg, secretDlg, confirmDlg })
                {
                    if (dlg != null)
                    {
                        try { dlg.Close(); }
                        catch (InvalidOperationException) { }
                    }
                }
                Dispatcher.UIThread.RunJobs();
            }
        }

        [Fact]
        public void SettingsSideEffectsService_VaultPickerOptions_ConfigureShiroVaultFilterAndDefaultExtension()
        {
            var saveOptions = SettingsSideEffectsService.CreateVaultSaveOptions("shiro-vault-test.shirovault", startLocation: null);
            Assert.Equal("shirovault", saveOptions.DefaultExtension);
            Assert.True(saveOptions.ShowOverwritePrompt);
            Assert.NotNull(saveOptions.FileTypeChoices);
            Assert.Contains("*.shirovault", saveOptions.FileTypeChoices![0].Patterns!);

            var openOptions = SettingsSideEffectsService.CreateVaultOpenOptions(startLocation: null);
            Assert.False(openOptions.AllowMultiple);
            Assert.NotNull(openOptions.FileTypeFilter);
            Assert.Contains("*.shirovault", openOptions.FileTypeFilter![0].Patterns!);
            Assert.Contains("*.bak", openOptions.FileTypeFilter![0].Patterns!);
        }

        [Fact]
        public async Task SettingsSideEffectsService_ImportVaultFromPathAsync_ImportsLocalVaultKeyBackupWithoutPrompt()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "shiro-bak-vault-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            ToastService.ClearAll();
            try
            {
                using var dataDirScope = StoragePaths.OverrideDataDirectoryForTesting(tempDir);
                var sourceAccounts = new List<Account>
                {
                    new() { Username = "LocalBackupUser", UserID = 8801, SecurityToken = "tok-bak", Valid = true }
                };
                byte[] vaultKey = StoragePaths.GetOrCreateVaultKeyCopy();
                byte[] plaintext = AccountDataSerializer.Serialize(sourceAccounts, includePasswords: true);
                byte[] encrypted = Cryptography.EncryptWithRandomKey(plaintext, vaultKey);
                string bakPath = Path.Combine(tempDir, "AccountData_20261124.json.bak");
                File.WriteAllBytes(bakPath, encrypted);

                var stored = new List<Account>();
                var repo = Substitute.For<IAccountRepository>();
                repo.GetAll().Returns(_ => stored.ToList());
                repo.When(r => r.AddThreadSafe(Arg.Any<Account>())).Do(ci => stored.Add(ci.Arg<Account>()));
                repo.SaveAsync(Arg.Any<bool>()).Returns(Task.CompletedTask);

                var dialogs = Substitute.For<IDialogService>();
                var host = Substitute.For<IHostUiBridgeService>();

                using var repoScope = AppServices.Override<IAccountRepository>(repo);
                using var dialogScope = AppServices.Override<IDialogService>(dialogs);
                using var hostScope = AppServices.Override<IHostUiBridgeService>(host);

                bool imported = await SettingsSideEffectsService.Default.ImportVaultFromPathAsync(bakPath);

                Assert.True(imported);
                Assert.Single(stored);
                Assert.Equal("LocalBackupUser", stored[0].Username);
                await dialogs.DidNotReceiveWithAnyArgs().ShowSecretPromptAsync(default!, default!, default!);
            }
            finally
            {
                ToastService.ClearAll();
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
        }

        [Fact]
        public async Task SettingsSideEffectsService_ImportVaultFromPathAsync_ImportsMasterPasswordBackupWithSecretPrompt()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "shiro-bak-mp-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            ToastService.ClearAll();
            try
            {
                using var dataDirScope = StoragePaths.OverrideDataDirectoryForTesting(tempDir);
                var sourceAccounts = new List<Account>
                {
                    new() { Username = "MasterBackupUser", UserID = 8802, SecurityToken = "tok-mp", Valid = true }
                };
                byte[] hash = SHA512.HashData("MasterPass#2026"u8);
                byte[] plaintext = AccountDataSerializer.Serialize(sourceAccounts, includePasswords: true);
                byte[] encrypted = Cryptography.Encrypt(plaintext, hash);
                string bakPath = Path.Combine(tempDir, "AccountData_20261124_mp.json.bak");
                File.WriteAllBytes(bakPath, encrypted);

                var stored = new List<Account>();
                var repo = Substitute.For<IAccountRepository>();
                repo.GetAll().Returns(_ => stored.ToList());
                repo.When(r => r.AddThreadSafe(Arg.Any<Account>())).Do(ci => stored.Add(ci.Arg<Account>()));
                repo.SaveAsync(Arg.Any<bool>()).Returns(Task.CompletedTask);

                var dialogs = Substitute.For<IDialogService>();
                dialogs.ShowSecretPromptAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
                    .Returns(Task.FromResult("MasterPass#2026"));
                var host = Substitute.For<IHostUiBridgeService>();

                using var repoScope = AppServices.Override<IAccountRepository>(repo);
                using var dialogScope = AppServices.Override<IDialogService>(dialogs);
                using var hostScope = AppServices.Override<IHostUiBridgeService>(host);

                bool imported = await SettingsSideEffectsService.Default.ImportVaultFromPathAsync(bakPath);

                Assert.True(imported);
                Assert.Single(stored);
                Assert.Equal("MasterBackupUser", stored[0].Username);
                await dialogs.Received(1).ShowSecretPromptAsync(
                    LocalizationService.Get("Settings.ImportBackupMasterPasswordPrompt"),
                    LocalizationService.Get("Settings.ImportVaultBtn"),
                    Arg.Any<string>());
            }
            finally
            {
                ToastService.ClearAll();
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
        }

        [Fact]
        public async Task SettingsSideEffectsService_ImportVaultFromPathAsync_RejectsInvalidFileWithoutPrompt()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "shiro-bak-bad-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                string badPath = Path.Combine(tempDir, "not-a-vault.bak");
                File.WriteAllBytes(badPath, "CORRUPT_HEADER_DATA_123456"u8.ToArray());

                var dialogs = Substitute.For<IDialogService>();
                using var dialogScope = AppServices.Override<IDialogService>(dialogs);

                await Assert.ThrowsAsync<InvalidDataException>(
                    () => SettingsSideEffectsService.Default.ImportVaultFromPathAsync(badPath));
                await dialogs.DidNotReceiveWithAnyArgs().ShowSecretPromptAsync(default!, default!, default!);
            }
            finally
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
        }

        [Fact]
        public async Task SettingsViewModel_ImportVaultCommand_ShowsLocalizedErrorsForWrongPasswordAndInvalidFile()
        {
            var store = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            var general = CreateInMemorySettings(store);
            using var settingsScope = AppServices.Override<ISettingsService>(general);

            var dialogs = Substitute.For<IDialogService>();
            using var dialogScope = AppServices.Override<IDialogService>(dialogs);

            var effects = Substitute.For<ISettingsSideEffects>();
            effects.ImportVaultAsync().Returns<Task<bool>>(_ => Task.FromException<bool>(new CryptographicException("bad tag")));

            using var vm = new SettingsViewModel(new AppSettings(), general, effects);
            await vm.ImportVaultCommand.ExecuteAsync(null);
            Assert.False(vm.IsBusy);
            Assert.Single(dialogs.ReceivedCalls());
            dialogs.Received(1).ShowError(
                LocalizationService.Get("Settings.ImportVaultWrongPassword"),
                LocalizationService.Get("Status_Error"));

            dialogs.ClearReceivedCalls();
            effects.ImportVaultAsync().Returns<Task<bool>>(_ => Task.FromException<bool>(new InvalidDataException("bad magic")));

            await vm.ImportVaultCommand.ExecuteAsync(null);
            Assert.False(vm.IsBusy);
            Assert.Single(dialogs.ReceivedCalls());
            dialogs.Received(1).ShowError(
                LocalizationService.Get("Settings.ImportVaultInvalidFile"),
                LocalizationService.Get("Status_Error"));
        }
    }
}

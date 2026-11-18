#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NSubstitute;
using Shiro.AvaloniaUI.Views;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public sealed class P0UiAndViewModelRegressionTests : IDisposable
    {
        private readonly Action<Action>? _originalDispatcher = AppDispatcher.CustomDispatcher;
        private readonly Func<string, Task<(bool Confirmed, string? Path)>>? _originalExportPathOverride =
            SettingsSideEffectsService.TestPickExportPathOverride;

        public void Dispose()
        {
            AppDispatcher.CustomDispatcher = _originalDispatcher;
            SettingsSideEffectsService.TestPickExportPathOverride = _originalExportPathOverride;
            ToastService.ClearAll();
        }

        [Fact]
        public async Task AppDispatcher_Headless_InvokeAsyncAction_ThrowsOnceAndExecutesOnce()
        {
            AppDispatcher.CustomDispatcher = null;
            int invocationCount = 0;

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                AppDispatcher.InvokeAsync(() =>
                {
                    invocationCount++;
                    throw new InvalidOperationException("boom");
                }));

            Assert.Equal("boom", ex.Message);
            Assert.Equal(1, invocationCount);
        }

        [Fact]
        public async Task AppDispatcher_Headless_InvokeAsyncFunc_ThrowsOnceAndExecutesOnce()
        {
            AppDispatcher.CustomDispatcher = null;
            int invocationCount = 0;

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                AppDispatcher.InvokeAsync<int>(() =>
                {
                    invocationCount++;
                    throw new InvalidOperationException("boom-func");
                }));

            Assert.Equal("boom-func", ex.Message);
            Assert.Equal(1, invocationCount);
        }

        [Fact]
        public void AppDispatcher_Headless_Post_ThrowsOnceAndExecutesOnce()
        {
            AppDispatcher.CustomDispatcher = null;
            int invocationCount = 0;

            var ex = Assert.Throws<InvalidOperationException>(() =>
                AppDispatcher.Post(() =>
                {
                    invocationCount++;
                    throw new InvalidOperationException("boom-post");
                }));

            Assert.Equal("boom-post", ex.Message);
            Assert.Equal(1, invocationCount);
        }

        [Fact]
        public void AppDispatcher_DispatchOrFallback_WhenDispatcherActiveAndDispatchThrows_DoesNotInvokeFallback()
        {
            int dispatchCount = 0;
            int fallbackCount = 0;

            AppDispatcher.DispatchOrFallback(
                hasActiveDispatcher: true,
                dispatch: () =>
                {
                    dispatchCount++;
                    throw new InvalidOperationException("dispatcher-boom");
                },
                fallback: () => fallbackCount++);

            Assert.Equal(1, dispatchCount);
            Assert.Equal(0, fallbackCount);
        }

        [Fact]
        public void AppDispatcher_DispatchOrFallback_WhenNoActiveDispatcher_InvokesFallbackOnce()
        {
            int dispatchCount = 0;
            int fallbackCount = 0;

            AppDispatcher.DispatchOrFallback(
                hasActiveDispatcher: false,
                dispatch: () => dispatchCount++,
                fallback: () => fallbackCount++);

            Assert.Equal(0, dispatchCount);
            Assert.Equal(1, fallbackCount);
        }

        [Fact]
        public async Task AppDispatcher_DispatchOrFallbackAsync_WhenDispatcherActiveAndDispatchThrows_PropagatesAndDoesNotInvokeFallback()
        {
            int dispatchCount = 0;
            int fallbackCount = 0;

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                AppDispatcher.DispatchOrFallbackAsync(
                    hasActiveDispatcher: true,
                    dispatch: () =>
                    {
                        dispatchCount++;
                        throw new InvalidOperationException("async-boom");
                    },
                    fallback: () =>
                    {
                        fallbackCount++;
                        return Task.CompletedTask;
                    }));

            Assert.Equal("async-boom", ex.Message);
            Assert.Equal(1, dispatchCount);
            Assert.Equal(0, fallbackCount);
        }

        [Fact]
        public async Task AppDispatcher_DispatchOrFallbackAsyncGeneric_WhenDispatcherActiveAndDispatchThrows_PropagatesAndDoesNotInvokeFallback()
        {
            int dispatchCount = 0;
            int fallbackCount = 0;

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                AppDispatcher.DispatchOrFallbackAsync<int>(
                    hasActiveDispatcher: true,
                    dispatch: () =>
                    {
                        dispatchCount++;
                        throw new InvalidOperationException("async-generic-boom");
                    },
                    fallback: () =>
                    {
                        fallbackCount++;
                        return Task.FromResult(42);
                    }));

            Assert.Equal("async-generic-boom", ex.Message);
            Assert.Equal(1, dispatchCount);
            Assert.Equal(0, fallbackCount);
        }

        [Fact]
        public async Task SettingsSideEffectsService_ExportVaultAsync_WhenPickerCancelled_ReturnsFalseCreatesNoFileAndShowsNoNotification()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "shiro-vault-cancel-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            ToastService.ClearAll();
            try
            {
                using var dataDirScope = StoragePaths.OverrideDataDirectoryForTesting(tempDir);
                var repo = Substitute.For<IAccountRepository>();
                repo.GetAll().Returns(new List<Account>
                {
                    new() { Username = "CancelExportUser", UserID = 1001, SecurityToken = "tok", Valid = true }
                });

                var dialogs = Substitute.For<IDialogService>();
                dialogs.ShowSecretPromptAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
                    .Returns(Task.FromResult("Passphrase#123"));

                using var repoScope = AppServices.Override<IAccountRepository>(repo);
                using var dialogScope = AppServices.Override<IDialogService>(dialogs);
                SettingsSideEffectsService.TestPickExportPathOverride =
                    _ => Task.FromResult<(bool Confirmed, string? Path)>((false, null));

                bool exported = await SettingsSideEffectsService.Default.ExportVaultAsync();

                Assert.False(exported);
                Assert.Empty(Directory.GetFiles(tempDir, "*.shirovault", SearchOption.AllDirectories));
                dialogs.DidNotReceiveWithAnyArgs().ShowInfo(default!, default!);
                Assert.Empty(ToastService.ActiveToasts);
            }
            finally
            {
                SettingsSideEffectsService.TestPickExportPathOverride = _originalExportPathOverride;
                ToastService.ClearAll();
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
        }

        [Fact]
        public async Task SettingsSideEffectsService_ExportAndImportVault_InvokeShowSecretPromptAsyncInsteadOfUnmaskedPrompt()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "shiro-vault-secret-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string exportPath = Path.Combine(tempDir, "picked.shirovault");
            ToastService.ClearAll();
            try
            {
                using var dataDirScope = StoragePaths.OverrideDataDirectoryForTesting(tempDir);
                var stored = new List<Account>
                {
                    new() { Username = "ExportSecretUser", UserID = 2002, SecurityToken = "tok", Valid = true }
                };
                var repo = Substitute.For<IAccountRepository>();
                repo.GetAll().Returns(_ => stored.ToList());
                repo.When(r => r.AddThreadSafe(Arg.Any<Account>()))
                    .Do(ci => stored.Add(ci.Arg<Account>()));
                repo.SaveAsync(Arg.Any<bool>()).Returns(Task.CompletedTask);

                var exportDialogs = Substitute.For<IDialogService>();
                exportDialogs.ShowSecretPromptAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
                    .Returns(Task.FromResult("SecretPass#456"));

                using var repoScope = AppServices.Override<IAccountRepository>(repo);
                using (AppServices.Override<IDialogService>(exportDialogs))
                {
                    SettingsSideEffectsService.TestPickExportPathOverride =
                        _ => Task.FromResult<(bool Confirmed, string? Path)>((true, exportPath));

                    bool exported = await SettingsSideEffectsService.Default.ExportVaultAsync();

                    Assert.True(exported);
                    Assert.True(File.Exists(exportPath));
                    await exportDialogs.Received(1).ShowSecretPromptAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
                    await exportDialogs.DidNotReceiveWithAnyArgs().ShowInputPromptAsync(default!, default!, default!);
                }

                stored.Clear();
                var importDialogs = Substitute.For<IDialogService>();
                importDialogs.ShowSecretPromptAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
                    .Returns(Task.FromResult("SecretPass#456"));
                var host = Substitute.For<IHostUiBridgeService>();

                using (AppServices.Override<IDialogService>(importDialogs))
                using (AppServices.Override<IHostUiBridgeService>(host))
                {
                    bool imported = await SettingsSideEffectsService.Default.ImportVaultFromPathAsync(exportPath);

                    Assert.True(imported);
                    Assert.Single(stored);
                    await importDialogs.Received(1).ShowSecretPromptAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
                    await importDialogs.DidNotReceiveWithAnyArgs().ShowInputPromptAsync(default!, default!, default!);
                }
            }
            finally
            {
                SettingsSideEffectsService.TestPickExportPathOverride = _originalExportPathOverride;
                ToastService.ClearAll();
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
        }

        [Fact]
        public async Task SettingsSideEffectsService_ExportVaultAsync_WhenHeadlessWithoutPicker_FallsBackToBackupsDirectory()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "shiro-vault-headless-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            ToastService.ClearAll();
            try
            {
                using var dataDirScope = StoragePaths.OverrideDataDirectoryForTesting(tempDir);
                var repo = Substitute.For<IAccountRepository>();
                repo.GetAll().Returns(new List<Account>
                {
                    new() { Username = "HeadlessExportUser", UserID = 3003, SecurityToken = "tok", Valid = true }
                });

                var dialogs = Substitute.For<IDialogService>();
                dialogs.ShowSecretPromptAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
                    .Returns(Task.FromResult("Passphrase#789"));

                using var repoScope = AppServices.Override<IAccountRepository>(repo);
                using var dialogScope = AppServices.Override<IDialogService>(dialogs);
                SettingsSideEffectsService.TestPickExportPathOverride = null;

                bool exported = await SettingsSideEffectsService.Default.ExportVaultAsync();

                Assert.True(exported);
                var backupFiles = Directory.GetFiles(StoragePaths.BackupsDirectory, "*.shirovault", SearchOption.TopDirectoryOnly);
                Assert.Single(backupFiles);
            }
            finally
            {
                SettingsSideEffectsService.TestPickExportPathOverride = _originalExportPathOverride;
                ToastService.ClearAll();
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
        }

        [Fact]
        public void InputDialog_ShouldMaskInput_ReturnsTrueForAnyLocalizedCaptionWhenIsSecretTrue()
        {
            Assert.True(InputDialog.ShouldMaskInput("Nhập mật khẩu kho...", isSecret: true));
            Assert.True(InputDialog.ShouldMaskInput("Export Vault", isSecret: true));
            Assert.True(InputDialog.ShouldMaskInput(null, isSecret: true));
            Assert.True(InputDialog.ShouldMaskInput("Enter Password", isSecret: false));
            Assert.False(InputDialog.ShouldMaskInput("Group Name", isSecret: false));
        }
    }
}

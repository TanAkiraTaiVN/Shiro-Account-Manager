#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using NSubstitute;
using Shiro.AvaloniaUI.Views;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public sealed class Phase3AsyncSafetyAndDialogTests : IDisposable
    {
        private readonly Action<Action>? _originalDispatcher;

        public Phase3AsyncSafetyAndDialogTests()
        {
            _originalDispatcher = AppDispatcher.CustomDispatcher;
        }

        public void Dispose()
        {
            AppDispatcher.CustomDispatcher = _originalDispatcher;
        }

        [Fact]
        public async Task AppDispatcher_PostTask_RunsAsyncBodyCleanly_AndObservesFaultsWithoutAsyncVoid()
        {
            int dispatchedCount = 0;
            AppDispatcher.CustomDispatcher = action =>
            {
                dispatchedCount++;
                action();
            };

            var ranTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            AppDispatcher.PostTask(async () =>
            {
                await Task.Yield();
                ranTcs.TrySetResult(true);
            }, "TestPostTask");

            bool ran = await ranTcs.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(ran);
            Assert.Equal(1, dispatchedCount);

            AppDispatcher.PostTask(null!);
            Assert.Equal(1, dispatchedCount);

            var loggerSub = Substitute.For<IAppLogger>();
            var loggedTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            loggerSub.When(l => l.Error(Arg.Any<object>()))
                .Do(ci => loggedTcs.TrySetResult(ci.Arg<object>()?.ToString() ?? string.Empty));
            using var loggerScope = AppServices.Override<IAppLogger>(loggerSub);

            AppDispatcher.PostTask(async () =>
            {
                await Task.Yield();
                throw new InvalidOperationException("BoomAsyncPostTask");
            }, "FaultScope");

            string loggedMessage = await loggedTcs.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Contains("[FaultScope]", loggedMessage, StringComparison.Ordinal);
            Assert.Contains("BoomAsyncPostTask", loggedMessage, StringComparison.Ordinal);
            Assert.Equal(2, dispatchedCount);
        }

        [Fact]
        public async Task AvaloniaDialogService_ConfirmAndAlertAsync_CheckIsAvaloniaUiReadyBeforeDispatcher()
        {
            Assert.False(MessageBoxDialog.IsAvaloniaUiReady());

            var svc = AvaloniaDialogService.Default;
            bool confirmResult = await Task.Run(() => svc.ConfirmAsync("Confirm action?", "Title"))
                .WaitAsync(TimeSpan.FromSeconds(3));
            await Task.Run(() => svc.AlertAsync("Alert body", "Title", PromptSeverity.Warning))
                .WaitAsync(TimeSpan.FromSeconds(3));

            Assert.False(confirmResult);
        }

        [AvaloniaFact]
        public async Task AvaloniaDialogService_SignInFastLoginAsync_CancellationClosesOpenDialog()
        {
            var owner = new Window { Width = 320, Height = 200 };
            owner.Show();
            Dispatcher.UIThread.RunJobs();

            using var ownerScope = AvaloniaDialogService.OverrideOwnerForTest(owner);
            try
            {
                using var preCanceledCts = new CancellationTokenSource();
                preCanceledCts.Cancel();
                Task<Account?> preCanceledTask = AvaloniaDialogService.Default.SignInFastLoginAsync(
                    new Account { Username = "PreCancelUser", UserID = 1001 },
                    preCanceledCts.Token);
                Dispatcher.UIThread.RunJobs();
                Assert.True(preCanceledTask.IsCanceled);
                Assert.Empty(owner.OwnedWindows);

                using var cts = new CancellationTokenSource();
                Task<Account?> task = AvaloniaDialogService.Default.SignInFastLoginAsync(
                    new Account { Username = "MidCancelUser", UserID = 1002 },
                    cts.Token);

                Dispatcher.UIThread.RunJobs();
                Assert.False(task.IsCompleted);
                var openDialog = Assert.Single(owner.OwnedWindows.OfType<FastLoginDialog>());
                Assert.True(openDialog.IsVisible);

                cts.Cancel();
                Dispatcher.UIThread.RunJobs();

                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(3)));
                Dispatcher.UIThread.RunJobs();
                Assert.Empty(owner.OwnedWindows.OfType<FastLoginDialog>());
                Assert.False(openDialog.IsVisible);
            }
            finally
            {
                foreach (var child in owner.OwnedWindows.ToList())
                {
                    child.Close();
                }
                owner.Close();
                Dispatcher.UIThread.RunJobs();
            }
        }

        [Fact]
        public async Task MainWindow_UnlockMasterPassword_AllowsRetryBeforeRecovery_AndZerosPasswordBytes()
        {
            byte[] expectedHash = SHA512.HashData(Encoding.UTF8.GetBytes("SecretPass!"));
            var observedClearedBuffers = new List<(byte[] PassBytes, byte[] HashBytes)>();

            // Case 1: 2 wrong attempts, 3rd succeeds
            var prompts1 = new Queue<string>(new[] { "wrong1", "wrong2", "SecretPass!" });
            int promptCount1 = 0;
            var toasts1 = new List<(string Title, string Message)>();
            bool unlocked1 = await MainWindow.TryPromptAndUnlockMasterPasswordAsync(
                () =>
                {
                    promptCount1++;
                    return Task.FromResult(prompts1.Dequeue());
                },
                hash => hash.SequenceEqual(expectedHash),
                (title, msg) => toasts1.Add((title, msg)),
                (passBytes, hashBytes) => observedClearedBuffers.Add((passBytes, hashBytes)));

            Assert.True(unlocked1);
            Assert.Equal(3, promptCount1);
            Assert.Equal(2, toasts1.Count);
            Assert.Equal(3, observedClearedBuffers.Count);
            foreach (var (passBytes, hashBytes) in observedClearedBuffers)
            {
                Assert.NotEmpty(passBytes);
                Assert.Equal(64, hashBytes.Length);
                Assert.All(passBytes, b => Assert.Equal(0, b));
                Assert.All(hashBytes, b => Assert.Equal(0, b));
            }

            // Case 2: 3 wrong attempts fail and trigger fallback
            observedClearedBuffers.Clear();
            var prompts2 = new Queue<string>(new[] { "bad1", "bad2", "bad3" });
            int promptCount2 = 0;
            var toasts2 = new List<(string Title, string Message)>();
            bool unlocked2 = await MainWindow.TryPromptAndUnlockMasterPasswordAsync(
                () =>
                {
                    promptCount2++;
                    return Task.FromResult(prompts2.Dequeue());
                },
                _ => false,
                (title, msg) => toasts2.Add((title, msg)),
                (passBytes, hashBytes) => observedClearedBuffers.Add((passBytes, hashBytes)));

            Assert.False(unlocked2);
            Assert.Equal(MainWindow.MaxMasterPasswordAttempts, promptCount2);
            Assert.Equal(3, toasts2.Count);
            Assert.Equal(3, observedClearedBuffers.Count);

            // Case 3: Cancel ("/UC") on attempt 2 aborts retry loop immediately
            var prompts3 = new Queue<string>(new[] { "bad1", "/UC" });
            int promptCount3 = 0;
            var toasts3 = new List<(string Title, string Message)>();
            bool unlocked3 = await MainWindow.TryPromptAndUnlockMasterPasswordAsync(
                () =>
                {
                    promptCount3++;
                    return Task.FromResult(prompts3.Dequeue());
                },
                _ => false,
                (title, msg) => toasts3.Add((title, msg)));

            Assert.False(unlocked3);
            Assert.Equal(2, promptCount3);
            Assert.Single(toasts3);

            // Case 4: Exception during LoadFromStore still zeroes both buffers in finally
            byte[]? thrownPassBytes = null;
            byte[]? thrownHashBytes = null;
            Assert.Throws<InvalidOperationException>(() =>
                MainWindow.TryUnlockWithPasswordCore(
                    "ThrowSecret99!",
                    _ => throw new InvalidOperationException("Simulated decrypt failure"),
                    (p, h) =>
                    {
                        thrownPassBytes = p;
                        thrownHashBytes = h;
                    }));

            Assert.NotNull(thrownPassBytes);
            Assert.NotNull(thrownHashBytes);
            Assert.All(thrownPassBytes!, b => Assert.Equal(0, b));
            Assert.All(thrownHashBytes!, b => Assert.Equal(0, b));
        }
    }
}

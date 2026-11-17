#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Shiro.AvaloniaUI.Views;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests.Ui
{
    [Collection("SerialGlobalState")]
    public class BugHuntUiRegressionTests
    {
        private sealed class BlockingHttpHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _responder;

            public BlockingHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
            {
                _responder = responder;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                _responder(request, cancellationToken);
        }

        private static readonly byte[] OneByOnePngBytes = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+ip1sAAAAASUVORK5CYII=");

        [AvaloniaFact]
        public void MessageBoxDialog_Show_WithOwnedWindowAndNullSyncContext_DoesNotThrow()
        {
            var prevCtx = SynchronizationContext.Current;
            var owner = new Window { Width = 300, Height = 200 };

            try
            {
                owner.Show();
                Dispatcher.UIThread.RunJobs();

                int attempts = 0;
                void CloseOwnedSoon()
                {
                    var owned = owner.OwnedWindows.ToList();
                    if (owned.Count > 0)
                    {
                        foreach (var child in owned)
                            child.Close();
                        return;
                    }
                    if (++attempts < 20)
                        Dispatcher.UIThread.Post(CloseOwnedSoon, DispatcherPriority.Background);
                }

                Dispatcher.UIThread.Post(CloseOwnedSoon, DispatcherPriority.Background);
                SynchronizationContext.SetSynchronizationContext(null);

                var result = MessageBoxDialog.Show("Null sync context test", "Test", MessageBoxButtons.OK, MessageBoxIcon.Information, owner);
                Assert.Equal(MessageBoxResult.None, result);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(prevCtx);
                owner.Close();
            }
        }

        [AvaloniaFact]
        public void InputDialog_Prompt_WithOwnedWindowAndNullSyncContext_DoesNotThrow()
        {
            var prevCtx = SynchronizationContext.Current;
            var owner = new Window { Width = 300, Height = 200 };

            try
            {
                owner.Show();
                Dispatcher.UIThread.RunJobs();

                int attempts = 0;
                void CloseOwnedSoon()
                {
                    var owned = owner.OwnedWindows.ToList();
                    if (owned.Count > 0)
                    {
                        foreach (var child in owned)
                            child.Close();
                        return;
                    }
                    if (++attempts < 20)
                        Dispatcher.UIThread.Post(CloseOwnedSoon, DispatcherPriority.Background);
                }

                Dispatcher.UIThread.Post(CloseOwnedSoon, DispatcherPriority.Background);
                SynchronizationContext.SetSynchronizationContext(null);

                string value = InputDialog.Prompt("Enter text", "Test", "default", big: false, owner: owner);
                Assert.Equal("/UC", value);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(prevCtx);
                owner.Close();
            }
        }

        [AvaloniaFact]
        public async Task GameThumbnailService_GetThumbnailBitmapAsync_WhenCallerCancelsEarly_CleansUpInFlightAndRetriesNextCall()
        {
            const long placeId = 987654321;
            GameThumbnailService.ClearCache();
            string? diskPath = GameThumbnailService.GetDiskCachePath(placeId);
            if (!string.IsNullOrEmpty(diskPath) && File.Exists(diskPath))
                File.Delete(diskPath);

            int callCount = 0;
            var firstRequestStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirstRequest = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            try
            {
                SharedHttpClient.SetTestClient("default", new HttpClient(new BlockingHttpHandler(async (_, _) =>
                {
                    int attempt = Interlocked.Increment(ref callCount);
                    if (attempt == 1)
                    {
                        firstRequestStarted.TrySetResult(true);
                        await releaseFirstRequest.Task.ConfigureAwait(false);
                        return new HttpResponseMessage(HttpStatusCode.NotFound);
                    }

                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(OneByOnePngBytes)
                    };
                })));

                using var cts = new CancellationTokenSource();
                var canceledTask = GameThumbnailService.GetThumbnailBitmapAsync(
                    placeId,
                    "https://cdn.example/thumb.png",
                    cts.Token);

                await firstRequestStarted.Task;
                cts.Cancel();

                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await canceledTask);

                // Unblock the underlying download so it completes with null after caller cancellation
                releaseFirstRequest.TrySetResult(true);
                await Task.Delay(50);

                var recoveredBitmap = await GameThumbnailService.GetThumbnailBitmapAsync(
                    placeId,
                    "https://cdn.example/thumb.png");

                Assert.NotNull(recoveredBitmap);
                Assert.Equal(2, callCount);
            }
            finally
            {
                releaseFirstRequest.TrySetResult(true);
                SharedHttpClient.ResetTestClient("default");
                GameThumbnailService.ClearCache();
                if (!string.IsNullOrEmpty(diskPath) && File.Exists(diskPath))
                    File.Delete(diskPath);
            }
        }

        [Fact]
        public void FastLoginDialog_AutomationAndSessionCapture_EnforcePasswordAndHandlerCleanupInvariants()
        {
            string viewsDir = Path.Combine(ArchitectureTests.RepoRoot, "src", "Shiro", "AvaloniaUI", "Views");
            string automationSource = File.ReadAllText(Path.Combine(viewsDir, "FastLoginDialog.Automation.cs"));
            string sessionCaptureSource = File.ReadAllText(Path.Combine(viewsDir, "FastLoginDialog.SessionCapture.cs"));

            int captureIdx = automationSource.IndexOf("CaptureSettledCookieAndDomPasswordAsync", StringComparison.Ordinal);
            Assert.True(captureIdx >= 0);
            int captureEndIdx = automationSource.IndexOf("private ", captureIdx + 10, StringComparison.Ordinal);
            string captureBody = captureEndIdx > captureIdx
                ? automationSource.Substring(captureIdx, captureEndIdx - captureIdx)
                : automationSource.Substring(captureIdx);
            Assert.DoesNotContain("currAcc.Password", captureBody, StringComparison.Ordinal);

            int advIdx = sessionCaptureSource.IndexOf("AdvanceToNextAccountOrCloseAsync", StringComparison.Ordinal);
            Assert.True(advIdx >= 0);
            int advEndIdx = sessionCaptureSource.IndexOf("LoadAccountAtIndex", advIdx, StringComparison.Ordinal);
            string advDetachSlice = sessionCaptureSource.Substring(advIdx, advEndIdx - advIdx);
            Assert.True(
                advDetachSlice.Contains("WebMessageReceived -= OnWebMessageReceived", StringComparison.Ordinal) ||
                advDetachSlice.Contains("DetachCoreWebViewHandlers", StringComparison.Ordinal),
                "AdvanceToNextAccountOrCloseAsync must detach WebMessageReceived along with navigation handlers.");
        }
    }
}
